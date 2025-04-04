using System;

namespace Orleans.Transactions.TestKit;

public class SkewedClock(TimeSpan minSkew, TimeSpan maxSkew) : TimeProvider
{
    private readonly int _skewRangeTicks = (int)(maxSkew.Ticks - minSkew.Ticks);

    public override DateTimeOffset GetUtcNow()
    {
        var skew = TimeSpan.FromTicks(minSkew.Ticks + Random.Shared.Next(_skewRangeTicks));

        // skew forward in time or backward in time
        var baseValue = base.GetUtcNow();
        return ((Random.Shared.Next() & 1) != 0)
            ? baseValue + skew
            : baseValue - skew;
    }
}
