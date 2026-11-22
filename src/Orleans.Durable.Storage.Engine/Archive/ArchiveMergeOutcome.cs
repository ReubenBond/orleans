namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// What merging a prefix with a key's scanned live rows gives (archive.md §8.1 step 4, conventions.md §15): the history
/// read; <see cref="PastEnd"/>, when the prefix holds a write past the end the scans reached, so the read starts again; or
/// the <see cref="ArchiveCorruptException"/> naming how the scanned rows break the key's chain (HI-I4), which the caller
/// throws only once a map read after it still lists every ledger the scans read: a reader whose map has since dropped
/// one of them may have met a legal rebase, and starts again.
/// </summary>
public readonly union ArchiveMergeOutcome(ArchiveRead, ArchiveMergeOutcome.PastEnd, ArchiveCorruptException)
{
    /// <summary>
    /// The prefix holds a write past the end the read's scans reached: its token lies past <c>Plan</c> of the last write
    /// the scans met, which the prefix holds whole. The caller reads the map and the store again.
    /// </summary>
    public sealed record PastEnd
    {
        private PastEnd()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static PastEnd Instance { get; } = new();
    }
}
