using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

#pragma warning disable CA2000 // Each replica double passes to the primary's Ledger, which owns and disposes it.

/// <summary>
/// Checks the writer's explicit contiguous-tail writes while idle (ledger-protocol.md §8.2, §8.4): once a completion
/// raised the known contiguous tail past what its writes carried and it has not written for
/// <see cref="LedgerWriter.ContiguousTailInterval"/>, the writer sends the tail to every replica without sealing, once,
/// as BookKeeper's explicit last add confirmed does, so readers in other processes read the last batches from one replica
/// (LP-I10, LP-I11). A writer whose lifetime stopped, or which was disposed, writes none. The writer runs over three replica
/// doubles with <c>Q = 2</c> on a fake clock, which fires its timer as the test advances it.
/// </summary>
public sealed class LedgerWriterContiguousTailTests : IDisposable
{
    private static readonly TimeSpan Interval = LedgerWriter.ContiguousTailInterval;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// The writer's last write carried the tail before its own completion, so once it has been idle for the interval, and
    /// not a tick sooner, it sends the tail its completions raised to every replica, and nothing more while it stays idle.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task AnIdleWriterWritesItsContiguousTailToEveryReplicaOnceTheIntervalPasses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var replicas = Replicas();
        await using var primary = WriterTestData.CreateLog(replicas, quorum: 2, timeProvider: time);

        Assert.Equal(WriterTestData.Appended(0), await WriterTestData.AppendAsync(primary, "a"u8.ToArray(), cancellationToken));
        Assert.Equal(WriterTestData.Appended(1), await WriterTestData.AppendAsync(primary, "b"u8.ToArray(), cancellationToken));
        Assert.Equal([0L, 1], replicas[0].WriteCalls.Select(static call => call.ContiguousTail.Value));

        time.Advance(Interval - TimeSpan.FromTicks(1));
        Assert.All(replicas, static replica => Assert.Empty(ExplicitTails(replica)));

        time.Advance(TimeSpan.FromTicks(1));
        Assert.All(replicas, static replica => Assert.Equal([2L], ExplicitTails(replica)));

        time.Advance(10 * Interval);
        Assert.All(replicas, static replica => Assert.Equal([2L], ExplicitTails(replica)));
    }

    /// <summary>
    /// A write within the interval carries the tail the earlier completion raised and postpones the explicit write, which
    /// follows the interval after the last write and carries the tail its completion raised.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task AWriteWithinTheIntervalPostponesTheExplicitWriteToTheIntervalAfterIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var replicas = Replicas();
        await using var primary = WriterTestData.CreateLog(replicas, quorum: 2, timeProvider: time);
        var gap = TimeSpan.FromMilliseconds(60);

        _ = await WriterTestData.AppendAsync(primary, "a"u8.ToArray(), cancellationToken);
        time.Advance(gap);
        _ = await WriterTestData.AppendAsync(primary, "b"u8.ToArray(), cancellationToken);
        Assert.Equal([0L, 1], replicas[0].WriteCalls.Select(static call => call.ContiguousTail.Value));

        time.Advance(Interval - TimeSpan.FromTicks(1));
        Assert.All(replicas, static replica => Assert.Empty(ExplicitTails(replica)));

        time.Advance(TimeSpan.FromTicks(1));
        Assert.All(replicas, static replica => Assert.Equal([2L], ExplicitTails(replica)));
    }

    /// <summary>
    /// A writer whose lifetime its ledger's recovery stopped (LP-I14), or which was disposed, writes no tail explicitly,
    /// however long it then stays idle.
    /// </summary>
    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStoppedOrDisposedWriterWritesNoTailExplicitly(bool disposed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var replicas = Replicas();
        await using var primary = WriterTestData.CreateLog(replicas, quorum: 2, timeProvider: time);

        _ = await WriterTestData.AppendAsync(primary, "a"u8.ToArray(), cancellationToken);
        if (disposed)
        {
            await primary.Writer.DisposeAsync();
        }
        else
        {
            Assert.Equal(new RecoveryOutcome.Recovered(new Slot(1)), (await primary.RecoverAsync(cancellationToken)).Value);
        }

        time.Advance(10 * Interval);
        Assert.All(replicas, static replica => Assert.Empty(ExplicitTails(replica)));
    }

    private static RecordingLedgerReplica[] Replicas() => [new(), new(), new()];

    /// <summary>Returns the tails the writer sent a replica explicitly: its contiguous-tail reads which do not seal.</summary>
    private static long[] ExplicitTails(RecordingLedgerReplica replica) =>
        [.. replica.ReadTailCalls.Where(static call => !call.Seal).Select(static call => call.ContiguousTail.Value)];
}
