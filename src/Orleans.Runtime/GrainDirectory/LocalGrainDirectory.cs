using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.GrainDirectory;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.MultiClusterNetwork;
using Orleans.Configuration;
using Orleans.Runtime.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Providers;
using Orleans.Timers.Internal;

namespace Orleans.Runtime.GrainDirectory
{
    internal class LocalGrainDirectory : ILocalGrainDirectory, ILifecycleParticipant<ISiloLifecycle>, IDisposable
    {
        private readonly DedicatedAsynchAgent maintainer;
        private readonly ILogger log;
        private readonly RegistrarManager registrarManager;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly IClusterMembershipService clusterMembership;
        private readonly IMultiClusterOracle multiClusterOracle;
        private readonly IInternalGrainFactory grainFactory;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly IServiceProvider serviceProvider;
        private readonly AsyncEnumerable<DirectoryMembershipSnapshot> directoryMembershipUpdates;
        private readonly string clusterId;
        private bool initialStabilizationComplete;
        private Catalog catalog;
        private DirectoryMembershipSnapshot membershipSnapshot;

        // Consider: move these constants into an apropriate place
        internal const int HOP_LIMIT = 6; // forward a remote request no more than 5 times
        public DirectoryMembershipSnapshot DirectoryMembershipSnapshot => this.membershipSnapshot;
        private SiloAddress MyAddress { get; set; }

        internal IGrainDirectoryCache DirectoryCache { get; private set; }
        internal GrainDirectoryPartition DirectoryPartition { get; private set; }

        public RemoteGrainDirectory RemoteGrainDirectory { get; private set; }
        public RemoteGrainDirectory CacheValidator { get; private set; }
        public ClusterGrainDirectory RemoteClusterGrainDirectory { get; private set; }

        internal OrleansTaskScheduler Scheduler { get; private set; }

        internal GrainDirectoryHandoffManager HandoffManager { get; private set; }

        internal GlobalSingleInstanceActivationMaintainer GsiActivationMaintainer { get; private set; }

        private readonly CounterStatistic localLookups;
        private readonly CounterStatistic localSuccesses;
        private readonly CounterStatistic fullLookups;
        private readonly CounterStatistic cacheLookups;
        private readonly CounterStatistic cacheSuccesses;
        private readonly CounterStatistic registrationsIssued;
        private readonly CounterStatistic registrationsSingleActIssued;
        private readonly CounterStatistic unregistrationsIssued;
        private readonly CounterStatistic unregistrationsManyIssued;
        private readonly IntValueStatistic directoryPartitionCount;

        internal readonly CounterStatistic RemoteLookupsSent;
        internal readonly CounterStatistic RemoteLookupsReceived;
        internal readonly CounterStatistic LocalDirectoryLookups;
        internal readonly CounterStatistic LocalDirectorySuccesses;
        internal readonly CounterStatistic CacheValidationsSent;
        internal readonly CounterStatistic CacheValidationsReceived;
        internal readonly CounterStatistic RegistrationsLocal;
        internal readonly CounterStatistic RegistrationsRemoteSent;
        internal readonly CounterStatistic RegistrationsRemoteReceived;
        internal readonly CounterStatistic RegistrationsSingleActLocal;
        internal readonly CounterStatistic RegistrationsSingleActRemoteSent;
        internal readonly CounterStatistic RegistrationsSingleActRemoteReceived;
        internal readonly CounterStatistic UnregistrationsLocal;
        internal readonly CounterStatistic UnregistrationsRemoteSent;
        internal readonly CounterStatistic UnregistrationsRemoteReceived;
        internal readonly CounterStatistic UnregistrationsManyRemoteSent;
        internal readonly CounterStatistic UnregistrationsManyRemoteReceived;

