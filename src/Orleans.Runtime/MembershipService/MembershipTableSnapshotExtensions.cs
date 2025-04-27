using System.Collections.Immutable;
using Orleans.Membership;

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

        internal static GatewayMembershipUpdate CreateGatewayMembershipUpdate(this MembershipTableSnapshot membership)
        {
            var memberBuilder = ImmutableArray.CreateBuilder<GatewayMembershipEntry>();
            foreach (var member in membership.Entries)
            {
                var entry = member.Value;
                if (entry.Status.IsTerminating() || entry.Status is SiloStatus.None)
                {
                    continue;
                }

                var gatewayAddress = SiloAddress.New(entry.SiloAddress.Endpoint.Address, entry.ProxyPort, entry.SiloAddress.Generation);
                memberBuilder.Add(new GatewayMembershipEntry(entry.SiloAddress, entry.Status, entry.SiloName, gatewayAddress));
            }

            return new GatewayMembershipUpdate(memberBuilder.ToImmutable(), membership.Version);
        }

        internal static GatewayMembershipUpdate CreateGatewayMembershipUpdate(this MembershipTableSnapshot membership, MembershipTableSnapshot previous)
        {
            var memberBuilder = ImmutableArray.CreateBuilder<GatewayMembershipEntry>();
            foreach (var member in membership.Entries)
            {
                var entry = member.Value;

                // Only include entries with changed statuses.
                if (previous.Entries.TryGetValue(entry.SiloAddress, out var previousEntry) && previousEntry.Status == entry.Status)
                {
                    continue;
                }

                var gatewayAddress = SiloAddress.New(entry.SiloAddress.Endpoint.Address, entry.ProxyPort, entry.SiloAddress.Generation);
                memberBuilder.Add(new GatewayMembershipEntry(entry.SiloAddress, entry.Status, entry.SiloName, gatewayAddress));
            }

            // Signal removal of entries which are no longer present in the current membership.
            foreach (var member in previous.Entries)
            {
                var entry = member.Value;
                if (!membership.Entries.TryGetValue(entry.SiloAddress, out _))
                {
                    var gatewayAddress = SiloAddress.New(entry.SiloAddress.Endpoint.Address, entry.ProxyPort, entry.SiloAddress.Generation);
                    memberBuilder.Add(new GatewayMembershipEntry(entry.SiloAddress, SiloStatus.Dead, entry.SiloName, gatewayAddress));
                }
            }

            return new GatewayMembershipUpdate(memberBuilder.ToImmutable(), membership.Version);
        }
    }
}
