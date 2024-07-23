using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Runtime.Scheduler;

#nullable enable
namespace Orleans.Runtime.GrainDirectory
{
    internal sealed class LocalGrainDirectory : ILocalGrainDirectory
    {
        private readonly AdaptiveDirectoryCacheMaintainer maintainer;
        private readonly ILogger log;
        private readonly SiloAddress? seed;
        private readonly ClusterMembershipService _clusterMembershipService;
        private readonly IInternalGrainFactory grainFactory;
        private readonly object writeLock = new object();
        private Action<SiloAddress, SiloStatus>? catalogOnSiloRemoved;
        private DirectoryMembership _directoryMembership = DirectoryMembership.Default;

        // Consider: move these constants into an appropriate place
        internal const int HOP_LIMIT = 6; // forward a remote request no more than 5 times
        public static readonly TimeSpan RETRY_DELAY = TimeSpan.FromMilliseconds(200); // Pause 200ms between forwards to let the membership directory settle down

        internal bool Running;

        internal SiloAddress MyAddress { get; }

        internal IGrainDirectoryCache DirectoryCache { get; }
        internal GrainDirectoryPartition DirectoryPartition { get; }

        public RemoteGrainDirectory RemoteGrainDirectory { get; }
        public RemoteGrainDirectory CacheValidator { get; }

        public LocalGrainDirectory(
            IServiceProvider serviceProvider,
            ILocalSiloDetails siloDetails,
            ClusterMembershipService clusterMembershipService,
            IInternalGrainFactory grainFactory,
            Factory<GrainDirectoryPartition> grainDirectoryPartitionFactory,
            IOptions<DevelopmentClusterMembershipOptions> developmentClusterMembershipOptions,
            IOptions<GrainDirectoryOptions> grainDirectoryOptions,
            ILoggerFactory loggerFactory)
        {
            log = loggerFactory.CreateLogger<LocalGrainDirectory>();

            MyAddress = siloDetails.SiloAddress;

            _clusterMembershipService = clusterMembershipService;
            this.grainFactory = grainFactory;

            DirectoryCache = GrainDirectoryCacheFactory.CreateGrainDirectoryCache(serviceProvider, grainDirectoryOptions.Value);
            maintainer =
                GrainDirectoryCacheFactory.CreateGrainDirectoryCacheMaintainer(
                    this,
                    DirectoryCache,
                    grainFactory,
                    loggerFactory);

            var primarySiloEndPoint = developmentClusterMembershipOptions.Value.PrimarySiloEndpoint;
            if (primarySiloEndPoint != null)
            {
                seed = MyAddress.Endpoint.Equals(primarySiloEndPoint) ? MyAddress : SiloAddress.New(primarySiloEndPoint, 0);
            }

            DirectoryPartition = grainDirectoryPartitionFactory();

            RemoteGrainDirectory = new RemoteGrainDirectory(this, Constants.DirectoryServiceType, loggerFactory);
            CacheValidator = new RemoteGrainDirectory(this, Constants.DirectoryCacheValidatorType, loggerFactory);

            // add myself to the list of members
            AddServer(MyAddress);

            DirectoryInstruments.RegisterDirectoryPartitionSizeObserve(() => DirectoryPartition.Count);
            DirectoryInstruments.RegisterMyPortionRingDistanceObserve(() => RingDistanceToSuccessor());
            DirectoryInstruments.RegisterMyPortionRingPercentageObserve(() => RingDistanceToSuccessor() / (float)(int.MaxValue * 2L) * 100);
            DirectoryInstruments.RegisterMyPortionAverageRingPercentageObserve(() =>
            {
                var ring = _directoryMembership.Members;
                return ring.Length == 0 ? 0 : (100 / (float)ring.Length);
            });
            DirectoryInstruments.RegisterRingSizeObserve(() => _directoryMembership.Members.Length);
        }

        public void Start()
        {
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug("Start");
            }

            Running = true;
            if (maintainer != null)
            {
                CacheValidator.WorkItemGroup.QueueAction(maintainer.Start);
            }
        }

