using System.Buffers;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The conditional register contract (metastore.md §2.2, §5), run against every MetaStore provider. Every case disposes
/// each entry it reads and each buffer it rents, so a test class which holds a page leak check runs it under the check.
/// </summary>
internal static class ConditionalRegisterContract
{
    private static readonly byte[] Previous = "previous"u8.ToArray();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task EmptyStoreReturnsNull(IConditionalRegister register)
    {
        Assert.Null(await register.ReadAsync(Token));
    }

    internal static async Task NullVersionCreatesOnlyWhenAbsent(IConditionalRegister register)
    {
        Assert.True((await register.WriteAsync(null, "first"u8.ToArray(), Token)).IsApplied());
        Assert.False((await register.WriteAsync(null, "second"u8.ToArray(), Token)).IsApplied());

        using var entry = await ReadPresentAsync(register);
        Assert.Equal("first"u8.ToArray(), entry.Value.ToArray());
    }

    /// <summary>
    /// MS-I2: an empty value round-trips as present and empty, both when it creates the register and when it replaces a
    /// value, and its entry owns no page.
    /// </summary>
    internal static async Task EmptyValueReadsAsPresent(IConditionalRegister register)
    {
        var created = (await register.WriteAsync(null, ReadOnlySequence<byte>.Empty, Token)).AppliedVersion();

        using (var entry = await ReadPresentAsync(register))
        {
            Assert.Equal(0, entry.Value.Length);
            Assert.Equal(created, entry.Version);
        }

        Assert.False((await register.WriteAsync(null, "second"u8.ToArray(), Token)).IsApplied());
        var full = (await register.WriteAsync(created, [1], Token)).AppliedVersion();
        _ = (await register.WriteAsync(full, ReadOnlySequence<byte>.Empty, Token)).AppliedVersion();
        using var emptied = await ReadPresentAsync(register);
        Assert.Equal(0, emptied.Value.Length);
    }

    internal static async Task NonNullVersionDoesNotCreateMissingRegister(IConditionalRegister register)
    {
        Assert.False(
            (await register.WriteAsync(new MetaStoreVersion("not-present"), "value"u8.ToArray(), Token)).IsApplied());
        Assert.Null(await register.ReadAsync(Token));
    }

    internal static async Task MatchingVersionUpdatesAndChangesVersion(IConditionalRegister register)
    {
        Assert.True((await register.WriteAsync(null, "first"u8.ToArray(), Token)).IsApplied());
        using var first = await ReadPresentAsync(register);

        Assert.True((await register.WriteAsync(first.Version, "second"u8.ToArray(), Token)).IsApplied());

        using var second = await ReadPresentAsync(register);
        Assert.NotEqual(first.Version, second.Version);
        Assert.Equal("second"u8.ToArray(), second.Value.ToArray());
    }

    /// <summary>
    /// The version an applied write returns is the register's version after the write: a read returns it with the
    /// write's value, a write naming it applies, and once a later write has applied, a write naming it mismatches
    /// (MS-I1, MS-I5).
    /// </summary>
    internal static async Task AnAppliedWriteReturnsTheRegistersNewVersion(IConditionalRegister register)
    {
        var created = (await register.WriteAsync(null, "first"u8.ToArray(), Token)).AppliedVersion();
        using (var first = await ReadPresentAsync(register))
        {
            Assert.Equal(created, first.Version);
            Assert.Equal("first"u8.ToArray(), first.Value.ToArray());
        }

        var replaced = (await register.WriteAsync(created, "second"u8.ToArray(), Token)).AppliedVersion();
        Assert.NotEqual(created, replaced);
        using (var second = await ReadPresentAsync(register))
        {
            Assert.Equal(replaced, second.Version);
            Assert.Equal("second"u8.ToArray(), second.Value.ToArray());
        }

        Assert.False((await register.WriteAsync(created, "stale"u8.ToArray(), Token)).IsApplied());
        var third = (await register.WriteAsync(replaced, "third"u8.ToArray(), Token)).AppliedVersion();
        using var current = await ReadPresentAsync(register);
        Assert.Equal(third, current.Version);
        Assert.Equal("third"u8.ToArray(), current.Value.ToArray());
    }

    internal static async Task StaleVersionDoesNotOverwriteValue(IConditionalRegister register)
    {
        Assert.True((await register.WriteAsync(null, "first"u8.ToArray(), Token)).IsApplied());
        using var first = await ReadPresentAsync(register);
        Assert.True((await register.WriteAsync(first.Version, "second"u8.ToArray(), Token)).IsApplied());

        Assert.False((await register.WriteAsync(first.Version, "stale"u8.ToArray(), Token)).IsApplied());

        using var current = await ReadPresentAsync(register);
        Assert.Equal("second"u8.ToArray(), current.Value.ToArray());
    }

