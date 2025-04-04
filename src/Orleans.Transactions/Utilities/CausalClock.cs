
using System;

namespace Orleans.Transactions;

internal sealed class CausalClock(TimeProvider clock)
{
    private readonly object _lockable = new();
    private readonly TimeProvider _timeProvider = clock ?? throw new ArgumentNullException(nameof(clock));
    private long _previous;

    public DateTime UtcNow()
    {
        var currentTicks = _timeProvider.GetUtcNow().Ticks;
        lock (_lockable)
        {
            var ticks = _previous = Math.Max(_previous + 1, currentTicks);
            return new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public DateTime Merge(DateTime timestamp)
    {
        lock (_lockable)
        {
            var ticks = _previous = Math.Max(_previous, timestamp.Ticks);
            return new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public DateTime MergeUtcNow(DateTime timestamp)
    {
        var currentTicks = _timeProvider.GetUtcNow().Ticks;
        var maxTicks = Math.Max(currentTicks, timestamp.Ticks + 1);
        lock (_lockable)
        {
            var ticks = _previous = Math.Max(_previous + 1, maxTicks);
            return new DateTime(ticks, DateTimeKind.Utc);
        }
    }
}
