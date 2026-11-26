namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// What a node applies the decided batches of the ledgers it replicates to, which <see cref="LedgerApplyLoop"/> delivers
/// in slot order as resolved rows (ledgers.md §8.5): the node's key indexes (journals.md §8.3), a layer above Ledgers,
/// which gives the loop its three callbacks. The loop calls them for different ledgers concurrently, and for one ledger
/// serially.
/// </summary>
public sealed class LedgerApplier
{
    /// <summary>
    /// Gets the callback which returns the slot from which the applier next needs a ledger's batches: its durable
    /// watermark, below which it has applied every batch, or zero. The loop asks once per delivery it starts, as after a
    /// restart.
    /// </summary>
    public required Func<LedgerId, CancellationToken, ValueTask<Slot>> NextSlotAsync { get; init; }

    /// <summary>
    /// Gets the callback which applies the decided batch an item holds, the ledger's next in slot order: the item's
    /// <see cref="LedgerScanItem.Read"/> holds the batch's <see cref="LedgerRead.Data"/>, and its
    /// <see cref="LedgerScanItem.Rows"/> lend the batch's resolved rows until the callback's task completes, after which
    /// the loop releases the batch.
    /// </summary>
    public required Func<LedgerId, LedgerScanItem, CancellationToken, ValueTask> ApplyAsync { get; init; }

    /// <summary>
    /// Gets an optional callback invoked by <see cref="LedgerApplyLoop"/> when a scan pass finishes (reaching its target
    /// or stopping on backpressure), allowing an applier that buffers consecutive decided slots within a pass to commit
    /// them together.
    /// </summary>
    public Func<LedgerId, CancellationToken, ValueTask>? FlushAsync { get; init; }

    /// <summary>
    /// Gets an optional callback invoked by <see cref="LedgerApplyLoop"/> when a delivery starts at a non-zero
    /// watermark to restore a previously persisted <see cref="LedgerSegmentView"/> snapshot and avoid rewinding to the
    /// segment's start.
    /// </summary>
    public Func<LedgerId, CancellationToken, ValueTask<byte[]?>>? LoadSegmentViewAsync { get; init; }

    /// <summary>
    /// Gets an optional callback invoked by <see cref="LedgerApplyLoop"/> periodically and on clean stop to persist the
    /// active <see cref="LedgerSegmentView"/> snapshot alongside the applier's watermark.
    /// </summary>
    public Func<LedgerId, ReadOnlyMemory<byte>, CancellationToken, ValueTask>? SaveSegmentViewAsync { get; init; }

    /// <summary>
    /// Gets the callback which tells the applier the node's map view no longer lists a ledger, so the loop delivers no
    /// more of it: an applier which had not reached a closed ledger's <c>T</c> reads the rest from the archive store,
    /// which covers it below <c>T</c> (HI-I2, ledgers.md §8.5).
    /// </summary>
    public required Func<LedgerId, CancellationToken, ValueTask> RemoveAsync { get; init; }
}
