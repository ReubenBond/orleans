namespace Orleans.Durable.Storage.Engine.Ledgers.Memory;

/// <summary>
/// Tracks one in-memory node's monotonic boot timestamp and RAM-only lease duration (LR-I10).
/// </summary>
internal sealed class InMemoryNodeLease
{
    private readonly TimeProvider _timeProvider;
    private readonly long _leaseDurationTicks;
    private long _bootTimestamp;

    internal InMemoryNodeLease(TimeProvider timeProvider, TimeSpan leaseDuration)
    {
        _timeProvider = timeProvider;
        _leaseDurationTicks = LedgerLeaseOptions.CeilingTimestampTicks(leaseDuration, timeProvider.TimestampFrequency);
        _bootTimestamp = timeProvider.GetTimestamp();
        WrittenWithLease = ReplicaWriteOutcome.Written.FromGrant(leaseDuration);
    }

    internal ReplicaWriteOutcome.Written WrittenWithLease { get; }

    internal void ResetBootTimestamp() => Volatile.Write(ref _bootTimestamp, _timeProvider.GetTimestamp());

    internal long ExtendLeaseTicks()
    {
        if (_leaseDurationTicks == 0)
        {
            return 0;
        }

        var boot = Volatile.Read(ref _bootTimestamp);
        var elapsed = Math.Max(0L, _timeProvider.GetTimestamp() - boot);
        var sum = (UInt128)elapsed + (UInt128)_leaseDurationTicks;
        return sum > (UInt128)long.MaxValue ? long.MaxValue : (long)sum;
    }

    internal TimeSpan ComputeLeaseRemaining(long grantedLeaseUntilTicks)
    {
        if (_leaseDurationTicks == 0)
        {
            return TimeSpan.Zero;
        }

        var boot = Volatile.Read(ref _bootTimestamp);
        var horizon = Math.Max(grantedLeaseUntilTicks, _leaseDurationTicks);
        var elapsed = Math.Max(0L, _timeProvider.GetTimestamp() - boot);
        var remainingTicks = horizon - elapsed;
        return remainingTicks <= 0
            ? TimeSpan.Zero
            : LedgerLeaseOptions.CeilingTimestampTicksToTimeSpan(remainingTicks, _timeProvider.TimestampFrequency);
    }
}
