// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Orleans.Runtime.MembershipService
{
    internal static class MembershipTableSnapshotExtensions
    {
        internal static ClusterMembershipSnapshot CreateClusterMembershipSnapshot(this MembershipTableSnapshot membership)
        {
            var memberBuilder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
            foreach (var member in membership.Entries)
            {
                var entry = member.Value;
                memberBuilder[entry.SiloAddress] = new ClusterMember(entry.SiloAddress, entry.Status, entry.SiloName);
            }

            return new ClusterMembershipSnapshot(memberBuilder.ToImmutable(), membership.Version);
        }
    }
}
