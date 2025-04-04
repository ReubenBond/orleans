
using System;

namespace Orleans.Transactions;

internal sealed class CausalClock(TimeProvider clock)
{
    private readonly object _lockable = new();
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private long _previous;

    public DateTime UtcNow()
    {
        lock (_lockable)
        {
            var ticks = _previous = Math.Max(_previous + 1, _clock.GetUtcNow().Ticks);
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
        lock (_lockable)
        {
            var ticks = _previous = Math.Max(Math.Max(_previous + 1, timestamp.Ticks + 1), _clock.GetUtcNow().Ticks);
            return new DateTime(ticks, DateTimeKind.Utc);
        }
    }
}
