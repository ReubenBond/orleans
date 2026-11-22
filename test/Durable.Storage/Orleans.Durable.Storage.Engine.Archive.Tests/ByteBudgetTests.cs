using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// The budgets of the compaction and drain workers of both stores (archive-lsm.md §8.1, §8.2, archive-logs.md §8.1): requests are granted in arrival order while their
/// bytes are available, a release grants the waiters it unblocks, a cancelled wait grants those it blocked, and a lease
/// grows without waiting only while no request waits.
/// </summary>
[NoPageLeakCheck("The budget counts bytes; the tests rent no pages.")]
public sealed class ByteBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RequestsAreGrantedInArrivalOrderAsTheirBytesAreReleased()
    {
        var budget = new ByteBudget(10);
        var first = await budget.AcquireAsync(6, Token);
        var large = budget.AcquireAsync(10, Token);
        var small = budget.AcquireAsync(1, Token);

        Assert.False(large.IsCompleted);
        Assert.False(small.IsCompleted, "A request waits behind an earlier one, though its own bytes are available.");

        first.Dispose();
        using (await large)
        {
            Assert.False(small.IsCompleted);
        }

        using (await small)
        {
            Assert.Equal(9, budget.Available);
        }

        Assert.Equal(10, budget.Available);
    }

    [Fact]
    public async Task ACancelledWaitGrantsTheRequestsItBlocked()
    {
        var budget = new ByteBudget(10);
        using var held = await budget.AcquireAsync(5, Token);
        using var cancellation = new CancellationTokenSource();
        var blocked = budget.AcquireAsync(10, cancellation.Token);
        var behind = budget.AcquireAsync(5, Token);

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        using (await behind)
        {
            Assert.Equal(0, budget.Available);
        }
    }

    [Fact]
    public async Task ALeaseReleasesItsBytesOnce()
    {
        var budget = new ByteBudget(10);
        var lease = await budget.AcquireAsync(4, Token);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(10, budget.Available);
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => budget.AcquireAsync(11, Token));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => budget.AcquireAsync(0, Token));
    }

    [Fact]
    public async Task ALeaseGrowsWithoutWaitingOnlyWhileNoRequestWaits()
    {
        var budget = new ByteBudget(10);
        var lease = await budget.AcquireAsync(2, Token);

        Assert.True(lease.TryGrow(5));
        Assert.False(lease.TryGrow(4), "A lease grew past the bytes available.");
        var waiting = budget.AcquireAsync(4, Token);
        Assert.False(waiting.IsCompleted);
        Assert.False(lease.TryGrow(1), "A lease grew ahead of a waiting request.");
        Assert.Equal(7, lease.Bytes);

        lease.Dispose();
        using (await waiting)
        {
            Assert.Equal(6, budget.Available);
        }

        _ = Assert.Throws<ObjectDisposedException>(() => lease.TryGrow(1));
    }

    /// <summary>A lease returns bytes it no longer needs, which grant a waiter, and keeps at least one.</summary>
    [Fact]
    public async Task ALeaseShrinksAndGrantsTheWaitersItUnblocks()
    {
        var budget = new ByteBudget(10);
        using var held = await budget.AcquireAsync(8, Token);
        var waiting = budget.AcquireAsync(5, Token);
        Assert.False(waiting.IsCompleted);

        held.Shrink(3);

        using var granted = await waiting;
        Assert.Equal((5, 0), (held.Bytes, budget.Available));
        Assert.Throws<ArgumentOutOfRangeException>(() => held.Shrink(5));
    }
}
