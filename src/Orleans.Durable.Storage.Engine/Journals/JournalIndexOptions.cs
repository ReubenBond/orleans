namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// Options for <see cref="JournalStateDatabase"/> and its underlying <see cref="IJournalIndex"/>.
/// </summary>
public sealed class JournalIndexOptions
{
    /// <summary>
    /// Gets or sets the interval between periodic atomic flushes of the journal index's column families;
    /// <see cref="Timeout.InfiniteTimeSpan"/> disables periodic flushes (flushes then happen on explicit
    /// <see cref="JournalStateDatabase.FlushAsync"/> or when RocksDB's write-buffer budget fills).
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the interval between low-priority background integrity checks of the journal index;
    /// <see cref="Timeout.InfiniteTimeSpan"/> disables periodic checks.
    /// </summary>
    public TimeSpan QuickCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the total memtable write-buffer budget (in bytes) across the index's column families, bounding
    /// crash replay length. Defaults to 64 MiB.
    /// </summary>
    public long MaxWriteBufferBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Validates the option values.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An option value is outside its valid range.</exception>
    public void Validate()
    {
        if (MaxWriteBufferBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxWriteBufferBytes), MaxWriteBufferBytes, "Write buffer budget must be positive.");
        }

        if (FlushInterval != Timeout.InfiniteTimeSpan && FlushInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(FlushInterval), FlushInterval, "Flush interval must be positive or Timeout.InfiniteTimeSpan.");
        }

        if (QuickCheckInterval != Timeout.InfiniteTimeSpan && QuickCheckInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(QuickCheckInterval), QuickCheckInterval, "Quick check interval must be positive or Timeout.InfiniteTimeSpan.");
        }
    }
}
