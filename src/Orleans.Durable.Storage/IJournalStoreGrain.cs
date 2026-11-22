using Orleans.Concurrency;

namespace Orleans.Durable.Storage;

/// <summary>
/// A node's journal endpoint (orleans.md §5.5, §7.2): a forwarded write, a forwarded read's first page, its later pages,
/// and the release of an abandoned read's snapshot, keyed by the node identity. Placed by <see cref="RegisteredNodePlacement"/> only on the silo the registry names for the
/// node while membership shows it active (OR-I14). Calls interleave: the node's Journals serialize the state they own.
/// </summary>
internal interface IJournalStoreGrain : IGrainWithGuidKey
{
    /// <summary>Writes a forwarded entry through the node's Journals, unless the key's ledger is throttled.</summary>
    [AlwaysInterleave]
    Task<JournalForwardedWriteOutcome> WriteAsync(JournalForwardedEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the journal <paramref name="key"/> through the node's Journals and answers its first page, pinning a snapshot
    /// for the later pages when more follow.
    /// </summary>
    [AlwaysInterleave]
    Task<JournalForwardedRead> ReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Answers the page of the pinned <paramref name="snapshot"/> at <paramref name="offset"/>, the last releasing it.</summary>
    [AlwaysInterleave]
    Task<JournalForwardedPage> ReadPageAsync(Guid snapshot, long offset, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the pinned <paramref name="snapshot"/> of a read its reading silo abandoned, so its reservation ends before
    /// its deadline; a snapshot already released is left as it is. One-way: the reading silo awaits no answer.
    /// </summary>
    [AlwaysInterleave]
    [OneWay]
    Task ReleaseSnapshotAsync(Guid snapshot);
}
