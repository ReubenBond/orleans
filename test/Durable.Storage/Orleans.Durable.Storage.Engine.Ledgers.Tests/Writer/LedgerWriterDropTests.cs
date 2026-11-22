using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

/// <summary>
/// Checks a writer whose ledger's replicas their nodes dropped, with <c>N = 1</c>, <c>Q = 1</c> and with <c>N = 3</c>,
/// <c>Q = 2</c> (LP-I8, LP-I5).
/// </summary>
public sealed class LedgerWriterDropTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// An append which meets a dropped replica is reported as meeting a seal, and Ledgers decides it from the ledger
    /// map (ledger-protocol.md §2.4): the replica answers a writer write which meets a dropped replica while fewer than <c>Q</c>
    /// answered written <c>Sealed</c> (LP-I5), here <c>N − Q + 1</c> dropped replicas. The append reports its position
    /// as sealed instead of waiting out the refusal as an outage (LP-I12), never completes its slot, and stops the
    /// sequencer with <see cref="SequencerStopReason.Sealing"/>.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task AppendWhichMeetsADroppedReplicaIsReportedAsMeetingASeal(int replicas, int quorum)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(replicas, windowSize: 4);
        await using var log = WriterTestData.CreateLog(storage.OpenEach(), quorum, windowSize: 4);
        Assert.Equal(WriterTestData.Appended(0), await WriterTestData.AppendAsync(log, "first"u8.ToArray(), cancellationToken));
        for (var replica = 0; replica < WriterTestData.Coverage(replicas, quorum); replica++)
        {
            await storage.DropAsync(replica, cancellationToken);
        }

        var result = await WriterTestData.AppendAsync(log, "second"u8.ToArray(), cancellationToken);

        Assert.Equal(WriterTestData.Sealed(1), result);
        Assert.Equal([SequencerStopReason.Sealing], log.StopReasons);
        _ = await Assert.ThrowsAsync<SequencerStoppedException>(
            () => log.Sequencer.AcquireSlotAsync(cancellationToken).AsTask());
        Assert.Equal(new Slot(1), log.KnownContiguousTail);
    }

    /// <summary>
    /// LP-I5: a dropped answer counts as sealed. With <c>N − Q + 1</c> replicas dropped, every quorum of answers holds a
    /// dropped one: a contiguous-tail read reports the ledger sealed, while a read which cannot decide without the
    /// dropped answers, and a seal, whose recovery meets them, answer <c>Dropped</c>; Ledgers classifies the position
    /// from the ledger map (ledger-protocol.md §3.3).
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task ADroppedReplicaReadsAsSealedAndAnswersReadsAndSealsDropped(int replicas, int quorum)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(replicas, windowSize: 4);
        await using var log = WriterTestData.CreateLog(storage.OpenEach(), quorum, windowSize: 4);
        Assert.Equal(WriterTestData.Appended(0), await WriterTestData.AppendAsync(log, "first"u8.ToArray(), cancellationToken));
        for (var replica = 0; replica < WriterTestData.Coverage(replicas, quorum); replica++)
        {
            await storage.DropAsync(replica, cancellationToken);
        }

        Assert.Equal(new LedgerTail(new Slot(1), IsSealed: true), (await log.ReadTailAsync(cancellationToken)).Tail);
        DroppedAnswers.AssertDropped(await log.ReadAsync(new Slot(1), cancellationToken));
        DroppedAnswers.AssertDropped(await log.RecoverAsync(cancellationToken));
    }
}