        public LocalGrainDirectory(
            ILocalSiloDetails siloDetails,
            OrleansTaskScheduler scheduler,
            IClusterMembershipService clusterMembership,
            IMultiClusterOracle multiClusterOracle,
            IInternalGrainFactory grainFactory,
            Factory<GrainDirectoryPartition> grainDirectoryPartitionFactory,
            RegistrarManager registrarManager,
            ExecutorService executorService,
            IOptions<MultiClusterOptions> multiClusterOptions,
            IOptions<GrainDirectoryOptions> grainDirectoryOptions,
            ILoggerFactory loggerFactory,
            IFatalErrorHandler fatalErrorHandler,
            IServiceProvider serviceProvider,
            SiloProviderRuntime siloProviderRuntime)
        {
            this.serviceProvider = serviceProvider;
            this.log = loggerFactory.CreateLogger<LocalGrainDirectory>();

            var clusterId = multiClusterOptions.Value.HasMultiClusterNetwork ? siloDetails.ClusterId : null;
            this.MyAddress = siloDetails.SiloAddress;
            this.Scheduler = scheduler;
            this.clusterMembership = clusterMembership;
            this.multiClusterOracle = multiClusterOracle;
            this.grainFactory = grainFactory;
            this.clusterId = clusterId;

            DirectoryCache = GrainDirectoryCacheFactory.CreateGrainDirectoryCache(grainDirectoryOptions.Value);
            maintainer =
                GrainDirectoryCacheFactory.CreateGrainDirectoryCacheMaintainer(
                    siloDetails,
                    this,
                    this.DirectoryCache,
                    grainFactory, 
                    executorService,
                    loggerFactory);
            GsiActivationMaintainer = new GlobalSingleInstanceActivationMaintainer(
                this,
                this.log,
                grainFactory,
                multiClusterOracle,
                executorService,
                siloDetails,
                multiClusterOptions,
                loggerFactory,
                registrarManager);
            
            this.membershipSnapshot = new DirectoryMembershipSnapshot(this.log, this.MyAddress, this.clusterMembership.CurrentSnapshot);
            this.directoryMembershipUpdates = new AsyncEnumerable<DirectoryMembershipSnapshot>(
                (previous, proposed) => proposed.ClusterMembership.Version > previous.ClusterMembership.Version,
                this.membershipSnapshot)
            {
                OnPublished = updated => Interlocked.Exchange(ref this.membershipSnapshot, updated)
            };

            DirectoryPartition = grainDirectoryPartitionFactory();
            this.HandoffManager = new GrainDirectoryHandoffManager(
                siloDetails,
                this,
                this.clusterMembership,
                grainFactory,
                grainDirectoryPartitionFactory,
                loggerFactory);

            log.LogDebug($"Creating {nameof(RemoteGrainDirectory)} System Target");
            RemoteGrainDirectory = new RemoteGrainDirectory(siloDetails, this, Constants.DirectoryServiceId, loggerFactory);
            siloProviderRuntime.RegisterSystemTarget(RemoteGrainDirectory);

            log.LogDebug($"Creating {nameof(CacheValidator)} System Target");
            CacheValidator = new RemoteGrainDirectory(siloDetails, this, Constants.DirectoryCacheValidatorId, loggerFactory);
            siloProviderRuntime.RegisterSystemTarget(CacheValidator);

            log.LogDebug($"Creating {nameof(ClusterGrainDirectory)} System Target");
            RemoteClusterGrainDirectory = new ClusterGrainDirectory(
                siloDetails,
                this,
                Constants.ClusterDirectoryServiceId,
                clusterId,
                grainFactory,
                multiClusterOracle,
                loggerFactory);
            siloProviderRuntime.RegisterSystemTarget(RemoteClusterGrainDirectory);

            localLookups = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_LOCAL_ISSUED);
            localSuccesses = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_LOCAL_SUCCESSES);
            fullLookups = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_FULL_ISSUED);

            RemoteLookupsSent = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_REMOTE_SENT);
            RemoteLookupsReceived = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_REMOTE_RECEIVED);

            LocalDirectoryLookups = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_LOCALDIRECTORY_ISSUED);
            LocalDirectorySuccesses = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_LOCALDIRECTORY_SUCCESSES);

            cacheLookups = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_CACHE_ISSUED);
            cacheSuccesses = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_CACHE_SUCCESSES);
            StringValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_CACHE_HITRATIO, () =>
                {
                    long delta1, delta2;
                    long curr1 = cacheSuccesses.GetCurrentValueAndDelta(out delta1);
                    long curr2 = cacheLookups.GetCurrentValueAndDelta(out delta2);
                    return String.Format("{0}, Delta={1}", 
                        (curr2 != 0 ? (float)curr1 / (float)curr2 : 0)
                        ,(delta2 !=0 ? (float)delta1 / (float)delta2 : 0));
                });

            CacheValidationsSent = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_VALIDATIONS_CACHE_SENT);
            CacheValidationsReceived = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_VALIDATIONS_CACHE_RECEIVED);

            registrationsIssued = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_ISSUED);
            RegistrationsLocal = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_LOCAL);
            RegistrationsRemoteSent = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_REMOTE_SENT);
            RegistrationsRemoteReceived = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_REMOTE_RECEIVED);
            registrationsSingleActIssued = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_ISSUED);
            RegistrationsSingleActLocal = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_LOCAL);
            RegistrationsSingleActRemoteSent = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_SENT);
            RegistrationsSingleActRemoteReceived = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_RECEIVED);
            unregistrationsIssued = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_ISSUED);
            UnregistrationsLocal = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_LOCAL);
            UnregistrationsRemoteSent = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_REMOTE_SENT);
            UnregistrationsRemoteReceived = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_REMOTE_RECEIVED);
            unregistrationsManyIssued = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_MANY_ISSUED);
            UnregistrationsManyRemoteSent = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_SENT);
            UnregistrationsManyRemoteReceived = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_RECEIVED);

            directoryPartitionCount = IntValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_PARTITION_SIZE, () => DirectoryPartition.Count);
            IntValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_MYPORTION_RINGDISTANCE, () => RingDistanceToSuccessor());
            FloatValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_MYPORTION_RINGPERCENTAGE, () => (((float)this.RingDistanceToSuccessor()) / ((float)(int.MaxValue * 2L))) * 100);
            FloatValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_MYPORTION_AVERAGERINGPERCENTAGE, () =>
            {
                var size = DirectoryMembershipSnapshot.RingSizeStatistic(this.membershipSnapshot);
                return size == 0 ? 0 : (100 / (float)size);
            });
            IntValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_RINGSIZE, () => DirectoryMembershipSnapshot.RingSizeStatistic(this.membershipSnapshot));
            StringValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING, () => DirectoryMembershipSnapshot.RingDetailsStatistic(this.membershipSnapshot));
            StringValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_PREDECESSORS, () => DirectoryMembershipSnapshot.RingPredecessorStatistic(this.membershipSnapshot));
            StringValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_SUCCESSORS, () => DirectoryMembershipSnapshot.RingSuccessorStatistic(this.membershipSnapshot));

            this.registrarManager = registrarManager;
            this.fatalErrorHandler = fatalErrorHandler;
        }

        private async Task ProcessMembershipUpdates()
        {
            IAsyncEnumerator<ClusterMembershipSnapshot> enumerator = default;
            try
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting to process membership updates");
                enumerator = this.clusterMembership.MembershipUpdates.GetAsyncEnumerator(this.cancellation.Token);
                ClusterMembershipSnapshot previousClusterMembership = default;
                while (await enumerator.MoveNextAsync())
                {
                    try
                    {
                        // Update the membership snapshot
                        var previous = this.membershipSnapshot;
                        var updated = new DirectoryMembershipSnapshot(this.log, this.MyAddress, enumerator.Current);

                        var directoryPartitionCopy = this.DirectoryPartition.GetItems();
                        var directoryCache = this.DirectoryCache.KeyValues;

                        // Process the individual changes
                        ClusterMembershipUpdate delta;
                        if (previousClusterMembership is null)
                        {
                            delta = updated.ClusterMembership.AsUpdate();
                        }
                        else
                        {
                            delta = updated.ClusterMembership.CreateUpdate(previousClusterMembership);
                        }

                        // Update membership now so that it is visible to callers below (eg, catalog, handoff)
                        this.directoryMembershipUpdates.TryPublish(updated);

                        foreach (var change in delta.Changes)
                        {
                            // Ignore changes for the local silo
                            if (change.SiloAddress.Equals(this.MyAddress)) continue;

                            var status = change.Status;
                            if (status.IsTerminating())
                            {
                                _ = this.Scheduler.QueueAction(
                                    () => RemoveSilo(previous, updated, change, directoryPartitionCopy, directoryCache),
                                    this.CacheValidator.SchedulingContext);
                            }
                            else if (status == SiloStatus.Active)
                            {
                                _ = this.Scheduler.QueueAction(
                                    () => AddSilo(updated, change, directoryPartitionCopy, directoryCache),
                                    this.CacheValidator.SchedulingContext);
                            }
                        }

                        previousClusterMembership = updated.ClusterMembership;
                    }
                    catch (Exception exception)
                    {
                        this.log.LogError("Exception while processing membership updates: {Exception}", exception);
                    }
                }
            }
            catch (Exception exception)
            {
                this.log.LogError("Fatal exception while processing membership updates: {Exception}", exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopping membership update processor");
                if (enumerator is object) await enumerator.DisposeAsync();
                this.directoryMembershipUpdates.Dispose();
            }

            void AddSilo(
                DirectoryMembershipSnapshot updated,
                ClusterMember added,
                Dictionary<GrainId, IGrainInfo> directoryPartitionCopy,
                IReadOnlyList<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>> directoryCache)
            {
                try
                {
                    if (log.IsEnabled(LogLevel.Information)) log.LogInformation("Silo {LocalSilo} adding silo {RemoteSilo}", MyAddress, added.SiloAddress);
                    HandoffManager.ProcessSiloAddEvent(updated, added.SiloAddress);

                    this.AdjustLocalDirectory(directoryPartitionCopy, added.SiloAddress, dead: false);
                    this.AdjustLocalCache(updated, directoryCache, added.SiloAddress, dead: false);
                    if (log.IsEnabled(LogLevel.Information)) log.LogInformation("Silo {LocalSilo} added silo {RemoteSilo}", MyAddress, added.SiloAddress);
                }
                catch (Exception exception)
                {
                    this.log.LogError(
                        "Exception while processing membership update for {Silo} in status {Status}: {Exception}",
                        added.SiloAddress,
                        added.Status,
                        exception);
                }
            }

            void RemoveSilo(
                DirectoryMembershipSnapshot existing,
                DirectoryMembershipSnapshot updated,
                ClusterMember removed,
                Dictionary<GrainId, IGrainInfo> directoryPartitionCopy,
                IReadOnlyList<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>> directoryCache)
            {
                if (log.IsEnabled(LogLevel.Information))
                {
                    this.log.LogInformation("Silo {LocalSilo} removing silo {RemoteSilo}", this.MyAddress, removed.SiloAddress);
                }

                try
                {
                    if (this.catalog is null) this.catalog = this.serviceProvider.GetRequiredService<Catalog>();

                    // Only notify the catalog once.
                    // The catalog is intentionally called using the previous membership snapshot so that calculations about directory partitions
                    // are consistent.
                    this.catalog.OnSiloStatusChange(existing, removed.SiloAddress, removed.Status);
                }
                catch (Exception exc)
                {
                    this.log.Error(
                        ErrorCode.Directory_SiloStatusChangeNotification_Exception,
                        string.Format("CatalogSiloStatusListener.RemoveServer has thrown an exception when notified about removed silo {0}.", removed.SiloAddress.ToStringWithHashCode()), exc);
                }

                try
                {
                    this.HandoffManager.ProcessSiloRemoveEvent(removed.SiloAddress);
                    this.AdjustLocalDirectory(directoryPartitionCopy, removed.SiloAddress, dead: true);
                    this.AdjustLocalCache(updated, directoryCache, removed.SiloAddress, dead: true);

                    if (log.IsEnabled(LogLevel.Information))
                    {
                        this.log.LogInformation("Silo {LocalSilo} removed silo {RemoteSilo}", this.MyAddress, removed.SiloAddress);
                    }
                }
                catch (Exception exception)
                {
                    this.log.LogError(
                        "Exception while processing membership update for {Silo} in status {Status}: {Exception}",
                        removed.SiloAddress,
                        removed.Status,
                        exception);
                }
            }
        }

        /// <summary>
        /// Adjust local directory following the addition/removal of a silo
        /// </summary>
        private void AdjustLocalDirectory(Dictionary<GrainId, IGrainInfo> directoryPartitionCopy, SiloAddress silo, bool dead)
        {
            // Determine which activations to remove.
            var activationsToRemove = new List<(GrainId, ActivationId)>();
            foreach (var entry in directoryPartitionCopy)
            {
                var (grain, grainInfo) = (entry.Key, entry.Value);
                foreach (var instance in grainInfo.Instances)
                {
                    var (activationId, activationInfo) = (instance.Key, instance.Value);

                    // Include any activations from dead silos and from predecessors.
                    var siloIsDead = dead && activationInfo.SiloAddress.Equals(silo);
                    var siloIsPredecessor = activationInfo.SiloAddress.IsPredecessorOf(silo);
                    if (siloIsDead || siloIsPredecessor)
                    {
                        activationsToRemove.Add((grain, activationId));
                    }
                }
            }
            // drop all records of activations located on the removed silo
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
        private void AdjustLocalCache(DirectoryMembershipSnapshot snapshot, IReadOnlyList<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>> cache, SiloAddress silo, bool dead)
        {
            // For dead silos, remove any activation registered to that silo or one of its predecessors.
            // For new silos, remove any activation registered to one of its predecessors.
            Func<Tuple<SiloAddress, ActivationId>, bool> predicate;
            if (dead) predicate = t => t.Item1.Equals(silo) || t.Item1.IsPredecessorOf(silo);
            else predicate = t => t.Item1.IsPredecessorOf(silo);

            // remove all records of activations located on the removed silo
            foreach (Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int> tuple in cache)
            {
                // 2) remove entries now owned by me (they should be retrieved from my directory partition)
                if (MyAddress.Equals(snapshot.CalculateGrainDirectoryPartition(tuple.Item1)))
                {
                    DirectoryCache.Remove(tuple.Item1);
                }

                // 1) remove entries that point to activations located on the removed silo
                RemoveActivations(DirectoryCache, tuple.Item1, tuple.Item2, tuple.Item3, predicate);
            }
        }

        private bool IsValidSilo(SiloAddress silo)
        { 
            return !this.membershipSnapshot.ClusterMembership.GetSiloStatus(silo).IsTerminating();
        }

        internal ValueTask<SiloAddress> GetPartitionOwner(
            GrainId grain,
            int hopCount,
            MembershipVersion targetMembershipVersion,
            string operationDescription,
            bool skipInitializationCheck = false)
        {
            var needsRefresh = hopCount > 0 && (targetMembershipVersion == default || this.DirectoryMembershipSnapshot.ClusterMembership.Version < targetMembershipVersion);

            if (!this.initialStabilizationComplete || needsRefresh)
            {
                return GetPartitionOwnerAsync(grain, targetMembershipVersion, operationDescription, skipInitializationCheck);
            }

            return new ValueTask<SiloAddress>(GetPartitionOwnerInternal(grain, operationDescription));

            async ValueTask<SiloAddress> GetPartitionOwnerAsync(GrainId grainId, MembershipVersion membershipVersion, string operation, bool skipInitialization)
            {
                await this.RefreshMembership(membershipVersion);

                var result = GetPartitionOwnerInternal(grainId, operation);

                // If this silo is supposed to service the request, wait until initial stabilization has completed.
                if (!skipInitialization && !this.initialStabilizationComplete && this.MyAddress.Equals(result))
                {
                    var stopwatch = ValueStopwatch.StartNew();
                    if (this.log.IsEnabled(LogLevel.Debug))
                    {
                        this.log.LogDebug("Waiting for initial directory stabilization");
                    }

                    var remainingWaitCycles = 80;

                    // Wait for handoff only if there is more than one member in the cluster.
                    while (this.DirectoryMembershipSnapshot.ActiveMemberCount > 1
                        && !this.HandoffManager.HasReceivedSplit
                        && remainingWaitCycles-- > 0)
                    {
                        await TimerManager.Delay(TimeSpan.FromMilliseconds(50));
                    }

                    // Even if a split partition has not been received yet, continue as though it has
                    // since we have waited for long enough.
                    this.initialStabilizationComplete = true;

                    if (this.log.IsEnabled(LogLevel.Debug))
                    {
                        this.log.LogDebug(
                            "Initial directory stabilization completed in {ElapsedMilliseconds}ms. Received split: {ReceivedSplit}",
                            stopwatch.Elapsed.TotalMilliseconds,
                            this.HandoffManager.HasReceivedSplit);
                    }
                }

                return result;
            }

            SiloAddress GetPartitionOwnerInternal(GrainId grainId, string operation)
            {
                var snapshot = this.membershipSnapshot;
                var owner = snapshot.CalculateGrainDirectoryPartition(grainId);
                
                // This silo is the owner if it's the owner according to the latest membership snapshot
                // or it has accepted handoff from the (shutting down) owner.
                SiloAddress result;
                if (this.HandoffManager.HasAcceptedHandoffForSilo(owner))
                {
                    result = this.MyAddress;
                }
                else
                {
                    result = owner;
                }

                if (this.MyAddress.Equals(result))
                {
                    // If this silo is terminating then forward all requests to its predecessor.
                    if (this.HandoffManager.HasPerformedHandoff)
                    {
                        result = snapshot.FindPredecessor(this.MyAddress);
                    }
                }

                if (result is null)
                {
                    // There are no available silos, so throw.
                    throw new InvalidOperationException("Grain directory is not operational");
                }

                if (!this.MyAddress.Equals(result) && hopCount > HOP_LIMIT)
                {
                    // we are not forwarding because there were too many hops already
                    throw new OrleansException(
                        $"Silo {this.MyAddress} is not owner of {grainId}, cannot forward {operation} to owner {owner} because hop limit is reached");
                }

                return result;
            }
        }

        public Task<AddressAndTag> RegisterAsync(ActivationAddress address, bool singleActivation, int hopCount)
        {
            return RegisterAsync(address, singleActivation, hopCount, skipInitializationCheck: false);
        }

        internal async Task<AddressAndTag> RegisterAsync(ActivationAddress address, bool singleActivation, int hopCount, bool skipInitializationCheck)
        {
            var callerMembershipVersion = default(MembershipVersion);

            var counterStatistic = 
                singleActivation 
                ? (hopCount > 0 ? this.RegistrationsSingleActRemoteReceived : this.registrationsSingleActIssued)
                : (hopCount > 0 ? this.RegistrationsRemoteReceived : this.registrationsIssued);

            counterStatistic.Increment();

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = await this.GetPartitionOwner(address.Grain, hopCount, callerMembershipVersion, "RegisterAsync", skipInitializationCheck);

            if (this.MyAddress.Equals(forwardAddress))
            {
                (singleActivation ? RegistrationsSingleActLocal : RegistrationsLocal).Increment();

                // we are the owner     
                var registrar = this.registrarManager.GetRegistrarForGrain(address.Grain);

                if (log.IsEnabled(LogLevel.Trace)) log.Trace($"use registrar {registrar.GetType().Name} for activation {address}");

                return registrar.IsSynchronous ? registrar.Register(address, singleActivation)
                    : await registrar.RegisterAsync(address, singleActivation);
            }
            else
            {
                (singleActivation ? RegistrationsSingleActRemoteSent : RegistrationsRemoteSent).Increment();

                if (hopCount > 0)
                {
                    this.log.LogWarning($"RegisterAsync - It seems we are not the owner of activation {address}, trying to forward it to {forwardAddress} (hopCount={hopCount})");
                }

                // otherwise, notify the owner
                AddressAndTag result = await GetDirectoryReference(forwardAddress).RegisterAsync(address, singleActivation, hopCount + 1);

                if (singleActivation)
                {
                    // Caching optimization: 
                    // cache the result of a successfull RegisterSingleActivation call, only if it is not a duplicate activation.
                    // this way next local lookup will find this ActivationAddress in the cache and we will save a full lookup!
                    if (result.Address == null) return result;

                    if (!address.Equals(result.Address) || !IsValidSilo(address.Silo)) return result;

                    var cached = new List<Tuple<SiloAddress, ActivationId>>(1) { Tuple.Create(address.Silo, address.Activation) };
                    // update the cache so next local lookup will find this ActivationAddress in the cache and we will save full lookup.
                    DirectoryCache.AddOrUpdate(address.Grain, cached, result.VersionTag);
                }
                else
                {
                    if (IsValidSilo(address.Silo))
                    {
                        // Caching optimization:
                        // cache the result of a successfull RegisterActivation call, only if it is not a duplicate activation.
                        // this way next local lookup will find this ActivationAddress in the cache and we will save a full lookup!
                        IReadOnlyList<Tuple<SiloAddress, ActivationId>> cached;
                        if (!DirectoryCache.LookUp(address.Grain, out cached))
                        {
                            cached = new List<Tuple<SiloAddress, ActivationId>>(1)
                        {
                            Tuple.Create(address.Silo, address.Activation)
                        };
                        }
                        else
                        {
                            var newcached = new List<Tuple<SiloAddress, ActivationId>>(cached.Count + 1);
                            newcached.AddRange(cached);
                            newcached.Add(Tuple.Create(address.Silo, address.Activation));
                            cached = newcached;
                        }
                        // update the cache so next local lookup will find this ActivationAddress in the cache and we will save full lookup.
                        DirectoryCache.AddOrUpdate(address.Grain, cached, result.VersionTag);
                    }
                }

                return result;
            }
        }

        public Task UnregisterAfterNonexistingActivation(ActivationAddress addr, SiloAddress origin)
        {
            log.Trace("UnregisterAfterNonexistingActivation addr={0} origin={1}", addr, origin);

            if (origin == null || this.IsSiloInCluster(origin))
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

        public async Task UnregisterAsync(ActivationAddress address, UnregistrationCause cause, int hopCount)
        {
            var callerMembershipVersion = default(MembershipVersion);

            (hopCount > 0 ? UnregistrationsRemoteReceived : unregistrationsIssued).Increment();

            if (hopCount == 0)
                InvalidateCacheEntry(address);

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = await this.GetPartitionOwner(address.Grain, hopCount, callerMembershipVersion, "UnregisterAsync");

            if (this.MyAddress.Equals(forwardAddress))
            {
                // we are the owner
                UnregistrationsLocal.Increment();

                var registrar = this.registrarManager.GetRegistrarForGrain(address.Grain);

                if (registrar.IsSynchronous)
                    registrar.Unregister(address, cause);
                else
                    await registrar.UnregisterAsync(new List<ActivationAddress>() { address }, cause);
            }
            else
            {
                if (hopCount > 0)
                {
                    this.log.LogWarning($"UnregisterAsync - It seems we are not the owner of activation {address}, trying to forward it to {forwardAddress} (hopCount={hopCount})");
                }

                UnregistrationsRemoteSent.Increment();
                // otherwise, notify the owner
                await GetDirectoryReference(forwardAddress).UnregisterAsync(address, cause, hopCount + 1);
            }
        }

        // helper method to avoid code duplication inside UnregisterManyAsync
        private void UnregisterOrPutInForwardList(
            IEnumerable<ActivationAddress> addresses,
            UnregistrationCause cause,
            int hopCount,
            ref Dictionary<SiloAddress, List<ActivationAddress>> forward,
            List<Task> tasks,
            string context)
        {
            Dictionary<IGrainRegistrar, List<ActivationAddress>> unregisterBatches = new Dictionary<IGrainRegistrar, List<ActivationAddress>>();

            var snapshot = this.membershipSnapshot;
            foreach (var address in addresses)
            {
                // see if the owner is somewhere else
                var forwardAddress = snapshot.CalculateGrainDirectoryPartition(address.Grain);

                if (forwardAddress != null && !forwardAddress.Equals(this.MyAddress))
                {
                    if (hopCount >= HOP_LIMIT)
                    {
                        // we are not forwarding because there were too many hops already
                        throw new OrleansException($"Silo {MyAddress} is not owner of {address.Grain}, cannot forward {context} to owner {forwardAddress} because hop limit is reached");
                    }

                    if (forward == null)
                    {
                        forward = new Dictionary<SiloAddress, List<ActivationAddress>>();
                    }

                    if (!forward.TryGetValue(forwardAddress, out var list))
                    {
                        forward[forwardAddress] = list = new List<ActivationAddress>();
                    }

                    list.Add(address);
                }
                else
                {
                    // we are the owner
                    UnregistrationsLocal.Increment();
                    var registrar = this.registrarManager.GetRegistrarForGrain(address.Grain);

                    if (registrar.IsSynchronous)
                    {
                        registrar.Unregister(address, cause);
                    }
                    else
                    {
                        List<ActivationAddress> list;
                        if (!unregisterBatches.TryGetValue(registrar, out list))
                            unregisterBatches.Add(registrar, list = new List<ActivationAddress>());
                        list.Add(address);
                    }
                }
            }

            // batch-unregister for each asynchronous registrar
            foreach (var kvp in unregisterBatches)
            {
                tasks.Add(kvp.Key.UnregisterAsync(kvp.Value, cause));
            }
        }

        public async Task UnregisterManyAsync(List<ActivationAddress> addresses, UnregistrationCause cause, int hopCount)
        {
            (hopCount > 0 ? UnregistrationsManyRemoteReceived : unregistrationsManyIssued).Increment();

            Dictionary<SiloAddress, List<ActivationAddress>> forwardlist = null;
            var tasks = new List<Task>();

            UnregisterOrPutInForwardList(addresses, cause, hopCount, ref forwardlist, tasks, "UnregisterManyAsync");

            // before forwarding to other silos, we insert a retry delay and re-check destination
            if (hopCount > 0 && forwardlist != null)
            {
                await this.RefreshMembership();
                Dictionary<SiloAddress, List<ActivationAddress>> forwardlist2 = null;
                UnregisterOrPutInForwardList(addresses, cause, hopCount, ref forwardlist2, tasks, "UnregisterManyAsync");
                forwardlist = forwardlist2;
                if (forwardlist != null)
                {
                    this.log.LogWarning($"UnregisterManyAsync - It seems we are not the owner of some activations, trying to forward it to {forwardlist.Count} silos (hopCount={hopCount})");
                }
                }
            }

            // forward the requests
            if (forwardlist != null)
            {
                foreach (var kvp in forwardlist)
                {
                    UnregistrationsManyRemoteSent.Increment();
                    tasks.Add(GetDirectoryReference(kvp.Key).UnregisterManyAsync(kvp.Value, cause, hopCount + 1));
                }
            }

            // wait for all the requests to finish
            await Task.WhenAll(tasks);
        }


        public bool LocalLookup(GrainId grain, out AddressesAndTag result)
        {
            localLookups.Increment();

            SiloAddress silo = this.membershipSnapshot.CalculateGrainDirectoryPartition(grain);

            if (log.IsEnabled(LogLevel.Debug)) log.Debug("Silo {0} tries to lookup for {1}-->{2} ({3}-->{4})", MyAddress, grain, silo, grain.GetUniformHashCode(), silo?.GetConsistentHashCode());

            //this will only happen if I'm the only silo in the cluster and I'm shutting down
            if (silo == null)
            {
                if (log.IsEnabled(LogLevel.Trace)) log.Trace("LocalLookup mine {0}=null", grain);
                result = new AddressesAndTag();
                return false;
            }

            // check if we own the grain
            if (silo.Equals(MyAddress))
            {
                LocalDirectoryLookups.Increment();
                result = GetLocalDirectoryData(grain);
                if (result.Addresses == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were 
                    // some recent changes in the membership
                    if (log.IsEnabled(LogLevel.Trace)) log.Trace("LocalLookup mine {0}=null", grain);
                    return false;
                }
                if (log.IsEnabled(LogLevel.Trace)) log.Trace("LocalLookup mine {0}={1}", grain, result.Addresses.ToStrings());
                LocalDirectorySuccesses.Increment();
                localSuccesses.Increment();
                return true;
            }

            // handle cache
            result = new AddressesAndTag();
            cacheLookups.Increment();
            result.Addresses = GetLocalCacheData(grain);
            if (result.Addresses == null)
            {
                if (log.IsEnabled(LogLevel.Trace)) log.Trace("TryFullLookup else {0}=null", grain);
                return false;
            }
            if (log.IsEnabled(LogLevel.Trace)) log.Trace("LocalLookup cache {0}={1}", grain, result.Addresses.ToStrings());
            cacheSuccesses.Increment();
            localSuccesses.Increment();
            return true;
        }

        public AddressesAndTag GetLocalDirectoryData(GrainId grain)
        {
            return DirectoryPartition.LookUpActivations(grain);
        }

        public List<ActivationAddress> GetLocalCacheData(GrainId grain)
        {
            IReadOnlyList<Tuple<SiloAddress, ActivationId>> cached;
            return DirectoryCache.LookUp(grain, out cached) ? 
                cached.Select(elem => ActivationAddress.GetAddress(elem.Item1, grain, elem.Item2)).Where(addr => IsValidSilo(addr.Silo)).ToList() : 
                null;
        }

        public Task<AddressesAndTag> LookupInCluster(GrainId grainId, string clusterId)
        {
            if (clusterId == null) throw new ArgumentNullException(nameof(clusterId));

            if (clusterId == this.clusterId)
            {
                return this.LookupAsync(grainId);
            }
            else
            {
                // find gateway
                var gossipOracle = this.multiClusterOracle;
                var clusterGatewayAddress = gossipOracle.GetRandomClusterGateway(clusterId);
                if (clusterGatewayAddress != null)
                {
                    // call remote grain directory
                    var remotedirectory = this.GetDirectoryReference(clusterGatewayAddress);
                    return remotedirectory.LookupAsync(grainId);
                }
                else
                {
                    return Task.FromResult(default(AddressesAndTag));
                }
            }
        }

        public async Task<AddressesAndTag> LookupAsync(GrainId grainId, int hopCount = 0)
        {
            var callerMembershipVersion = default(MembershipVersion);
            (hopCount > 0 ? RemoteLookupsReceived : fullLookups).Increment();

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = await this.GetPartitionOwner(grainId, hopCount, callerMembershipVersion, "LookUpAsync");

            if (this.MyAddress.Equals(forwardAddress))
            {
                // we are the owner
                LocalDirectoryLookups.Increment();
                var localResult = DirectoryPartition.LookUpActivations(grainId);
                if (localResult.Addresses == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were 
                    // some recent changes in the membership
                    if (log.IsEnabled(LogLevel.Trace)) log.Trace("FullLookup mine {0}=none", grainId);
                    localResult.Addresses = new List<ActivationAddress>();
                    localResult.VersionTag = GrainInfo.NO_ETAG;
                    return localResult;
                }

                if (log.IsEnabled(LogLevel.Trace)) log.Trace("FullLookup mine {0}={1}", grainId, localResult.Addresses.ToStrings());
                LocalDirectorySuccesses.Increment();
                return localResult;
            }
            else
            {
                if (hopCount > 0)
                {
                    this.log.LogWarning($"LookupAsync - It seems we are not the owner of grain {grainId}, trying to forward it to {forwardAddress} (hopCount={hopCount})");
                }

                // Just a optimization. Why sending a message to someone we know is not valid.
                if (!IsValidSilo(forwardAddress))
                {
                    throw new OrleansException(String.Format("Current directory at {0} is not stable to perform the lookup for grainId {1} (it maps to {2}, which is not a valid silo). Retry later.", MyAddress, grainId, forwardAddress));
                }

                RemoteLookupsSent.Increment();
                var result = await GetDirectoryReference(forwardAddress).LookupAsync(grainId, hopCount + 1);

                // update the cache
                result.Addresses = result.Addresses.Where(t => IsValidSilo(t.Silo)).ToList();
                if (log.IsEnabled(LogLevel.Trace)) log.Trace("FullLookup remote {0}={1}", grainId, result.Addresses.ToStrings());

                var entries = result.Addresses.Select(t => Tuple.Create(t.Silo, t.Activation)).ToList();

                if (entries.Count > 0)
                    DirectoryCache.AddOrUpdate(grainId, entries, result.VersionTag);

                return result;
            }
        }

        public async Task DeleteGrainAsync(GrainId grainId, int hopCount)
        {
            var callerMembershipVersion = default(MembershipVersion);

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = await this.GetPartitionOwner(grainId, hopCount, callerMembershipVersion, "DeleteGrainAsync");

            if (this.MyAddress.Equals(forwardAddress))
            {
                // we are the owner
                var registrar = this.registrarManager.GetRegistrarForGrain(grainId);

                if (registrar.IsSynchronous)
                    registrar.Delete(grainId);
                else
                    await registrar.DeleteAsync(grainId);
            }
            else
            {
                if (hopCount > 0)
                {
                    this.log.LogWarning($"DeleteGrainAsync - It seems we are not the owner of grain {grainId}, trying to forward it to {forwardAddress} (hopCount={hopCount})");
                }

                // otherwise, notify the owner
                DirectoryCache.Remove(grainId);
                await GetDirectoryReference(forwardAddress).DeleteGrainAsync(grainId, hopCount + 1);
            }
        }

        public void InvalidateCacheEntry(ActivationAddress activationAddress, bool invalidateDirectoryAlso = false)
        {
            int version;
            IReadOnlyList<Tuple<SiloAddress, ActivationId>> list;
            var grainId = activationAddress.Grain;
            var activationId = activationAddress.Activation;

            // look up grainId activations
            if (DirectoryCache.LookUp(grainId, out list, out version))
            {
                RemoveActivations(DirectoryCache, grainId, list, version, t => t.Item2.Equals(activationId));
            }

            // for multi-cluster registration, the local directory may cache remote activations
            // and we need to remove them here, on the fast path, to avoid forwarding the message
            // to the wrong destination again
            if (invalidateDirectoryAlso && MyAddress.Equals(this.membershipSnapshot.CalculateGrainDirectoryPartition(grainId)))
            {
                var registrar = this.registrarManager.GetRegistrarForGrain(grainId);
                registrar.InvalidateCache(activationAddress);
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();

            long localLookupsDelta;
            long localLookupsCurrent = localLookups.GetCurrentValueAndDelta(out localLookupsDelta);
            long localLookupsSucceededDelta;
            long localLookupsSucceededCurrent = localSuccesses.GetCurrentValueAndDelta(out localLookupsSucceededDelta);
            long fullLookupsDelta;
            long fullLookupsCurrent = fullLookups.GetCurrentValueAndDelta(out fullLookupsDelta);
            long directoryPartitionSize = directoryPartitionCount.GetCurrentValue();

            sb.AppendLine("Local Grain Directory:");
            sb.AppendFormat("   Local partition: {0} entries", directoryPartitionSize).AppendLine();
            sb.AppendLine("   Since last call:");
            sb.AppendFormat("      Local lookups: {0}", localLookupsDelta).AppendLine();
            sb.AppendFormat("      Local found: {0}", localLookupsSucceededDelta).AppendLine();
            if (localLookupsDelta > 0)
                sb.AppendFormat("      Hit rate: {0:F1}%", (100.0 * localLookupsSucceededDelta) / localLookupsDelta).AppendLine();
            
            sb.AppendFormat("      Full lookups: {0}", fullLookupsDelta).AppendLine();
            sb.AppendLine("   Since start:");
            sb.AppendFormat("      Local lookups: {0}", localLookupsCurrent).AppendLine();
            sb.AppendFormat("      Local found: {0}", localLookupsSucceededCurrent).AppendLine();
            if (localLookupsCurrent > 0)
                sb.AppendFormat("      Hit rate: {0:F1}%", (100.0 * localLookupsSucceededCurrent) / localLookupsCurrent).AppendLine();
            
            sb.AppendFormat("      Full lookups: {0}", fullLookupsCurrent).AppendLine();
            sb.Append(DirectoryCache.ToString());

            return sb.ToString();
        }

        private long RingDistanceToSuccessor()
        {
            var snapshot = this.membershipSnapshot;
            long distance;
            var successor = snapshot.FindSuccessor(MyAddress);
            if (successor is null)
            {
                distance = 0;
            }
            else
            {
                distance = successor == null ? 0 : CalcRingDistance(MyAddress, successor);
            }

            return distance;
        }

        private static long CalcRingDistance(SiloAddress silo1, SiloAddress silo2)
        {
            const long ringSize = int.MaxValue * 2L;
            long hash1 = silo1.GetConsistentHashCode();
            long hash2 = silo2.GetConsistentHashCode();

            if (hash2 > hash1) return hash2 - hash1;
            if (hash2 < hash1) return ringSize - (hash1 - hash2);

            return 0;
        }

        internal IRemoteGrainDirectory GetDirectoryReference(SiloAddress silo)
        {
            return this.grainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryServiceId, silo);
        }
        
        private static void RemoveActivations(IGrainDirectoryCache directoryCache, GrainId key, IReadOnlyList<Tuple<SiloAddress, ActivationId>> activations, int version, Func<Tuple<SiloAddress, ActivationId>, bool> doRemove)
        {
            int removeCount = activations.Count(doRemove);
            if (removeCount == 0)
            {
                return; // nothing to remove, done here
            }

            if (activations.Count > removeCount) // still some left, update activation list.  Note: Most of the time there should be only one activation
            {
                var newList = new List<Tuple<SiloAddress, ActivationId>>(activations.Count - removeCount);
                newList.AddRange(activations.Where(t => !doRemove(t)));
                directoryCache.AddOrUpdate(key, newList, version);
            }
            else // no activations left, remove from cache
            {
                directoryCache.Remove(key);
            }
        }

        public bool IsSiloInCluster(SiloAddress silo)
        {
            if (silo.Equals(this.MyAddress)) return true;

            var status = this.membershipSnapshot.ClusterMembership.GetSiloStatus(silo);
            return status != SiloStatus.None;
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var tasks = new List<Task>();
            lifecycle.Subscribe(nameof(LocalGrainDirectory), ServiceLifecycleStage.RuntimeServices, OnRuntimeServicesStart, OnRuntimeServicesStop);
            lifecycle.Subscribe(nameof(LocalGrainDirectory), ServiceLifecycleStage.BecomeActive, OnBecomeActiveStart, OnBecomeActiveStop);
            lifecycle.Subscribe(nameof(LocalGrainDirectory), ServiceLifecycleStage.Active, OnActiveStart, OnActiveStop);

            Task OnRuntimeServicesStart(CancellationToken ct)
            {
                log.Info("Start");

                if (maintainer != null)
                {
                    maintainer.Start();
                }

                if (GsiActivationMaintainer != null)
                {
                    GsiActivationMaintainer.Start();
                }

                tasks.Add(Task.Run(() => this.ProcessMembershipUpdates()));
                return Task.CompletedTask;
            }

            async Task OnRuntimeServicesStop(CancellationToken ct)
            {
                this.cancellation.Cancel(throwOnFirstException: false);

                await Task.WhenAny(ct.WhenCancelled(), Task.WhenAll(tasks));
            }

            async Task OnBecomeActiveStart(CancellationToken ct)
            {
                // Wait until the directory has processed that the local silo is active before completing.
                await this.WaitForStatus(status => status == SiloStatus.Active, ct);
            }

            async Task OnBecomeActiveStop(CancellationToken ct)
            {
                // Wait until the local grain directory observes that the silo is shutting down.
                var isTerminatingTask = this.WaitForStatus(status => status != SiloStatus.Active, ct);
                var task = await Task.WhenAny(
                    ct.WhenCancelled(),
                    Task.Delay(TimeSpan.FromSeconds(10)),
                    isTerminatingTask);

                if (!ReferenceEquals(task, isTerminatingTask) || !await isTerminatingTask)
                {
                    this.log.LogWarning(
                        "Did not observe status change during shutdown. Skipping graceful shutdown behavior");
                    return;
                }

                if (maintainer != null)
                {
                    maintainer.Stop();
                }
                if (GsiActivationMaintainer != null)
                {
                    GsiActivationMaintainer.Stop();
                }

                DirectoryPartition.Clear();
                DirectoryCache.Clear();

                if (this.catalog is null) this.catalog = this.serviceProvider.GetRequiredService<Catalog>();
                await await Task.WhenAny(ct.WhenCancelled(), this.catalog.DeactivateAllActivations());

                // Wait for messages to be forwarded. Obviously this is a bad hack.
                if (!ct.IsCancellationRequested) await Task.WhenAny(ct.WhenCancelled(), Task.Delay(TimeSpan.FromSeconds(5)));
            }

            Task OnActiveStart(CancellationToken ct) => Task.CompletedTask;

            async Task OnActiveStop(CancellationToken ct)
            {
                await this.HandoffLocalDirectory(ct);
            }
        }

        private async Task RefreshMembership(MembershipVersion targetVersion = default, CancellationToken cancellationToken = default)
        {
            if (this.log.IsEnabled(LogLevel.Debug))
            {
                this.log.LogDebug("Refreshing membership due to apparent inconsistency with received call.");
            }

            await this.clusterMembership.Refresh(targetVersion);

            IAsyncEnumerator<DirectoryMembershipSnapshot> enumerator = default;
            try
            {
                enumerator = this.directoryMembershipUpdates.GetAsyncEnumerator(cancellationToken);
                while (await enumerator.MoveNextAsync())
                {
                    var version = enumerator.Current.ClusterMembership.Version;

                    if (version >= this.membershipSnapshot.ClusterMembership.Version)
                    {
                        return;
                    }
                }
            }
            finally
            {
                if (enumerator is object) await enumerator.DisposeAsync();
            }
        }

        private async Task<bool> WaitForStatus(Func<SiloStatus, bool> condition, CancellationToken ct)
        {
            IAsyncEnumerator<DirectoryMembershipSnapshot> enumerator = default;
            try
            {
                enumerator = this.directoryMembershipUpdates.GetAsyncEnumerator(ct);
                while (await enumerator.MoveNextAsync())
                {
                    var status = enumerator.Current.ClusterMembership.GetSiloStatus(this.MyAddress);

                    if (condition(status))
                    {
                        return true;
                    }
                }
            }
            finally
            {
                if (enumerator is object) await enumerator.DisposeAsync();
            }

            return false;
        }

        private async Task HandoffLocalDirectory(CancellationToken cancellationToken)
        {
            try
            {
                await Task.WhenAny(
                    cancellationToken.WhenCancelled(),
                    this.Scheduler.QueueTask(
                        () => this.HandoffManager.ProcessSiloStoppingEvent(this.membershipSnapshot),
                        this.CacheValidator.SchedulingContext));
            }
            catch (Exception exc)
            {
                this.log.LogWarning($"GrainDirectoryHandOffManager failed ProcessSiloStoppingEvent due to exception {exc}");
            }
        }

        void IDisposable.Dispose()
        {
            this.directoryMembershipUpdates.Dispose();
        }
    }
}
