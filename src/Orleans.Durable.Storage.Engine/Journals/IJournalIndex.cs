using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// Specialized durable derived index for one node's live ledgers (journals.md §8.3): stores <c>key_index</c> entries,
/// <c>tombstones</c> records, and per-ledger durable watermarks without a separate write-ahead log, advancing all three
/// together on atomic multi-column-family flushes.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ApplyBatchAsync"/> and <see cref="DropLedgersAsync"/> write directly into the index's in-memory write
/// buffers without WAL fsyncs; <see cref="FlushAsync"/> persists all column families atomically so a crash always
/// recovers every ledger to a consistent durable watermark from which <see cref="LedgerApplyLoop"/> replays any
/// unflushed suffix.
/// </para>
/// <para>
/// Closing via <see cref="CloseAsync"/> releases open handles without flushing unflushed writes, so disposal without a
/// preceding <see cref="FlushAsync"/> models a crash; reopening via <see cref="OpenAsync"/> resumes from the last
/// atomic flush's durable watermarks.
/// </para>
/// </remarks>
public interface IJournalIndex
{
    /// <summary>
    /// Opens the index at its last atomically flushed durable state and returns each persisted ledger's durable
    /// watermark (next slot to apply).
    /// </summary>
    /// <exception cref="JournalIndexCorruptException">The durable index is corrupt and must be reset.</exception>
    /// <exception cref="JournalIndexLockHeldException">Another open handle or process holds the index's lock.</exception>
    ValueTask<SortedDictionary<LedgerId, Slot>> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one ledger's batch of <c>key_index</c> puts, <c>tombstones</c> mutations, and advancing watermark into
    /// the index's write buffers in a single atomic batch.
    /// </summary>
    ValueTask ApplyBatchAsync(in JournalIndexBatch batch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all <c>key_index</c> entries, <c>tombstones</c> records, and watermarks for <paramref name="ledgers"/>.
    /// </summary>
    ValueTask DropLedgersAsync(IReadOnlyList<LedgerId> ledgers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every <c>key_index</c> entry for <c>(<paramref name="ledger"/>, <paramref name="keyHash"/>)</c> whose
    /// tag position lies in <c>[(<paramref name="startSlot"/>, <paramref name="startIndex"/>), (<paramref name="exclusiveEndSlot"/>, 0))</c>,
    /// in ascending <c>(Slot, Index)</c> order.
    /// </summary>
    ValueTask<List<JournalIndexKeyEntry>> GetKeyEntriesAsync(
        LedgerId ledger,
        JournalKeyHash keyHash,
        Slot startSlot,
        int startIndex,
        Slot exclusiveEndSlot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether at least one <c>key_index</c> entry exists for
    /// <c>(<paramref name="ledger"/>, <paramref name="keyHash"/>)</c> in
    /// <c>[(<paramref name="startSlot"/>, <paramref name="startIndex"/>), (<paramref name="exclusiveEndSlot"/>, 0))</c>.
    /// </summary>
    ValueTask<bool> HasKeyEntryAsync(
        LedgerId ledger,
        JournalKeyHash keyHash,
        Slot startSlot,
        int startIndex,
        Slot exclusiveEndSlot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every tombstone currently indexed for <paramref name="ledger"/>.
    /// </summary>
    ValueTask<List<JournalIndexTombstone>> ScanTombstonesAsync(
        LedgerId ledger,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically flushes <c>key_index</c>, <c>tombstones</c>, and watermarks to durable storage so a subsequent crash
    /// recovers at least every batch applied before this flush began.
    /// </summary>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the index's durable and buffered state and reopens an empty index.
    /// </summary>
    ValueTask ResetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a low-priority integrity check verifying checksums across all column families.
    /// </summary>
    /// <exception cref="JournalIndexCorruptException">Checksum or format corruption was detected.</exception>
    ValueTask CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes any open handles and discards unflushed buffered writes without flushing.
    /// </summary>
    ValueTask CloseAsync();
}
