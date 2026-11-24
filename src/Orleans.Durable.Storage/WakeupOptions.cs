using System.Globalization;

namespace Orleans.Durable.Storage;

/// <summary>
/// Configures ledger-local grain wakeup dispatch and closed-ledger wakeup drain (orleans.md §5.15, §6).
/// </summary>
public sealed class WakeupOptions
{
    /// <summary>
    /// Gets or sets the live wakeup-heap byte threshold per open ledger above which the silo requests a routine ledger
    /// roll to bound primary-side memory (orleans.md §5.15, §6). Default: 4 MiB.
    /// </summary>
    public long MaxWakeupBytesPerLedger { get; set; } = 4L << 20;

    /// <summary>
    /// Gets or sets the maximum number of concurrent ephemeral grain wakeup dispatches per open ledger session.
    /// Default: 32.
    /// </summary>
    public int MaxConcurrentWakeupDispatches { get; set; } = 32;

    /// <summary>
    /// Gets or sets the maximum number of concurrent backing <c>ScheduleJobAsync</c> calls across all closed-ledger
    /// wakeup drains on a silo. Default: 16.
    /// </summary>
    public int MaxConcurrentWakeupSchedules { get; set; } = 16;

    /// <summary>
    /// Gets or sets the initial retry delay after a transient ephemeral wakeup dispatch failure. Default: 250 ms.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gets or sets the maximum retry delay cap after repeated transient ephemeral wakeup dispatch failures.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the deterministic jitter window applied to overdue wakeups when scheduling backing jobs during
    /// closed-ledger drain. Default: 5 seconds.
    /// </summary>
    public TimeSpan OverdueJitterWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Throws unless each setting is within its valid range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWakeupBytesPerLedger, 1, nameof(MaxWakeupBytesPerLedger));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentWakeupDispatches, 1, nameof(MaxConcurrentWakeupDispatches));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentWakeupSchedules, 1, nameof(MaxConcurrentWakeupSchedules));
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
