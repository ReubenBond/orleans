using System;
using Orleans.Configuration;

namespace Orleans.Runtime.MembershipService;

internal static class MembershipTableEntryExtensions
{
    public static bool HasMissedIAmAlives(this MembershipEntry entry, ClusterMembershipOptions options, DateTimeOffset time)
        => time - entry.EffectiveIAmAliveTime > options.AllowedIAmAliveMissPeriod;

    public static DateTimeOffset LatestTime(this MembershipTableSnapshot snapshot, DateTimeOffset time)
    {
        foreach (var item in snapshot.Entries)
        {
            var otherEntry = item.Value;
            if (otherEntry.EffectiveIAmAliveTime > time)
            {
                time = otherEntry.EffectiveIAmAliveTime;
            }
        }

        return time;
    }
}
