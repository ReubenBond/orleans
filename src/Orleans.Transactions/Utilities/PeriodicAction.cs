using System;

namespace Orleans.Internal.Trasactions;

internal sealed class PeriodicAction(TimeSpan period, Action action, DateTime? start = null)
{
    private DateTime _nextUtc = start ?? DateTime.UtcNow + period;

    public bool TryAction(DateTime nowUtc)
    {
        if (nowUtc < _nextUtc)
        {
            return false;
        }

        _nextUtc = nowUtc + period;
        action();
        return true;
    }
}