        // Note that this implementation stops processing directory change requests (Register, Unregister, etc.) when the Stop event is raised.
        // This means that there may be a short period during which no silo believes that it is the owner of directory information for a set of
        // grains (for update purposes), which could cause application requests that require a new activation to be created to time out.
        // The alternative would be to allow the silo to process requests after it has handed off its partition, in which case those changes
        // would receive successful responses but would not be reflected in the eventual state of the directory.
        // It's easy to change this, if we think the trade-off is better the other way.
        public async Task StopAsync()
        {
            // This will cause remote write requests to be forwarded to the silo that will become the new owner.
            // Requests might bounce back and forth for a while as membership stabilizes, but they will either be served by the
            // new owner of the grain, or will wind up failing. In either case, we avoid requests succeeding at this silo after we've
            // begun stopping, which could cause them to not get handed off to the new owner.

            //mark Running as false will exclude myself from CalculateGrainDirectoryPartition(grainId)
            Running = false;

            if (maintainer is { } directoryCacheMaintainer)
            {
                await CacheValidator.QueueTask(directoryCacheMaintainer.StopAsync);
            }

            DirectoryPartition.Clear();
            DirectoryCache.Clear();
        }

        /// <inheritdoc />
        public void SetSiloRemovedCatalogCallback(Action<SiloAddress, SiloStatus> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (writeLock)
            {
                catalogOnSiloRemoved = callback;
            }
        }

        private async Task ProcessMembershipUpdates()
        {
            while (true)
            {
                await foreach (var snapshot in _clusterMembershipService.MembershipUpdates)
                {
                    var previous = _directoryMembership;
                    var current = new DirectoryMembership(snapshot);
                    _directoryMembership = current;

                    var currentContainsSelf = current.Contains(MyAddress);
                    var previousContainsSelf = previous.Contains(MyAddress);

                    if (currentContainsSelf && !previousContainsSelf)
                    {
                        // We just became active
                    }
                    else if (!currentContainsSelf && previousContainsSelf)
                    {
                        // We just became inactive
                    }
                    else if (!currentContainsSelf && !previousContainsSelf)
                    {
                        // We are not active, and we were not active
                    }
                    else
                    {
                    }

                    var previousRange = previous.GetRingRange(MyAddress);
                    var currentRange = current.GetRingRange(MyAddress);
                    var removedRange = previousRange.Remove(currentRange);
                    var addedRange = currentRange.Remove(previousRange);
                }
            }
        }

        private void AddServer(SiloAddress silo)
        {
            lock (writeLock)
            {
                var existing = _directoryMembership;
                if (existing.Contains(silo))
                {
                    // we have already cached this silo
                    return;
                }

                // insert new silo in the sorted order
                long hash = silo.GetConsistentHashCode();

                // Find the last silo with hash smaller than the new silo, and insert the latter after (this is why we have +1 here) the former.
                // Notice that FindLastIndex might return -1 if this should be the first silo in the list, but then
                // 'index' will get 0, as needed.
                int index = existing.Members.FindLastIndex(siloAddr => siloAddr.GetConsistentHashCode() < hash) + 1;

                _directoryMembership = new DirectoryMembership(
                    existing.Members.Insert(index, silo),
                    existing.MembershipCache.Add(silo));

                HandoffManager.ProcessSiloAddEvent(silo);

                AdjustLocalDirectory(silo, dead: false);
                AdjustLocalCache(silo, dead: false);

                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Silo {SiloAddress} added silo {OtherSiloAddress}", MyAddress, silo);
            }
        }

