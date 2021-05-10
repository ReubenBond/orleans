using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Collections.Immutable;

namespace Orleans.Runtime.MembershipService
{
    internal class SiloStatusOracle : ISiloStatusOracle
    {
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly IClusterMembershipService  clusterMembershipService;
        private readonly SiloStatusListenerManager listenerManager;
        private readonly ILogger log;
        private readonly object cacheUpdateLock = new object();
        private ClusterMembershipSnapshot cachedSnapshot;
        private Dictionary<SiloAddress, SiloStatus> siloStatusCache = new Dictionary<SiloAddress, SiloStatus>();
        private Dictionary<SiloAddress, SiloStatus> siloStatusCacheOnlyActive = new Dictionary<SiloAddress, SiloStatus>();
        private ImmutableArray<SiloAddress> _activeSilos = [];

        public SiloStatusOracle(
            ILocalSiloDetails localSiloDetails,
            IClusterMembershipService clusterMembershipService,
            ILogger<SiloStatusOracle> logger,
            SiloStatusListenerManager listenerManager)
        {
            this.localSiloDetails = localSiloDetails;
            this.clusterMembershipService = clusterMembershipService;
            this.listenerManager = listenerManager;
            this.log = logger;
        }

        public SiloStatus CurrentStatus => this.clusterMembershipService.CurrentStatus;
        public string SiloName => this.localSiloDetails.Name;
        public SiloAddress SiloAddress => this.localSiloDetails.SiloAddress;

        public SiloStatus GetSiloStatus(SiloAddress silo)
        {
            var status = this.clusterMembershipService.CurrentSnapshot.GetSiloStatus(silo);

            if (status == SiloStatus.None)
            {
                if (this.CurrentStatus == SiloStatus.Active && this.log.IsEnabled(LogLevel.Debug))
                {
                    this.log.LogDebug(
                        (int)ErrorCode.Runtime_Error_100209,
                        "The given SiloAddress {SiloAddress} is not registered in this MembershipOracle.",
                        silo);
                }
            }

            return status;
        }

        public ImmutableArray<SiloAddress> GetActiveSilos()
        {
            EnsureFreshCache();
            return _activeSilos;
        }

        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            EnsureFreshCache();
            return onlyActive ? this.siloStatusCacheOnlyActive : this.siloStatusCache;
        }

        private void EnsureFreshCache()
        {
            var currentMembership = this.membershipTableManager.MembershipTableSnapshot;
            if (ReferenceEquals(this.cachedSnapshot, currentMembership))
            {
                return;
            }

            lock (this.cacheUpdateLock)
            {
                currentMembership = this.membershipTableManager.MembershipTableSnapshot;
                if (ReferenceEquals(this.cachedSnapshot, currentMembership))
                {
                    return;
                }

                var newSiloStatusCache = new Dictionary<SiloAddress, SiloStatus>();
                var newSiloStatusCacheOnlyActive = new Dictionary<SiloAddress, SiloStatus>();
                var newActiveSilos = ImmutableArray.CreateBuilder<SiloAddress>();
                foreach (var entry in currentMembership.Entries)
                {
                    var silo = entry.Key;
                    var status = entry.Value.Status;
                    newSiloStatusCache[silo] = status;
                    if (status == SiloStatus.Active)
                    {
                        newSiloStatusCacheOnlyActive[silo] = status;
                        newActiveSilos.Add(silo);
                    }
                }

                Interlocked.Exchange(ref this.cachedSnapshot, currentMembership);
                this.siloStatusCache = newSiloStatusCache;
                this.siloStatusCacheOnlyActive = newSiloStatusCacheOnlyActive;
                _activeSilos = newActiveSilos.ToImmutable();
            }
        }

        public bool IsDeadSilo(SiloAddress silo)
        {
            if (silo.Equals(this.SiloAddress)) return false;

            var status = this.GetSiloStatus(silo);

            return status == SiloStatus.Dead;
        }

        public bool IsFunctionalDirectory(SiloAddress silo)
        {
            if (silo.Equals(this.SiloAddress)) return true;

            var status = this.GetSiloStatus(silo);
            return !status.IsTerminating();
        }

        public bool TryGetSiloName(SiloAddress siloAddress, out string siloName)
        {
            var snapshot = this.clusterMembershipService.CurrentSnapshot.Members;
            if (snapshot.TryGetValue(siloAddress, out var entry))
            {
                siloName = entry.Name;
                return true;
            }

            siloName = default;
            return false;
        }

        public bool SubscribeToSiloStatusEvents(ISiloStatusListener listener) => this.listenerManager.Subscribe(listener);

        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener listener) => this.listenerManager.Unsubscribe(listener);
    }
}
