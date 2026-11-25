using System.Globalization;

namespace Orleans.Durable.Storage;

/// <summary>
/// Configures ledger-local grain reminder dispatch and segment reminder draining (orleans.md §5.15, §6).
/// </summary>
public sealed class ReminderOptions
{
    /// <summary>
    /// Gets or sets the live reminder-heap byte threshold per open ledger above which the silo requests a routine ledger
    /// roll to bound primary-side memory (orleans.md §5.15, §6). Default: 4 MiB.
    /// </summary>
    public long MaxReminderBytesPerLedger { get; set; } = 4L << 20;

    /// <summary>
    /// Gets or sets the maximum number of concurrent ephemeral grain reminder dispatches per open ledger session.
    /// Default: 32.
    /// </summary>
    public int MaxConcurrentReminderDispatches { get; set; } = 32;

    /// <summary>
    /// Gets or sets the maximum number of concurrent sealed reminder shard publishes across all segment drains on a
    /// silo. Default: 16.
    /// </summary>
    public int MaxConcurrentReminderPublishes { get; set; } = 16;

    /// <summary>
    /// Gets or sets the initial retry delay after a transient ephemeral reminder dispatch failure. Default: 250 ms.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gets or sets the maximum retry delay cap after repeated transient ephemeral reminder dispatch failures.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the deterministic jitter window applied to overdue reminders when publishing sealed shards during
    /// segment drain. Default: 5 seconds.
    /// </summary>
    public TimeSpan OverdueJitterWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Throws unless each setting is within its valid range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxReminderBytesPerLedger, 1, nameof(MaxReminderBytesPerLedger));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentReminderDispatches, 1, nameof(MaxConcurrentReminderDispatches));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentReminderPublishes, 1, nameof(MaxConcurrentReminderPublishes));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InitialRetryDelay, TimeSpan.Zero, nameof(InitialRetryDelay));
        if (MaxRetryDelay < InitialRetryDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetryDelay),
                MaxRetryDelay,
                string.Create(CultureInfo.InvariantCulture, $"MaxRetryDelay ({MaxRetryDelay}) must be at least InitialRetryDelay ({InitialRetryDelay})."));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(OverdueJitterWindow, TimeSpan.Zero, nameof(OverdueJitterWindow));
    }
}