        private void RemoveServer(SiloAddress silo, SiloStatus status)
        {
            lock (writeLock)
            {
                try
                {
                    // Only notify the catalog once. Order is important: call BEFORE updating membershipRingList.
                    catalogOnSiloRemoved?.Invoke(silo, status);
                }
                catch (Exception exc)
                {
                    log.LogError(
                        (int)ErrorCode.Directory_SiloStatusChangeNotification_Exception,
                        exc,
                        "CatalogSiloStatusListener.SiloStatusChangeNotification has thrown an exception when notified about removed silo {Silo}.",
                        silo.ToStringWithHashCode());
                }

                var existing = _directoryMembership;
                if (!existing.Contains(silo))
                {
                    // we have already removed this silo
                    return;
                }

                _directoryMembership = new DirectoryMembership(
                    existing.Members.Remove(silo),
                    existing.MembershipCache.Remove(silo));

                AdjustLocalDirectory(silo, dead: true);
                AdjustLocalCache(silo, dead: true);

                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Silo {LocalSilo} removed silo {OtherSilo}", MyAddress, silo);
            }
        }

        /// <summary>
        /// Adjust local directory following the addition/removal of a silo
        /// </summary>
        private void AdjustLocalDirectory(SiloAddress silo, bool dead)
        {
            // Determine which activations to remove.
            var activationsToRemove = new List<(GrainId, ActivationId)>();
            foreach (var entry in DirectoryPartition.GetItems())
            {
                if (entry.Value.Activation is { } address)
                {
                    // Include any activations from dead silos and from predecessors.
                    if (dead && address.SiloAddress!.Equals(silo) || address.SiloAddress!.IsPredecessorOf(silo))
                    {
                        activationsToRemove.Add((entry.Key, address.ActivationId));
                    }
                }
            }

            // Remove all defunct activations.
            foreach (var activation in activationsToRemove)
            {
                DirectoryPartition.RemoveActivation(activation.Item1, activation.Item2);
            }
        }

        /// Adjust local cache following the removal of a silo by dropping:
        /// 1) entries that point to activations located on the removed silo
        /// 2) entries for grains that are now owned by this silo (me)
        /// 3) entries for grains that were owned by this removed silo - we currently do NOT do that.
        ///     If we did 3, we need to do that BEFORE we change the membershipRingList (based on old Membership).
        ///     We don't do that since first cache refresh handles that.
        ///     Second, since Membership events are not guaranteed to be ordered, we may remove a cache entry that does not really point to a failed silo.
        ///     To do that properly, we need to store for each cache entry who was the directory owner that registered this activation (the original partition owner).
        private void AdjustLocalCache(SiloAddress silo, bool dead)
        {
            // remove all records of activations located on the removed silo
            foreach (var tuple in DirectoryCache.KeyValues)
            {
                var activationAddress = tuple.ActivationAddress;

                // 2) remove entries now owned by me (they should be retrieved from my directory partition)
                if (MyAddress.Equals(CalculateGrainDirectoryPartition(activationAddress.GrainId)))
                {
                    DirectoryCache.Remove(activationAddress.GrainId);
                    continue;
                }

                // 1) remove entries that point to activations located on the removed silo
                // For dead silos, remove any activation registered to that silo or one of its predecessors.
                // For new silos, remove any activation registered to one of its predecessors.
                if (activationAddress.SiloAddress!.IsPredecessorOf(silo) || dead && activationAddress.SiloAddress.Equals(silo))
                {
                    DirectoryCache.Remove(activationAddress.GrainId);
                }
            }
        }

        internal SiloAddress? FindSuccessor(SiloAddress silo)
        {
            var existing = _directoryMembership.Members;
            int index = existing.IndexOf(silo);
            if (index == -1)
            {
                log.LogWarning(
                    (int)ErrorCode.Runtime_Error_100203,
                    "Got request to find successors of silo {SiloAddress}, which is not in the list of members",
                    silo);
                return null;
            }

            return existing.Length > 1 ? existing[(index + 1) % existing.Length] : null;
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            // This silo's status has changed
            if (!Equals(updatedSilo, MyAddress)) // Status change for some other silo
            {
                if (status.IsTerminating())
                {
                    // QueueAction up the "Remove" to run on a system turn
                    CacheValidator.WorkItemGroup.QueueAction(() => RemoveServer(updatedSilo, status));
                }
                else if (status == SiloStatus.Active)      // do not do anything with SiloStatus.Starting -- wait until it actually becomes active
                {
                    // QueueAction up the "Remove" to run on a system turn
                    CacheValidator.WorkItemGroup.QueueAction(() => AddServer(updatedSilo));
                }
            }
        }

