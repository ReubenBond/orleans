namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>The policy of a <c>JournalStore</c> and its admission (journals.md §8.8).</summary>
public sealed class JournalStoreOptions
{
    /// <summary>
    /// Gets or sets the longest body an <c>Append</c> or a <c>Replace</c> carries, at least 1 and 16 MiB by default;
    /// admission refuses a longer one (journals.md §8.4).
    /// </summary>
    public int MaxBodyBytes { get; set; } = 16 << 20;

    /// <summary>Gets or sets the most entries one batch holds.</summary>
    public int MaxBatchEntries { get; set; } = 256;

    /// <summary>
    /// Gets or sets the bytes of rows after which a batch takes no more entries; a batch always takes its first entry, and
    /// never passes the replica's payload limit.
    /// </summary>
    public int MaxBatchBytes { get; set; } = 1 << 20;

    /// <summary>
    /// Gets or sets the bytes of rows a ledger's admission holds in entries it accepted and has not yet proposed; a write
    /// past it waits until a batch takes entries.
    /// </summary>
    public long MaxQueuedBytes { get; set; } = 64L << 20;

    /// <summary>
    /// Gets or sets the bytes of warm state a ledger's admission keeps; past it, it evicts the least recently used keys
    /// with nothing waiting or in flight.
    /// </summary>
    public long WarmStateBytes { get; set; } = 64L << 20;

    /// <summary>
    /// Gets or sets how long a deleted journal keeps its tombstone: an <c>Expired</c> applies only once the tombstone's
    /// deletion time plus it is at or before the primary's clock (journals.md §8.6); at least zero, and 7 days by default.
    /// </summary>
    public TimeSpan TombstoneRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets the least time between the starts of two of a primary's expiry scans, which propose an <c>Expired</c> for
    /// each warm or indexed journal whose tombstone's retention has passed (journals.md §8.6); above zero, and 1 hour by default.
    /// </summary>
    public TimeSpan ExpiryInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets the most history reads the store runs at once, its loads and its owner reads together, at least 1 and 4
    /// by default; a read past it waits for one to end (journals.md §8.8). The bound limits concurrent archive-store and
    /// replica reads and the history a process holds assembling reads.
    /// </summary>
    public int MaxConcurrentHistoryReads { get; set; } = 4;

    /// <summary>
    /// Gets or sets the maximum number of <see cref="MessageRecord"/> items attached to a single journal write
    /// (1 to 255, default 8; journals.md §8.8).
    /// </summary>
    public int MaxMessagesPerWrite { get; set; } = 8;

    /// <summary>
    /// Gets or sets the maximum combined payload bytes of all <see cref="MessageRecord"/> items attached to a
    /// single journal write (default 64 KiB; journals.md §8.8).
    /// </summary>
    public int MaxMessageBytesPerWrite { get; set; } = 65_536;

    /// <summary>Returns a copy, which a store keeps, so it enforces the settings it checked whatever its caller changes.</summary>
    internal JournalStoreOptions Clone() => (JournalStoreOptions)MemberwiseClone();

    /// <summary>
    /// Returns the bytes of the largest batch admission builds under these options (journals.md §8.4): the larger of a batch
    /// of one row with the longest key and the longest body, which a row past <see cref="MaxBatchBytes"/> takes alone, and a
    /// batch of <see cref="MaxBatchEntries"/> rows within <see cref="MaxBatchBytes"/>. A ledger's queue and its writer's
    /// proposals each hold at least it.
    /// </summary>
    public long LargestBatch() => LedgerAdmission.LargestBatch(this);

    /// <summary>
    /// Returns the bytes of the largest batch admission builds under these options, including message rows when
    /// <paramref name="hasMessageChannels"/> is <see langword="true"/> (journals.md §8.4).
    /// </summary>
    public long LargestBatch(bool hasMessageChannels) => LedgerAdmission.LargestBatch(this, hasMessageChannels);

    /// <summary>Throws unless each setting is within its range (journals.md §8.8), naming the setting.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of its range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBodyBytes, 1, nameof(MaxBodyBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchEntries, 1, nameof(MaxBatchEntries));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchBytes, 1, nameof(MaxBatchBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxQueuedBytes, 1, nameof(MaxQueuedBytes));
        ArgumentOutOfRangeException.ThrowIfNegative(WarmStateBytes, nameof(WarmStateBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(TombstoneRetention, TimeSpan.Zero, nameof(TombstoneRetention));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ExpiryInterval, TimeSpan.Zero, nameof(ExpiryInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentHistoryReads, 1, nameof(MaxConcurrentHistoryReads));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMessagesPerWrite, 1, nameof(MaxMessagesPerWrite));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxMessagesPerWrite, byte.MaxValue, nameof(MaxMessagesPerWrite));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxMessageBytesPerWrite, nameof(MaxMessageBytesPerWrite));
    }
}
