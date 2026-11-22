using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// The archive store's answer for a key at its first touch (archive.md §2.2, §8.2): the store's prefix, or none when it
/// answered <see cref="ArchiveStoreRead.Absent"/> (carrying any per-ledger empty bounds), paired with the ledger map
/// value read before it. Its caller disposes it.
/// </summary>
/// <remarks>
/// The answer describes the key's history while <see cref="Map"/>'s open ledger for the key is unsealed: the key's history
/// is the prefix followed by the rows of the live ledgers <see cref="Map"/> lists which the prefix does not hold, found
/// by scanning each from <see cref="AbsentBound"/> (slot 0 unless an <c>Absent</c> answer proves a higher bound), and a
/// first touch confirms the open ledger unsealed with the seal check in its process before it relies on the answer (HI-I1).
/// </remarks>
public sealed class ArchiveStoreAnswer : IDisposable
{
    private readonly IArchiveStore _store;
    private readonly ArchiveMerge.Mutant _mutant;
    private int _disposed;

    internal ArchiveStoreAnswer(
        IArchiveStore store,
        LedgerMapValue map,
        ArchivePrefix? prefix,
        ImmutableArray<LedgerAbsentBound> absentBounds = default,
        ArchiveMerge.Mutant mutant = ArchiveMerge.Mutant.None)
    {
        _store = store;
        _mutant = mutant;
        Map = map;
        Prefix = prefix;
        AbsentBounds = prefix is null && !absentBounds.IsDefaultOrEmpty ? absentBounds : [];
    }

    /// <summary>Gets the ledger map value read before the store.</summary>
    public LedgerMapValue Map { get; }

    /// <summary>Gets the key's prefix in the store, which the answer owns, or <see langword="null"/> for <c>Absent</c>.</summary>
    public ArchivePrefix? Prefix { get; }

    /// <summary>
    /// Gets the per-ledger slot bounds from an <see cref="ArchiveStoreRead.Absent"/> answer below which the store proves
    /// the key has no row in each listed ledger; empty when <see cref="Prefix"/> is present or the store reported none.
    /// </summary>
    public ImmutableArray<LedgerAbsentBound> AbsentBounds { get; }

    /// <summary>Returns the proven empty bound for <paramref name="ledger"/>, or <see cref="Slot.Zero"/> when none.</summary>
    public Slot AbsentBound(LedgerId ledger) => ArchiveStoreRead.Absent.BoundOf(AbsentBounds, ledger);

    /// <summary>
    /// Merges the prefix with <paramref name="live"/>, the decided rows of the key the caller scanned from the live
    /// ledgers <see cref="Map"/> lists, each from slot 0 to its decided end in <paramref name="through"/>, into the key's
    /// history: each row marked held by the store's <c>Holds</c>, and the token chain and versions of the rows not held
    /// checked (archive.md §8.1 step 4, HI-I4). The merge borrows its inputs and retains what it returns.
    /// </summary>
    /// <param name="live">The live rows, in strictly increasing tag order, not marked held.</param>
    /// <param name="through">Each live ledger's decided end, which the read copies.</param>
    /// <param name="openLedger">The open ledger over the key in the map value read, whose seal check gave its end; null when it lists none.</param>
    /// <param name="linearizable">Whether the open ledger's end was a seal check's tail in its primary's process.</param>
    /// <returns>
    /// The history read, which the caller disposes; <see cref="ArchiveMergeOutcome.PastEnd"/>, after which the caller
    /// reads the map and the store again; or the <see cref="ArchiveCorruptException"/> naming a break of the key's chain,
    /// which the caller throws once a map read after it still lists every ledger it scanned, and otherwise reads again.
    /// </returns>
    /// <exception cref="ArgumentException">The rows' tags do not strictly increase, or a row is marked held.</exception>
    public ArchiveMergeOutcome Merge(ReadOnlySpan<ArchiveRow> live, IReadOnlyDictionary<LedgerId, Slot> through, LedgerId? openLedger, bool linearizable)
    {
        ArgumentNullException.ThrowIfNull(through);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        for (var index = 0; index < live.Length; index++)
        {
            if (live[index].IsHeld)
            {
                throw new ArgumentException("A history's live rows come unmarked: the merge marks the held ones.", nameof(live));
            }

            if (index > 0 && live[index].Tag <= live[index - 1].Tag)
            {
                throw new ArgumentException("A history's live rows are in strictly increasing tag order.", nameof(live));
            }
        }

        return ArchiveMerge.Merge(_store, Prefix, live, through, openLedger, linearizable, _mutant);
    }

    /// <summary>
    /// Internal merge for <see cref="ArchiveReader"/>, which already validated row ordering and exclusively owns
    /// <paramref name="through"/> (so the read keeps it directly without copying).
    /// </summary>
    internal ArchiveMergeOutcome MergeOwned(ReadOnlySpan<ArchiveRow> live, IReadOnlyDictionary<LedgerId, Slot> through, LedgerId? openLedger, bool linearizable)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return ArchiveMerge.MergeOwned(_store, Prefix, live, through, openLedger, linearizable, _mutant);
    }

    /// <summary>Releases the prefix.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Prefix?.Dispose();
        }
    }
}