        private bool IsValidSilo(SiloAddress? silo) => silo is not null && _directoryMembership.Contains(silo);

        /// <summary>
        /// Finds the silo that owns the directory information for the given grain ID.
        /// This method will only be null when I'm the only silo in the cluster and I'm shutting down
        /// </summary>
        /// <param name="grainId"></param>
        /// <returns></returns>
        public SiloAddress? CalculateGrainDirectoryPartition(GrainId grainId)
        {
            // give a special treatment for special grains
            if (grainId.IsSystemTarget())
            {
                if (Constants.SystemMembershipTableType.Equals(grainId.Type))
                {
                    if (seed == null)
                    {
                        var errorMsg =
                            $"Development clustering cannot run without a primary silo. " +
                            $"Please configure {nameof(DevelopmentClusterMembershipOptions)}.{nameof(DevelopmentClusterMembershipOptions.PrimarySiloEndpoint)} " +
                            "or provide a primary silo address to the UseDevelopmentClustering extension. " +
                            "Alternatively, you may want to use reliable membership, such as Azure Table.";
                        throw new ArgumentException(errorMsg, "grainId = " + grainId);
                    }
                }

                if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("Silo {SiloAddress} looked for a system target {GrainId}, returned {TargetSilo}", MyAddress, grainId, MyAddress);

                // every silo owns its system targets
                return MyAddress;
            }

            var hash = grainId.GetUniformHashCode();
            if (!_directoryMembership.TryGetOwner(hash, out var siloAddress))
            {
                // No owner is available.
                return null;
            }

            if (log.IsEnabled(LogLevel.Trace))
            {
                log.LogTrace(
                    "Silo {SiloAddress} calculated directory partition owner silo {OwnerAddress} for grain {GrainId}: {GrainIdHash} --> {OwnerAddressHash}",
                    MyAddress,
                    siloAddress,
                    grainId,
                    hash,
                    siloAddress?.GetConsistentHashCode());
            }

            return siloAddress;
        }

        public SiloAddress? CheckIfShouldForward(GrainId grainId, int hopCount, string operationDescription)
        {
            var owner = CalculateGrainDirectoryPartition(grainId);

            if (owner is null || owner.Equals(MyAddress))
            {
                // Either we don't know about any other silos and we're stopping, or we are the owner.
                // Null indicates that the operation should be performed locally.
                // In the case that this host is terminating, any grain registered to this host must terminate.
                return null;
            }

            if (hopCount >= HOP_LIMIT)
            {
                // we are not forwarding because there were too many hops already
                throw new OrleansException($"Silo {MyAddress} is not owner of {grainId}, cannot forward {operationDescription} to owner {owner} because hop limit is reached");
            }

            // forward to the silo that we think is the owner
            return owner;
        }

        public Task<AddressAndTag> RegisterAsync(GrainAddress address, int hopCount) => RegisterAsync(address, previousAddress: null, hopCount: hopCount);

