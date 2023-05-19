using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Connections.Transport;
using Orleans.Runtime;
using Orleans.Runtime.Utilities;

namespace UnitTests.Directory
{
    internal class MockClusterMembershipService : IClusterMembershipService
    {
        private long version = 0;
        private Dictionary<SiloAddress, (SiloStatus Status, string Name, List<EndpointInfo> Endpoints)> statuses;
        private ClusterMembershipSnapshot snapshot;
        private readonly AsyncEnumerable<ClusterMembershipSnapshot> updates;

        ClusterMembershipSnapshot IClusterMembershipService.CurrentSnapshot => this.snapshot;

        public MembershipVersion CurrentVersion => this.snapshot.Version;

        IAsyncEnumerable<ClusterMembershipSnapshot> IClusterMembershipService.MembershipUpdates => this.updates;

        public IClusterMembershipService Target => this;

        public MockClusterMembershipService(Dictionary<SiloAddress, (SiloStatus Status, string Name, List<EndpointInfo> Endpoints)> initialStatuses = null)
        {
            this.statuses = initialStatuses ?? new Dictionary<SiloAddress, (SiloStatus Status, string Name, List<EndpointInfo> Endpoints)>();
            this.snapshot = ToSnapshot(this.statuses, ++version);
            this.updates = this.updates = new AsyncEnumerable<ClusterMembershipSnapshot>(
                (previous, proposed) => proposed.Version == MembershipVersion.MinValue || proposed.Version > previous.Version,
                this.snapshot,
                update => Interlocked.Exchange(ref this.snapshot, update));
        }

        public void UpdateSiloStatus(SiloAddress siloAddress, SiloStatus siloStatus, string name, List<EndpointInfo> endpoints = null)
        {
            this.statuses[siloAddress] = (siloStatus, name, endpoints ?? new List<EndpointInfo> { new() { Name = "silo", ["ep"] = siloAddress.Endpoint.ToString() } });
            this.updates.Publish(ToSnapshot(this.statuses, ++version));
        }

        internal static ClusterMembershipSnapshot ToSnapshot(Dictionary<SiloAddress, (SiloStatus Status, string Name, List<EndpointInfo> Endpoints)> statuses, long version)
        {
            var dictBuilder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
            foreach (var kvp in statuses)
                dictBuilder.Add(kvp.Key, new ClusterMember(kvp.Key, kvp.Value.Status, kvp.Value.Name, kvp.Value.Endpoints));

            return new ClusterMembershipSnapshot(dictBuilder.ToImmutable(), new MembershipVersion(version));
        }

        public ValueTask Refresh(MembershipVersion minimumVersion = default) => default;

        public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);
    }
}
