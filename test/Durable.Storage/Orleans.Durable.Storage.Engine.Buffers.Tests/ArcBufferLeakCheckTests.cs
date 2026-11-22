using Orleans.Durable.Storage.Testing;
using Xunit.Sdk;

namespace Orleans.Durable.Storage.Engine.Buffers.Tests;

/// <summary>
/// Tests of the page accounting behind the leak checks (BUF-I1, BUF-I2; docs/spec/verification.md §11.1): a page is
/// attributed to the accounting current where it is rented and returns to it wherever it is released.
/// </summary>
public sealed class ArcBufferLeakCheckTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// A check started in a field initializer counts the pages the test method rents, so a test class can own one.
    /// </summary>
    [Fact]
    public void AFieldInitializerCheckCountsTheTestsPages()
    {
        using (var writer = new ArcBufferWriter())
        {
            writer.Write(new byte[3 * ArcBufferWriter.MinimumPageSize]);
            Assert.Equal(3, _leaks.Outstanding);
        }

        Assert.Equal(3, _leaks.Rented);
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// A retained slice keeps its pages outstanding after the writer is disposed, until the slice is released
    /// (BUF-I2); releasing every alias returns them once (BUF-I1).
    /// </summary>
    [Fact]
    public void ARetainedSliceIsOutstandingUntilItIsReleased()
    {
        ArcBuffer slice;
        using (var writer = new ArcBufferWriter())
        {
            writer.Write(new byte[2 * ArcBufferWriter.MinimumPageSize]);
            slice = writer.PeekSlice(writer.Length);
        }

        Assert.Equal(2, _leaks.Outstanding);
        Assert.Throws<Xunit.Sdk.FailException>(_leaks.AssertNoLeaks);
        var alias = slice;
        slice.Dispose();
        alias.Dispose();
        Assert.Equal(0, _leaks.Outstanding);
        _leaks.AssertNoLeaks();
    }

    /// <summary>
    /// Work started from the test inherits its check, and a page returns to the accounting it was rented under
    /// whichever thread releases it.
    /// </summary>
    [Fact]
    public async Task BackgroundWorkInheritsTheCheckAndAnyThreadMayRelease()
    {
        var slice = await Task.Run(static () =>
        {
            using var writer = new ArcBufferWriter();
            writer.Write([1, 2, 3]);
            return writer.ConsumeSlice(3);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, _leaks.Outstanding);
        await Task.Run(slice.Dispose, TestContext.Current.CancellationToken);
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// Pages rented outside the check never count, even when they are released under it, and work which does not
    /// flow the execution context enters the check through <see cref="ArcBufferLeakCheck.Run{T}"/>.
    /// </summary>
    [Fact]
    public async Task OnlyPagesRentedUnderTheCheckCount()
    {
        Task<ArcBuffer> outside;
        using (ExecutionContext.SuppressFlow())
        {
            outside = Task.Run(static () => ArcBuffer.Wrap(new byte[] { 1 }), TestContext.Current.CancellationToken);
        }

        using (await outside)
        {
            Assert.Equal(0, _leaks.Rented);
        }

        Assert.Equal(0, _leaks.Rented);
        Task<ArcBuffer> entered;
        using (ExecutionContext.SuppressFlow())
        {
            entered = Task.Run(() => ArcBufferLeakCheck.Run(_leaks, static () => ArcBuffer.Wrap(new byte[] { 2 })), TestContext.Current.CancellationToken);
        }

        var buffer = await entered;
        Assert.Equal(1, _leaks.Rented);
        Assert.Equal(1, _leaks.Outstanding);
        buffer.Dispose();
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// Wrapping an array creates a lease which must be released like a rented page (BUF-I1).
    /// </summary>
    [Fact]
    public void AWrappedArrayIsOutstandingUntilItsLeaseIsReleased()
    {
        var wrapped = ArcBuffer.Wrap(new byte[] { 1, 2, 3 });
        using var retained = wrapped.Retain();
        wrapped.Dispose();
        Assert.Equal(1, _leaks.Outstanding);
        retained.Dispose();
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// A traced check reports each outstanding rental's call site, with source lines, grouped by call site, and forgets
    /// a page once it returns; an untraced check names the variable which turns tracing on.
    /// </summary>
    [Fact]
    public void ATracedCheckReportsWhereOutstandingPagesWereRented()
    {
        var kept = new List<ArcBuffer>();
        var traced = ArcBufferLeakCheck.Start(tracesRentals: true);
        try
        {
            Assert.True(traced.TracesRentals);
            for (var rental = 0; rental < 3; rental++)
            {
                kept.Add(Rent());
            }

            var report = Assert.Throws<FailException>(traced.AssertNoLeaks).Message;
            Assert.Contains("3 rented at:", report, StringComparison.Ordinal);
            Assert.Contains($"{typeof(ArcBufferLeakCheckTests).FullName}.{nameof(Rent)} in ", report, StringComparison.Ordinal);
            Assert.Contains($"{nameof(ArcBufferLeakCheckTests)}.cs:line ", report, StringComparison.Ordinal);
            Assert.DoesNotContain(ArcBufferLeakCheck.TraceVariable, report, StringComparison.Ordinal);
            Assert.DoesNotContain("Xunit.", report, StringComparison.Ordinal);
            kept.ForEach(static buffer => buffer.Dispose());
            traced.AssertNoLeaks();
        }
        finally
        {
            traced.Dispose();
        }

        using var untraced = ArcBufferLeakCheck.Start(tracesRentals: false);
        using var outstanding = Rent();
        Assert.False(untraced.TracesRentals);
        Assert.Contains(ArcBufferLeakCheck.TraceVariable, Assert.Throws<FailException>(untraced.AssertNoLeaks).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scope counts the pages rented within it, and so does the check which opened it; disposing the scope restores
    /// that check without asserting, and pages released after the scope ends still return to both.
    /// </summary>
    [Fact]
    public void AScopeCountsItsOwnPagesAndRestoresItsParent()
    {
        ArcBuffer kept;
        var scope = _leaks.Scope();
        try
        {
            Assert.Same(scope, ArcBufferPageAccounting.Current);
            kept = Rent();
            Assert.Equal(1, scope.Outstanding);
            Assert.Equal(1, _leaks.Outstanding);
            _ = Assert.Throws<FailException>(scope.AssertNoLeaks);
        }
        finally
        {
            scope.Dispose();
        }

        Assert.Same(_leaks, ArcBufferPageAccounting.Current);
        using (Rent())
        {
            Assert.Equal(1, scope.Rented);
            Assert.Equal(2, _leaks.Outstanding);
        }

        kept.Dispose();
        Assert.Equal(0, scope.Outstanding);
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// <see cref="ArcBufferLeakCheck.Run{T}"/> makes its accounting current for its body and restores the accounting it
    /// replaced, whether the body returns or throws.
    /// </summary>
    [Fact]
    public void RunRestoresTheAccountingItReplaced()
    {
        using var inner = ArcBufferLeakCheck.Start();
        var rented = ArcBufferLeakCheck.Run(_leaks, () =>
        {
            Assert.Same(_leaks, ArcBufferPageAccounting.Current);
            Rent().Dispose();
            return _leaks.Rented;
        });

        Assert.Equal(1, rented);
        Assert.Equal(0, inner.Rented);
        Assert.Same(inner, ArcBufferPageAccounting.Current);

        _ = Assert.Throws<InvalidOperationException>(() => ArcBufferLeakCheck.Run<int>(_leaks, static () => throw new InvalidOperationException()));
        Assert.Same(inner, ArcBufferPageAccounting.Current);
    }

    /// <summary>
    /// A property case run in its own scope fails when it keeps a page and passes when it releases every page; a case
    /// which throws reports its own failure; each restores the check which opened the scope.
    /// </summary>
    [Fact]
    public async Task InScopeFailsACaseWhichKeepsAPageAndRestoresTheCheck()
    {
        var kept = new List<ArcBuffer>();
        _leaks.InScope(static () => Rent().Dispose());
        Assert.Same(_leaks, ArcBufferPageAccounting.Current);

        var leak = Assert.Throws<FailException>(() => _leaks.InScope(() => kept.Add(Rent())));
        Assert.Contains("1 of the 1 ArcBuffer pages", leak.Message, StringComparison.Ordinal);
        Assert.Same(_leaks, ArcBufferPageAccounting.Current);

        _ = Assert.Throws<InvalidOperationException>(() => _leaks.InScope(() =>
        {
            kept.Add(Rent());
            throw new InvalidOperationException();
        }));
        Assert.Same(_leaks, ArcBufferPageAccounting.Current);

        await _leaks.InScopeAsync(static async () =>
        {
            await Task.Yield();
            Rent().Dispose();
        });
        _ = await Assert.ThrowsAsync<FailException>(() => _leaks.InScopeAsync(async () =>
        {
            await Task.Yield();
            kept.Add(Rent());
        }));

        Assert.Equal(3, _leaks.Outstanding);
        kept.ForEach(static buffer => buffer.Dispose());
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// A check started inside another counts the pages rented while it is current, and disposing it restores the check
    /// it replaced.
    /// </summary>
    [Fact]
    public void ANestedCheckRestoresTheCheckItReplaced()
    {
        using (var inner = ArcBufferLeakCheck.Start())
        {
            Assert.Same(inner, ArcBufferPageAccounting.Current);
            Rent().Dispose();
            Assert.Equal(1, inner.Rented);
        }

        Assert.Same(_leaks, ArcBufferPageAccounting.Current);
        Assert.Equal(0, _leaks.Rented);
        Rent().Dispose();
        Assert.Equal(1, _leaks.Rented);
    }

    private static ArcBuffer Rent()
    {
        using var writer = new ArcBufferWriter();
        writer.Write([1]);
        return writer.ConsumeSlice(1);
    }
}