        public async Task<AddressAndTag> RegisterAsync(GrainAddress address, GrainAddress? previousAddress, int hopCount)
        {
            if (hopCount > 0)
            {
                DirectoryInstruments.RegistrationsSingleActRemoteReceived.Add(1);
            }
            else
            {
                DirectoryInstruments.RegistrationsSingleActIssued.Add(1);
            }

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = CheckIfShouldForward(address.GrainId, hopCount, "RegisterAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = CheckIfShouldForward(address.GrainId, hopCount, "RegisterAsync");
                if (forwardAddress is not null)
                {
                    int hash = unchecked((int)address.GrainId.GetUniformHashCode());
                    log.LogWarning(
                        "RegisterAsync - It seems we are not the owner of activation {Address} (hash: {Hash}), trying to forward it to {ForwardAddress} (hopCount={HopCount})",
                        address,
                        hash.ToString("X"),
                        forwardAddress,
                        hopCount);
                }
            }

            if (forwardAddress == null)
            {
                DirectoryInstruments.RegistrationsSingleActLocal.Add(1);

                var result = DirectoryPartition.AddSingleActivation(address, previousAddress);

                // update the cache so next local lookup will find this ActivationAddress in the cache and we will save full lookup.
                DirectoryCache.AddOrUpdate(result.Address, result.VersionTag);
                return result;
            }
            else
            {
                DirectoryInstruments.RegistrationsSingleActRemoteSent.Add(1);

                // otherwise, notify the owner
                AddressAndTag result = await GetDirectoryReference(forwardAddress).RegisterAsync(address, previousAddress, hopCount + 1);

                // Caching optimization:
                // cache the result of a successful RegisterSingleActivation call, only if it is not a duplicate activation.
                // this way next local lookup will find this ActivationAddress in the cache and we will save a full lookup!
                if (result.Address == null) return result;

                if (!address.Equals(result.Address) || !IsValidSilo(address.SiloAddress)) return result;

                // update the cache so next local lookup will find this ActivationAddress in the cache and we will save full lookup.
                DirectoryCache.AddOrUpdate(result.Address, result.VersionTag);

                return result;
            }
        }

        public Task UnregisterAfterNonexistingActivation(GrainAddress addr, SiloAddress origin)
        {
            log.LogTrace("UnregisterAfterNonexistingActivation addr={Address} origin={Origin}", addr, origin);

            if (origin == null || _directoryMembership.Contains(origin))
            {
                // the request originated in this cluster, call unregister here
                return UnregisterAsync(addr, UnregistrationCause.NonexistentActivation, 0);
            }
            else
            {
                // the request originated in another cluster, call unregister there
                var remoteDirectory = GetDirectoryReference(origin);
                return remoteDirectory.UnregisterAsync(addr, UnregistrationCause.NonexistentActivation);
            }
        }

        public async Task UnregisterAsync(GrainAddress address, UnregistrationCause cause, int hopCount)
        {
            if (hopCount > 0)
            {
                DirectoryInstruments.UnregistrationsRemoteReceived.Add(1);
            }
            else
            {
                DirectoryInstruments.UnregistrationsIssued.Add(1);
            }

            if (hopCount == 0)
                InvalidateCacheEntry(address);

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = CheckIfShouldForward(address.GrainId, hopCount, "UnregisterAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = CheckIfShouldForward(address.GrainId, hopCount, "UnregisterAsync");
                log.LogWarning(
                    "UnregisterAsync - It seems we are not the owner of activation {Address}, trying to forward it to {ForwardAddress} (hopCount={HopCount})",
                    address,
                    forwardAddress,
                    hopCount);
            }

            if (forwardAddress == null)
            {
                // we are the owner
                DirectoryInstruments.UnregistrationsLocal.Add(1);
                DirectoryPartition.RemoveActivation(address.GrainId, address.ActivationId, cause);
            }
            else
            {
                DirectoryInstruments.UnregistrationsRemoteSent.Add(1);
                // otherwise, notify the owner
                await GetDirectoryReference(forwardAddress).UnregisterAsync(address, cause, hopCount + 1);
            }
        }

        // helper method to avoid code duplication inside UnregisterManyAsync
        private void UnregisterOrPutInForwardList(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount,
            ref Dictionary<SiloAddress, List<GrainAddress>>? forward, string context)
        {
            foreach (var address in addresses)
            {
                // see if the owner is somewhere else (returns null if we are owner)
                var forwardAddress = CheckIfShouldForward(address.GrainId, hopCount, context);

                if (forwardAddress != null)
                {
                    forward ??= new();
                    if (!forward.TryGetValue(forwardAddress, out var list))
                        forward[forwardAddress] = list = new();
                    list.Add(address);
                }
                else
                {
                    // we are the owner
                    DirectoryInstruments.UnregistrationsLocal.Add(1);

                    DirectoryPartition.RemoveActivation(address.GrainId, address.ActivationId, cause);
                }
            }
        }


