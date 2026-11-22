using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Configures the L2 ledger-replica quorum read lease (system-model.md §2, ledger-protocol.md §4, LP-I18).
/// </summary>
/// <remarks>
/// <para>
/// Leases use only monotonic clock-rate drift bounded by <see cref="ClockDriftFactor"/> (<c>v_max / v_min ≤ F</c>),
/// never wall-clock synchronization. Setting <see cref="LeaseDuration"/> to <see cref="TimeSpan.Zero"/> disables leases
/// completely and reduces every read tail check to the clock-free quorum check (LP-I9).
/// </para>
/// </remarks>
public sealed class LedgerLeaseOptions
{
    /// <summary>
    /// The default lease duration (3 seconds).
    /// </summary>
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The default monotonic clock-rate drift factor (<c>F = 1.25</c>, covering symmetric ±10% rate drift).
    /// </summary>
    public const double DefaultClockDriftFactor = 1.25;

    /// <summary>
    /// The default maximum guard band subtracted from the primary's local lease horizon (500 ms).
    /// </summary>
    public static readonly TimeSpan DefaultMaxGuardBand = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets the default lease options (<see cref="DefaultLeaseDuration"/>, <see cref="DefaultClockDriftFactor"/>).
    /// </summary>
    public static LedgerLeaseOptions Default { get; } = new();

    /// <summary>
    /// Gets options with leases disabled (<see cref="LeaseDuration"/> is <see cref="TimeSpan.Zero"/>).
    /// </summary>
    public static LedgerLeaseOptions Disabled { get; } = new()
    {
        LeaseDuration = TimeSpan.Zero,
        LeaseGuardBand = TimeSpan.Zero,
    };

    /// <summary>
    /// Gets or sets the target replica lease duration <c>D</c>. <see cref="TimeSpan.Zero"/> disables leases completely.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = DefaultLeaseDuration;

    /// <summary>
    /// Gets or sets the monotonic clock-rate drift bound <c>F ≥ v_max / v_min</c> (at least 1.0, finite).
    /// </summary>
    public double ClockDriftFactor { get; set; } = DefaultClockDriftFactor;

    /// <summary>
    /// Gets or sets an optional explicit guard band <c>δ</c> subtracted from the primary's local lease expiry before
    /// serving a 0-RTT local read. When <see langword="null"/>, defaults to <c>min(500 ms, LeaseDuration / 5)</c> when
    /// enabled, or <see cref="TimeSpan.Zero"/> when disabled.
    /// </summary>
    public TimeSpan? LeaseGuardBand { get; set; }

    /// <summary>Returns a copy so a consumer enforces the settings it validated whatever its caller changes later.</summary>
    internal LedgerLeaseOptions Clone() => (LedgerLeaseOptions)MemberwiseClone();

    /// <summary>
    /// Gets a value indicating whether quorum read leases are enabled.
    /// </summary>
    public bool IsEnabled => LeaseDuration > TimeSpan.Zero;

    /// <summary>
    /// Gets the effective guard band <c>δ</c> used for local read lease checks.
    /// </summary>
    public TimeSpan EffectiveGuardBand
    {
        get
        {
            if (!IsEnabled)
            {
                return TimeSpan.Zero;
            }

            if (LeaseGuardBand is { } explicitBand)
            {
                return explicitBand;
            }

            var fifth = TimeSpan.FromTicks(LeaseDuration.Ticks / 5);
            return fifth < DefaultMaxGuardBand ? fifth : DefaultMaxGuardBand;
        }
    }

    /// <summary>
    /// Gets the idle renewal interval (<c>LeaseDuration / 3</c>), or <see cref="TimeSpan.Zero"/> when disabled.
    /// </summary>
    internal TimeSpan RenewalInterval => IsEnabled
        ? TimeSpan.FromTicks(Math.Max(1L, LeaseDuration.Ticks / 3))
        : TimeSpan.Zero;

    /// <summary>
    /// Validates the lease configuration, throwing <see cref="ArgumentOutOfRangeException"/> on invalid values.
    /// </summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(LeaseDuration, TimeSpan.Zero, nameof(LeaseDuration));
        if (double.IsNaN(ClockDriftFactor) || double.IsInfinity(ClockDriftFactor) || ClockDriftFactor < 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ClockDriftFactor),
                ClockDriftFactor,
                "ClockDriftFactor must be a finite number at least 1.0.");
        }

        if (!IsEnabled)
        {
            if (LeaseGuardBand is { } band && band != TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(LeaseGuardBand),
                    band,
                    "LeaseGuardBand must be zero or null when LeaseDuration is zero.");
            }

            return;
        }

        var effectiveBand = EffectiveGuardBand;
        ArgumentOutOfRangeException.ThrowIfLessThan(effectiveBand, TimeSpan.Zero, nameof(LeaseGuardBand));
        var scaledDuration = TimeSpan.FromTicks((long)Math.Floor(LeaseDuration.Ticks / ClockDriftFactor));
        if (effectiveBand >= scaledDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LeaseGuardBand),
                effectiveBand,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Effective LeaseGuardBand ({effectiveBand}) must be strictly less than LeaseDuration / ClockDriftFactor ({scaledDuration})."));
        }
    }

    /// <summary>
    /// Converts a duration to monotonic timestamp ticks rounding down, so a primary never overestimates its local lease.
    /// </summary>
    internal static long FloorScaledTimestampTicks(TimeSpan leaseGrant, double clockDriftFactor, long timestampFrequency)
    {
        if (leaseGrant <= TimeSpan.Zero)
        {
            return 0;
        }

        var scaledTicks = (long)Math.Floor(leaseGrant.Ticks / clockDriftFactor);
        if (scaledTicks <= 0)
        {
            return 0;
        }

        if (timestampFrequency == TimeSpan.TicksPerSecond)
        {
            return scaledTicks;
        }

        var result = (UInt128)scaledTicks * (UInt128)timestampFrequency / (UInt128)TimeSpan.TicksPerSecond;
        return result > (UInt128)long.MaxValue ? long.MaxValue : (long)result;
    }

    /// <summary>
    /// Converts a duration to monotonic timestamp ticks rounding up, so a replica never underestimates a lease horizon
    /// and a primary never underestimates its guard band.
    /// </summary>
    internal static long CeilingTimestampTicks(TimeSpan duration, long timestampFrequency)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        if (timestampFrequency == TimeSpan.TicksPerSecond)
        {
            return duration.Ticks;
        }

        var numerator = (UInt128)duration.Ticks * (UInt128)timestampFrequency + (UInt128)(TimeSpan.TicksPerSecond - 1);
        var result = numerator / (UInt128)TimeSpan.TicksPerSecond;
        return result > (UInt128)long.MaxValue ? long.MaxValue : (long)result;
    }

    /// <summary>
    /// Converts monotonic timestamp ticks to a <see cref="TimeSpan"/> rounding up, so a replica's reported
    /// <c>LeaseRemaining</c> never underestimates remaining time.
    /// </summary>
    internal static TimeSpan CeilingTimestampTicksToTimeSpan(long timestampTicks, long timestampFrequency)
    {
        if (timestampTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        if (timestampFrequency == TimeSpan.TicksPerSecond)
        {
            return TimeSpan.FromTicks(timestampTicks);
        }

        var numerator = (UInt128)timestampTicks * (UInt128)TimeSpan.TicksPerSecond + (UInt128)(timestampFrequency - 1);
        var result = numerator / (UInt128)timestampFrequency;
        return TimeSpan.FromTicks(result > (UInt128)long.MaxValue ? long.MaxValue : (long)result);
    }
}