    /// <summary>
    /// A write reads its value only before it completes, so a caller's later change to its buffer, or its release of the
    /// page or pages a value spans, changes no register; an entry keeps its value through later writes (metastore.md §4).
    /// </summary>
    internal static async Task ValuesAreCopiedOnWriteAndEntriesKeepTheirValues(IConditionalRegister register)
    {
        byte[] input = [1, 2, 3];
        var version = (await register.WriteAsync(null, input, Token)).AppliedVersion();
        input[0] = 9;

        using var first = await ReadPresentAsync(register);
        Assert.Equal([1, 2, 3], first.Value.ToArray());

        foreach (var pages in (int[])[1, 3])
        {
            var value = Value(0x30, pages);
            using (var buffer = Rent(value, pages))
            {
                version = (await register.WriteAsync(version, buffer.AsReadOnlySequence(), Token)).AppliedVersion();
            }

            using var current = await ReadPresentAsync(register);
            Assert.Equal(value, current.Value.ToArray());
        }

        Assert.Equal([1, 2, 3], first.Value.ToArray());
    }

    internal static async Task ConcurrentCreatesHaveOneWinner(IConditionalRegister register)
    {
        var attempts = Enumerable.Range(0, 32)
            .Select(index => register.WriteAsync(null, BitConverter.GetBytes(index), Token).AsTask())
            .ToArray();

        var results = await Task.WhenAll(attempts);

        await AssertTheWinnerHoldsTheRegisterAsync(register, results);
    }

    internal static async Task ConcurrentWritesWithOneVersionHaveOneWinner(IConditionalRegister register)
    {
        Assert.True((await register.WriteAsync(null, "initial"u8.ToArray(), Token)).IsApplied());
        using var initial = await ReadPresentAsync(register);
        var attempts = Enumerable.Range(0, 32)
            .Select(index => register.WriteAsync(initial.Version, BitConverter.GetBytes(index), Token).AsTask())
            .ToArray();

        var results = await Task.WhenAll(attempts);

        await AssertTheWinnerHoldsTheRegisterAsync(register, results);
    }

    internal static async Task CancellationIsObserved(IConditionalRegister register)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => _ = await register.ReadAsync(cancellation.Token));
        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => _ = await register.WriteAsync(null, ReadOnlySequence<byte>.Empty, cancellation.Token));
    }

    /// <summary>
    /// A write whose caller's wait is cancelled while its request is held mid-call, and whose value's pages the caller
    /// then releases and rents again, leaves the register holding exactly the value written, when the held request
    /// lands, or exactly the previous one, when it never does: a write copies its value before it dispatches the request,
    /// so a request which lands after its caller stopped waiting never carries the pages' later bytes, nor the poison they
    /// hold between owners (conventions.md §1). Only Debug builds poison a returned page, so the Debug leg's run is this
    /// case's evidence; a Release run sees the pages' later bytes instead.
    /// </summary>
    /// <param name="register">A register of a backend double which can hold a write.</param>
    /// <param name="holdNextWrite">Makes the double hold the register's next write through the given hold.</param>
    /// <param name="pages">The number of pages the value spans: one, or several, so a provider which wraps a single
    /// ledger rather than copying it fails too.</param>
    /// <param name="lands">Whether the held request lands once released.</param>
    internal static async Task AWriteCancelledMidCallLandsItsOwnCopy(
        IConditionalRegister register,
        Action<WriteHold> holdNextWrite,
        int pages,
        bool lands)
    {
        var previous = (await register.WriteAsync(null, Previous, Token)).AppliedVersion();
        var written = Value(0x5a, pages);
        var buffer = Rent(written, pages);
        var hold = new WriteHold();
        holdNextWrite(hold);

        using (var cancellation = new CancellationTokenSource())
        {
            var write = register.WriteAsync(previous, buffer.AsReadOnlySequence(), cancellation.Token).AsTask();
            await hold.Held.WaitAsync(Token);
            await cancellation.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        }

        // The write's task has completed, so the pages are the caller's again: it releases them, which poisons them in
        // Debug builds, and rents them again for other bytes.
        buffer.Dispose();
        using (Rent(Value(0xa5, pages), pages))
        {
            hold.Release();
            await hold.Settled.WaitAsync(Token);
        }

        using var entry = await ReadPresentAsync(register);
        Assert.Equal(lands ? written : Previous, entry.Value.ToArray());
    }

    private static async Task<MetaStoreEntry> ReadPresentAsync(IConditionalRegister register) =>
        Assert.IsType<MetaStoreEntry>(await register.ReadAsync(Token));

    /// <summary>
    /// A backend's conflict answer which reaches the register after its caller's token is cancelled is no mismatch:
    /// the write throws, its outcome unknown to the caller, and never answers <see cref="RegisterWriteOutcome.Mismatch"/>
    /// (metastore.md §6).
    /// </summary>
    /// <param name="register">A register of a backend double which can hold a write and answer it with a conflict.</param>
    /// <param name="conflictAfterCancellation">Makes the double hold the register's next write through the given hold
    /// until the caller's token is cancelled, and then answer it with the backend's conflict.</param>
    internal static async Task AConflictAnsweredAfterCancellationThrows(
        IConditionalRegister register,
        Action<WriteHold> conflictAfterCancellation)
    {
        var previous = (await register.WriteAsync(null, Previous, Token)).AppliedVersion();
        var hold = new WriteHold();
        conflictAfterCancellation(hold);
        Exception? thrown;
        using (var cancellation = new CancellationTokenSource())
        {
            var write = register.WriteAsync(previous, "conflicted"u8.ToArray(), cancellation.Token).AsTask();
            await hold.Held.WaitAsync(Token);
            await cancellation.CancelAsync();
            thrown = await Record.ExceptionAsync(() => write);
        }

        hold.Release();
        Assert.NotNull(thrown);
        using var entry = await ReadPresentAsync(register);
        Assert.Equal(Previous, entry.Value.ToArray());
    }

    /// <summary>
    /// Of writes of <c>BitConverter.GetBytes(index)</c> racing on one version, exactly one applied, and the register holds
    /// its value at the version it returned, which a later write names.
    /// </summary>
    private static async Task AssertTheWinnerHoldsTheRegisterAsync(IConditionalRegister register, RegisterWriteOutcome[] results)
    {
        var winner = Assert.Single(results, static result => result.IsApplied());
        var version = winner.AppliedVersion();
        using (var entry = await ReadPresentAsync(register))
        {
            Assert.Equal(version, entry.Version);
            Assert.Equal(BitConverter.GetBytes(Array.IndexOf(results, winner)), entry.Value.ToArray());
        }

        Assert.True((await register.WriteAsync(version, "next"u8.ToArray(), Token)).IsApplied());
    }

    /// <summary>
    /// Returns a value which fills half a page of the smallest class, for one page, or spans <paramref name="pages"/>,
    /// its bytes counting up from <paramref name="first"/>.
    /// </summary>
    private static byte[] Value(byte first, int pages) =>
        [.. Enumerable.Range(first, pages == 1 ? ArcBufferWriter.MinimumPageSize / 2 : pages * ArcBufferWriter.MinimumPageSize)
            .Select(static index => unchecked((byte)index))];

    /// <summary>
    /// Returns <paramref name="bytes"/> in pooled pages of the smallest class, which it requires to span
    /// <paramref name="pages"/> ledgers.
    /// </summary>
    private static ArcBuffer Rent(byte[] bytes, int pages)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        var buffer = writer.ConsumeSlice(writer.Length);
        Assert.Equal(pages == 1, buffer.AsReadOnlySequence().IsSingleSegment);
        return buffer;
    }
}