        public async Task UnregisterManyAsync(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount)
        {
            if (hopCount > 0)
            {
                DirectoryInstruments.UnregistrationsManyRemoteReceived.Add(1);
            }
            else
            {
                DirectoryInstruments.UnregistrationsManyIssued.Add(1);
            }

            Dictionary<SiloAddress, List<GrainAddress>>? forwardlist = null;

            UnregisterOrPutInForwardList(addresses, cause, hopCount, ref forwardlist, "UnregisterManyAsync");

            // before forwarding to other silos, we insert a retry delay and re-check destination
            if (hopCount > 0 && forwardlist != null)
            {
                await Task.Delay(RETRY_DELAY);
                Dictionary<SiloAddress, List<GrainAddress>>? forwardlist2 = null;
                UnregisterOrPutInForwardList(addresses, cause, hopCount, ref forwardlist2, "UnregisterManyAsync");
                forwardlist = forwardlist2;
                if (forwardlist != null)
                {
                    log.LogWarning(
                        "RegisterAsync - It seems we are not the owner of some activations, trying to forward it to {Count} silos (hopCount={HopCount})",
                        forwardlist.Count,
                        hopCount);
                }
            }

            // forward the requests
            if (forwardlist != null)
            {
                var tasks = new List<Task>();
                foreach (var kvp in forwardlist)
                {
                    DirectoryInstruments.UnregistrationsManyRemoteSent.Add(1);
                    tasks.Add(GetDirectoryReference(kvp.Key).UnregisterManyAsync(kvp.Value, cause, hopCount + 1));
                }

                // wait for all the requests to finish
                await Task.WhenAll(tasks);
            }
        }


        public bool LocalLookup(GrainId grain, out AddressAndTag result)
        {
            DirectoryInstruments.LookupsLocalIssued.Add(1);

            var silo = CalculateGrainDirectoryPartition(grain);

            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug(
                    "Silo {SiloAddress} tries to lookup for {Grain}-->{PartitionOwner} ({GrainHashCode}-->{PartitionOwnerHashCode})",
                    MyAddress,
                    grain,
                    silo,
                    grain.GetUniformHashCode(),
                    silo?.GetConsistentHashCode());

            //this will only happen if I'm the only silo in the cluster and I'm shutting down
            if (silo == null)
            {
                if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("LocalLookup mine {GrainId}=null", grain);
                result = default;
                return false;
            }

            // handle cache
            DirectoryInstruments.LookupsCacheIssued.Add(1);
            var address = GetLocalCacheData(grain);
            if (address != default)
            {
                result = new(address, 0);

                if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("LocalLookup cache {GrainId}={TargetAddress}", grain, result.Address);
                DirectoryInstruments.LookupsCacheSuccesses.Add(1);
                DirectoryInstruments.LookupsLocalSuccesses.Add(1);
                return true;
            }

            // check if we own the grain
            if (silo.Equals(MyAddress))
            {
                DirectoryInstruments.LookupsLocalDirectoryIssued.Add(1);
                result = GetLocalDirectoryData(grain);
                if (result.Address == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were
                    // some recent changes in the membership
                    if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("LocalLookup mine {GrainId}=null", grain);
                    return false;
                }
                if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("LocalLookup mine {GrainId}={Address}", grain, result.Address);
                DirectoryInstruments.LookupsLocalDirectorySuccesses.Add(1);
                DirectoryInstruments.LookupsLocalSuccesses.Add(1);
                return true;
            }

            if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("TryFullLookup else {GrainId}=null", grain);
            result = default;
            return false;
        }

        public AddressAndTag GetLocalDirectoryData(GrainId grain) => DirectoryPartition.LookUpActivation(grain);

        public GrainAddress? GetLocalCacheData(GrainId grain) => DirectoryCache.LookUp(grain, out var cache) && IsValidSilo(cache.SiloAddress) ? cache : null;

