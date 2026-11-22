using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

#pragma warning disable CA2000 // Each replica double passes to a Ledger, which owns and disposes it.

/// <summary>
/// The writer's acquisition and append (ledger-protocol.md §2.3): an append names a slot its writer acquired and has not
/// yet appended, so an append at a slot another writer acquired, at one never acquired, or at one already appended is
/// refused and sends nothing; a cancelled acquisition acquires nothing.
/// </summary>
public sealed class LedgerWriterAcquireTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// ledger-protocol.md §2.3: an append at a slot this writer did not acquire, here one another writer acquired, is
    /// refused with <see cref="ArgumentException"/> and writes nothing; the acquired slot appends once, and a second append
    /// at it is refused.
    /// </summary>
    [Fact]
    public async Task AnAppendAtAForeignSlotOrASlotAppendedTwiceIsRefused()
    {
        var token = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica();
        var replica = new TailRecordingLedgerReplica(inner);
        await using var log = WriterTestData.CreateLog(replica);
        await using var otherReplica = WriterTestData.CreateReplica();
        await using var other = WriterTestData.CreateLog(otherReplica);
        using var payload = WriterTestData.CreatePayload("row"u8);
        var foreign = await other.Writer.AcquireAsync(token);
        Assert.Equal(Slot.Zero, foreign);

        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await log.Writer.AppendAsync(foreign!.Value, payload, token));
        Assert.DoesNotContain(replica.Operations, static operation => operation.Kind == TailRecordingLedgerReplica.Write);

        var slot = await log.Writer.AcquireAsync(token);
        Assert.Equal(Slot.Zero, slot);
        Assert.Equal(WriterTestData.Appended(0), await log.Writer.AppendAsync(slot!.Value, payload, token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await log.Writer.AppendAsync(slot.Value, payload, token));
        Assert.Single(replica.Operations, static operation => operation.Kind == TailRecordingLedgerReplica.Write);
    }

    /// <summary>
    /// ledger-protocol.md §2.3: an acquisition cancelled before it starts, or while it waits for the window, throws
    /// <see cref="OperationCanceledException"/> and acquires nothing, so the next acquisition takes the slot it would have.
    /// </summary>
    [Fact]
    public async Task ACancelledAcquisitionTakesNoSlot()
    {
        var token = TestContext.Current.CancellationToken;
        await using var replica = WriterTestData.CreateReplica(windowSize: 1);
        await using var log = WriterTestData.CreateLog(replica, windowSize: 1);
        using var payload = WriterTestData.CreatePayload("row"u8);
        using var precancelled = new CancellationTokenSource();
        await precancelled.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await log.Writer.AcquireAsync(precancelled.Token));

        var first = await log.Writer.AcquireAsync(token);
        Assert.Equal(Slot.Zero, first);
        using var waiting = new CancellationTokenSource();
        var withdrawn = log.Writer.AcquireAsync(waiting.Token).AsTask();
        Assert.False(withdrawn.IsCompleted);
        await waiting.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawn);

        Assert.Equal(WriterTestData.Appended(0), await log.Writer.AppendAsync(first!.Value, payload, token));
        Assert.Equal(new Slot(1), await log.Writer.AcquireAsync(token));
    }
}