/// <summary>
/// Holds one write in a backend double once its request is dispatched, as a request in flight is held, until the test
/// releases it to land.
/// </summary>
internal sealed class WriteHold
{
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets a task which completes once the double holds the write.
    /// </summary>
    internal Task Held => _held.Task;

    /// <summary>
    /// Gets a task which completes once the released request has landed, or the double knows it never will.
    /// </summary>
    internal Task Settled => _settled.Task;

    /// <summary>
    /// Reports that the double holds the write, and waits until the test releases it.
    /// </summary>
    internal async Task HoldAsync()
    {
        _ = _held.TrySetResult();
        await _released.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the write to land.
    /// </summary>
    internal void Release() => _ = _released.TrySetResult();

    /// <summary>
    /// Reports that the released request has landed, or never will.
    /// </summary>
    internal void Settle() => _ = _settled.TrySetResult();

    /// <summary>
    /// Holds the write until the test releases it, then runs <paramref name="land"/>, which lands it; the test's wait for
    /// the write to settle fails with whatever <paramref name="land"/> throws, a held request's mismatch among them.
    /// </summary>
    internal async Task LandAsync(Func<Task> land)
    {
        await HoldAsync().ConfigureAwait(false);
        try
        {
            await land().ConfigureAwait(false);
            Settle();
        }
        catch (Exception exception)
        {
            _ = _settled.TrySetException(exception);
        }
    }

    /// <inheritdoc cref="LandAsync(Func{Task})"/>
    internal Task LandAsync(Action land) => LandAsync(() =>
    {
        land();
        return Task.CompletedTask;
    });

    /// <summary>
    /// Reports that the double holds the write, waits until the caller's token is cancelled, and then throws
    /// <paramref name="conflict"/>, the backend's answer that the write's condition failed.
    /// </summary>
    internal async Task ConflictAfterCancellationAsync(Exception conflict, CancellationToken cancellationToken)
    {
        _ = HoldAsync();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller stopped waiting; the backend's conflict answer arrives after it.
        }

        throw conflict;
    }
}