        public async Task<AddressAndTag> LookupAsync(GrainId grainId, int hopCount = 0)
        {
            if (hopCount > 0)
            {
                DirectoryInstruments.LookupsRemoteReceived.Add(1);
            }
            else
            {
                DirectoryInstruments.LookupsFullIssued.Add(1);
            }

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = CheckIfShouldForward(grainId, hopCount, "LookUpAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = CheckIfShouldForward(grainId, hopCount, "LookUpAsync");
                if (forwardAddress is not null)
                {
                    int hash = unchecked((int)grainId.GetUniformHashCode());
                    log.LogWarning(
                        "LookupAsync - It seems we are not the owner of grain {GrainId} (hash: {Hash}), trying to forward it to {ForwardAddress} (hopCount={HopCount})",
                        grainId,
                        hash.ToString("X"),
                        forwardAddress,
                        hopCount);
                }
            }

            if (forwardAddress == null)
            {
                // we are the owner
                DirectoryInstruments.LookupsLocalDirectoryIssued.Add(1);
                var localResult = DirectoryPartition.LookUpActivation(grainId);
                if (localResult.Address == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were
                    // some recent changes in the membership
                    if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("FullLookup mine {GrainId}=none", grainId);
                    return new(default, GrainInfo.NO_ETAG);
                }

                if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("FullLookup mine {GrainId}={Address}", grainId, localResult.Address);
                DirectoryInstruments.LookupsLocalDirectorySuccesses.Add(1);
                return localResult;
            }
            else
            {
                // Just a optimization. Why sending a message to someone we know is not valid.
                if (!IsValidSilo(forwardAddress))
                {
                    throw new OrleansException($"Current directory at {MyAddress} is not stable to perform the lookup for grainId {grainId} (it maps to {forwardAddress}, which is not a valid silo). Retry later.");
                }

                DirectoryInstruments.LookupsRemoteSent.Add(1);
                var result = await GetDirectoryReference(forwardAddress).LookupAsync(grainId, hopCount + 1);

                // update the cache
                if (result.Address is { } address && IsValidSilo(address.SiloAddress))
                {
                    DirectoryCache.AddOrUpdate(address, result.VersionTag);
                }

                if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("FullLookup remote {GrainId}={Address}", grainId, result.Address);

                return result;
            }
        }

        public async Task DeleteGrainAsync(GrainId grainId, int hopCount)
        {
            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = CheckIfShouldForward(grainId, hopCount, "DeleteGrainAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = CheckIfShouldForward(grainId, hopCount, "DeleteGrainAsync");
                log.LogWarning(
                    "DeleteGrainAsync - It seems we are not the owner of grain {GrainId}, trying to forward it to {ForwardAddress} (hopCount={HopCount})",
                    grainId,
                    forwardAddress,
                    hopCount);
            }

            if (forwardAddress == null)
            {
                // we are the owner
                DirectoryPartition.RemoveGrain(grainId);
            }
            else
            {
                // otherwise, notify the owner
                DirectoryCache.Remove(grainId);
                await GetDirectoryReference(forwardAddress).DeleteGrainAsync(grainId, hopCount + 1);
            }
        }

        public void InvalidateCacheEntry(GrainId grainId)
        {
            DirectoryCache.Remove(grainId);
        }

        public void InvalidateCacheEntry(GrainAddress activationAddress)
        {
            DirectoryCache.Remove(activationAddress);
        }

        /// <summary>
        /// For testing purposes only.
        /// Returns the silo that this silo thinks is the primary owner of directory information for
        /// the provided grain ID.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        public SiloAddress? GetPrimaryForGrain(GrainId grain) => CalculateGrainDirectoryPartition(grain);

        private long RingDistanceToSuccessor() => FindSuccessor(MyAddress) is { } successor ? CalcRingDistance(MyAddress, successor) : 0;

        private static long CalcRingDistance(SiloAddress silo1, SiloAddress silo2)
        {
            const long ringSize = int.MaxValue * 2L;
            long hash1 = silo1.GetConsistentHashCode();
            long hash2 = silo2.GetConsistentHashCode();

            if (hash2 > hash1) return hash2 - hash1;
            if (hash2 < hash1) return ringSize - (hash1 - hash2);

            return 0;
        }

