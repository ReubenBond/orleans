// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Configuration;

namespace Orleans.Runtime.MembershipService;

internal static class MembershipTableEntryExtensions
{
    public static bool HasMissedIAmAlives(this MembershipEntry entry, ClusterMembershipOptions options, DateTime time)
        => time - entry.EffectiveIAmAliveTime > options.AllowedIAmAliveMissPeriod;
}
