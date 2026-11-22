using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// The store the live ledgers drain into (archive.md §3): the LSM or archive logs. A cluster uses one for its life, and
/// <see cref="ArchiveReader"/> reads a key's history across the live ledgers and it. Beside its reads, a store has pure
/// members over its archive tokens, which do no I/O, are deterministic, and depend only on their arguments and the
/// store's parameters, which its record holds (HI-I3).
/// </summary>
public interface IArchiveStore
{
    /// <summary>Gets the store's kind, which a composition records with its ID (archive.md §2.4).</summary>
    ArchiveStoreKind Kind { get; }

    /// <summary>Gets the largest <c>Append</c> body the store accepts, or <see langword="null"/> when it is unbounded.</summary>
    int? MaxAppendBytes { get; }

    /// <summary>
    /// Reads the prefix of <paramref name="key"/> the store holds (archive.md §3, HI-I1): the store's first touch of a key,
    /// whose one read answers absence too.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="map">A ledger map value the caller read before the call.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>
    /// <see cref="ArchiveStoreRead.Absent"/> when the key's prefix would hold no command: the store holds none of the key,
    /// or, on a store whose coverage is <paramref name="map"/>, none in a ledger <paramref name="map"/> no longer lists
    /// (<see cref="LedgerMapValue.NoLongerLists"/>). Or <see cref="ArchiveStoreRead.Read"/>, which its caller disposes:
    /// the key's prefix, whose <see cref="Holds"/> covers every decided row of the key in a ledger <paramref name="map"/>
    /// no longer lists, and which holds exactly the writes its <see cref="Holds"/> holds. Either answers from the store's
    /// state as of a read begun after the call, or from cached state which covers every ledger <paramref name="map"/> no
    /// longer lists. Or <see cref="ArchiveStoreRead.Stale"/>, when the store's own snapshot proved out of date, or
    /// <paramref name="map"/> older than it: the caller reads the map and the store again.
    /// </returns>
    /// <exception cref="ArchiveCorruptException">The store contradicts itself.</exception>
    /// <exception cref="NotSupportedException">The key's content would pass <see cref="int.MaxValue"/> bytes, the bound of a prefix's content.</exception>
    ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the store's record (archive.md §2.4), which the first call which finds none creates with the store's own
    /// fields and <paramref name="parameters"/>; the same record for every later call. The store checks its own fields, its
    /// layout and its <c>Plan</c> parameters, against the record's, and never its caller's: the record's
    /// <see cref="ArchiveStoreRecord.Parameters"/> may differ from <paramref name="parameters"/>, which the caller compares
    /// with <see cref="ArchiveStoreRecord.CheckParametersMatch"/>.
    /// </summary>
    /// <param name="parameters">The caller's opaque <c>key=value</c> parameters, which a record the call creates holds verbatim.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <exception cref="ArgumentException">A parameter is not a valid field of a store's record.</exception>
    /// <exception cref="ArchiveStoreMismatchException">
    /// The record holds other store fields than the store's own, which the exception names, never a caller parameter
    /// (HI-I3).
    /// </exception>
    /// <exception cref="ArchiveCorruptException">The record breaks its format.</exception>
    ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken);

    /// <summary>
    /// Begins the incremental fold of <c>Plan</c> from <paramref name="token"/> (archive.md §3), which defines the store's
    /// <c>Plan</c> and <c>Admits</c> (<see cref="ArchiveStorePlans"/>).
    /// </summary>
    /// <exception cref="ArchiveCorruptException"><paramref name="token"/> is one the store could not have minted.</exception>
    ArchivePlan Begin(RowToken token);

    /// <summary>
    /// Returns whether <paramref name="prefix"/>, one of this store's, holds the first <paramref name="count"/> commands of
    /// <paramref name="write"/>, applied or superseded: true for 0, and monotone in <paramref name="count"/> and in the
    /// prefix's coverage, which only grows.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative or above the write's commands.</exception>
    bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count);

    /// <summary>
    /// Returns whether the store asks for a <c>Replace</c> of the key soon, with headroom before <c>Admits</c> refuses.
    /// </summary>
    /// <exception cref="ArchiveCorruptException"><paramref name="token"/> is one the store could not have minted.</exception>
    bool IsReplaceRequested(RowToken token);

    /// <summary>Returns whether the store can store <paramref name="key"/> at all.</summary>
    bool AdmitsKey(ReadOnlySpan<byte> key);
}
