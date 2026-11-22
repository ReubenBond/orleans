using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// One decoded row from the journal index's <c>key_index</c> column family for a <c>(ledger, keyHash)</c> prefix
/// (journals.md §8.3): the row's tag position (<see cref="Slot"/>, <see cref="Index"/>), its defining row's slot
/// (<see cref="DefiningSlot"/>), and its segment ordinal (<see cref="Ordinal"/>).
/// </summary>
public readonly record struct JournalIndexKeyEntry(Slot Slot, int Index, Slot DefiningSlot, ulong Ordinal);

/// <summary>
/// One <c>key_index</c> entry to write in a <see cref="JournalIndexBatch"/>.
/// </summary>
public readonly record struct JournalIndexKeyWrite(
    JournalKeyHash KeyHash,
    Slot Slot,
    int Index,
    Slot DefiningSlot,
    ulong Ordinal);

/// <summary>
/// One <c>tombstones</c> mutation to apply in a <see cref="JournalIndexBatch"/>: either inserting/replacing a live
/// tombstone (<see cref="IsDelete"/> is <see langword="false"/>) or deleting one on resurrection/expiry
/// (<see cref="IsDelete"/> is <see langword="true"/>).
/// </summary>
public readonly record struct JournalIndexTombstoneWrite(
    JournalKeyHash KeyHash,
    bool IsDelete,
    long DeletionTicks,
    Slot DeleteSlot,
    int DeleteIndex,
    ReadOnlyMemory<byte> RawKey)
{
    /// <summary>Creates a write that inserts or replaces a live tombstone for <paramref name="keyHash"/>.</summary>
    public static JournalIndexTombstoneWrite Put(
        JournalKeyHash keyHash,
        long deletionTicks,
        Slot deleteSlot,
        int deleteIndex,
        ReadOnlyMemory<byte> rawKey) =>
        new(keyHash, IsDelete: false, deletionTicks, deleteSlot, deleteIndex, rawKey);

    /// <summary>Creates a write that removes any live tombstone for <paramref name="keyHash"/>.</summary>
    public static JournalIndexTombstoneWrite Delete(JournalKeyHash keyHash) =>
        new(keyHash, IsDelete: true, DeletionTicks: 0, DeleteSlot: Slot.Zero, DeleteIndex: 0, ReadOnlyMemory<byte>.Empty);
}

/// <summary>
/// One decoded tombstone returned by <see cref="IJournalIndex.ScanTombstonesAsync"/> (journals.md §8.6).
/// </summary>
public readonly record struct JournalIndexTombstone(
    JournalKeyHash KeyHash,
    long DeletionTicks,
    Slot DeleteSlot,
    int DeleteIndex,
    ReadOnlyMemory<byte> RawKey);

/// <summary>
/// One atomic write batch for a single ledger: its <c>key_index</c> puts, <c>tombstones</c> puts/deletes, and
/// advancing watermark (<see cref="NextWatermark"/>).
/// </summary>
public readonly struct JournalIndexBatch(
    LedgerId ledger,
    Slot nextWatermark,
    ReadOnlyMemory<JournalIndexKeyWrite> keyWrites,
    ReadOnlyMemory<JournalIndexTombstoneWrite> tombstoneWrites)
{
    /// <summary>Gets the ledger whose index entries and watermark this batch updates.</summary>
    public LedgerId Ledger { get; } = ledger;

    /// <summary>Gets the ledger's new applied watermark (<c>lastSlot + 1</c>).</summary>
    public Slot NextWatermark { get; } = nextWatermark;

    /// <summary>Gets the <c>key_index</c> rows to insert.</summary>
    public ReadOnlyMemory<JournalIndexKeyWrite> KeyWrites { get; } = keyWrites;

    /// <summary>Gets the <c>tombstones</c> puts and deletes to apply.</summary>
    public ReadOnlyMemory<JournalIndexTombstoneWrite> TombstoneWrites { get; } = tombstoneWrites;
}