        internal IRemoteGrainDirectory GetDirectoryReference(SiloAddress silo) => grainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryServiceType, silo);

        public void AddOrUpdateCacheEntry(GrainId grainId, SiloAddress siloAddress) => DirectoryCache.AddOrUpdate(new GrainAddress { GrainId = grainId, SiloAddress = siloAddress }, 0);
        public bool TryCachedLookup(GrainId grainId, [NotNullWhen(true)] out GrainAddress? address) => (address = GetLocalCacheData(grainId)) is not null;

        internal sealed class DirectoryMembership
        {
            private readonly ClusterMembershipSnapshot _snapshot;

            public DirectoryMembership(ClusterMembershipSnapshot snapshot)
            {
                var sortedActiveMembers = ImmutableArray.CreateBuilder<SiloAddress>(snapshot.Members.Count(static m => m.Value.Status == SiloStatus.Active));
                foreach (var member in snapshot.Members)
                {
                    // Only active members are part of directory membership.
                    if (member.Value.Status == SiloStatus.Active)
                    {
                        sortedActiveMembers.Add(member.Key);
                    }
                }

                sortedActiveMembers.Sort(static (left, right) => left.GetConsistentHashCode().CompareTo(right.GetConsistentHashCode()));

                var memberRanges = ImmutableArray.CreateBuilder<SingleRange>(sortedActiveMembers.Count);
                for (var i = 0; i < sortedActiveMembers.Count; i++)
                {
                    var memberRange = RangeFactory.GetEquallyDividedSubRange(RangeFactory.FullRange, sortedActiveMembers.Count, i);
                    memberRanges.Add(memberRange);
                }

                Members = sortedActiveMembers.ToImmutable();
                Ranges = memberRanges.ToImmutable();
                _snapshot = snapshot;
            }

            public static DirectoryMembership Default { get; } = new DirectoryMembership(
                new ClusterMembershipSnapshot(ImmutableDictionary<SiloAddress, ClusterMember>.Empty, MembershipVersion.MinValue));

            public MembershipVersion Version => _snapshot.Version;

            public ImmutableArray<SiloAddress> Members { get; }

            public ImmutableArray<SingleRange> Ranges { get; }

            public bool Contains(SiloAddress address) => TryGetMemberIndex(address) >= 0;

            public RingRange GetRingRange(SiloAddress address)
            {
                var index = TryGetMemberIndex(address);

                if (index < 0)
                {
                    return RingRange.Empty;
                }

                return Ranges[index];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private int TryGetMemberIndex(SiloAddress address)
            {
                return BinarySearch(
                    Members,
                    address,
                    static (candidate, address) =>
                    {
                        var comparison = candidate.GetConsistentHashCode().CompareTo(address.GetConsistentHashCode());
                        if (comparison != 0)
                        {
                            return comparison;
                        }

                        if (candidate.Equals(address))
                        {
                            return 0;
                        }

                        return candidate.CompareTo(address);
                    });
            }

            public bool TryGetOwner(GrainId grainId, out SiloAddress? owner) => TryGetOwner(grainId.GetUniformHashCode(), out owner);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryGetOwner(uint hashCode, out SiloAddress? owner)
            {
                var index = BinarySearch(Ranges, hashCode, static (range, hashCode) => range.Compare(hashCode));
                if (index >= 0)
                {
                    owner = Members[index];
                    return true;
                }

                owner = null;
                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static int BinarySearch<T, U>(ImmutableArray<T> haystack, U needle, Func<T, U, int> comparer)
            {
                var left = 0;
                var right = haystack.Length - 1;

                while (left <= right)
                {
                    var mid = left + (right - left) / 2;
                    var comparison = comparer(haystack[mid], needle);

                    if (comparison == 0)
                    {
                        return mid;
                    }
                    else if (comparison < 0)
                    {
                        left = mid + 1;
                    }
                    else
                    {
                        right = mid - 1;
                    }
                }

                return -1;
            }
        }
    }
}
