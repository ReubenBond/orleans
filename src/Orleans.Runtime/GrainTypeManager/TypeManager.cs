using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Versions;
using Orleans.Versions;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using Orleans.Runtime.Utilities;
using System.Threading;

namespace Orleans.Runtime
{
    internal class TypeManager : SystemTarget, IClusterTypeManager, ISiloTypeManager, ISiloStatusListener, IDisposable, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly ILogger logger;
        private readonly GrainTypeManager grainTypeManager;
        private readonly ISiloStatusOracle statusOracle;
        private readonly ImplicitStreamSubscriberTable implicitStreamSubscriberTable;
        private readonly IInternalGrainFactory grainFactory;
        private readonly CachedVersionSelectorManager versionSelectorManager;
        private readonly IClusterMembership clusterMembership;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly OrleansTaskScheduler scheduler;
        private readonly TimeSpan refreshClusterMapInterval;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private bool hasToRefreshClusterGrainInterfaceMap;
        private IDisposable refreshClusterGrainInterfaceMapTimer;
        private IVersionStore versionStore;
        private MembershipVersion lastUpdatedVersion;

        internal TypeManager(
            SiloAddress myAddr,
            GrainTypeManager grainTypeManager,
            ISiloStatusOracle oracle,
            OrleansTaskScheduler scheduler,
            TimeSpan refreshClusterMapInterval,
            ImplicitStreamSubscriberTable implicitStreamSubscriberTable,
            IInternalGrainFactory grainFactory,
            CachedVersionSelectorManager versionSelectorManager,
            ILoggerFactory loggerFactory,
            IClusterMembership clusterMembership,
            IFatalErrorHandler fatalErrorHandler,
            IAsyncTimerFactory timerFactory)
            : base(Constants.TypeManagerId, myAddr, loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<TypeManager>();
            this.grainTypeManager = grainTypeManager ?? throw new ArgumentNullException(nameof(grainTypeManager));
            this.statusOracle = oracle ?? throw new ArgumentNullException(nameof(oracle));
            this.implicitStreamSubscriberTable = implicitStreamSubscriberTable ?? throw new ArgumentNullException(nameof(implicitStreamSubscriberTable));
            this.grainFactory = grainFactory;
            this.versionSelectorManager = versionSelectorManager;
            this.clusterMembership = clusterMembership;
            this.fatalErrorHandler = fatalErrorHandler;
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            this.refreshClusterMapInterval = refreshClusterMapInterval;

            // We need this so we can place needed local activations
            this.grainTypeManager.SetInterfaceMapsBySilo(new Dictionary<SiloAddress, GrainInterfaceMap>
            {
                {this.Silo, grainTypeManager.GetTypeCodeMap()}
            });

            this.refreshTimer = timerFactory.Create(refreshClusterMapInterval, nameof(TypeManager) + ".RefreshTypeMap");
        }

        internal async Task Initialize(IVersionStore store)
        {
            this.versionStore = store;
            this.hasToRefreshClusterGrainInterfaceMap = true;

            await this.OnRefreshClusterMapTimer(null);

            this.refreshClusterGrainInterfaceMapTimer = this.RegisterTimer(
                OnRefreshClusterMapTimer,
                null,
                this.refreshClusterMapInterval,
                this.refreshClusterMapInterval);
        }

        public Task<IGrainTypeResolver> GetClusterGrainTypeResolver()
        {
            return Task.FromResult(grainTypeManager.GrainTypeResolver);
        }

        public Task<GrainInterfaceMap> GetSiloTypeCodeMap()
        {
            return Task.FromResult(grainTypeManager.GetTypeCodeMap());
        }

        public Task<ImplicitStreamSubscriberTable> GetImplicitStreamSubscriberTable(SiloAddress silo)
        {
            return Task.FromResult(implicitStreamSubscriberTable);
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            hasToRefreshClusterGrainInterfaceMap = true;
            if (status == SiloStatus.Active)
            {
                if (this.logger.IsEnabled(LogLevel.Information))
                {
                    this.logger.LogInformation("Expediting cluster type map refresh due to new silo, {SiloAddress}", updatedSilo);
                }

                this.scheduler.QueueTask(() => this.OnRefreshClusterMapTimer(null), SchedulingContext);
            }
        }

        private async Task ProcessMembershipUpdates()
        {
            IAsyncEnumerator<ClusterMembershipSnapshot> enumerator = default;
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug("Starting to process membership updates");
                enumerator = this.clusterMembership.MembershipUpdates.GetAsyncEnumerator(this.cancellation.Token);
                while (await enumerator.MoveNextAsync())
                {
                    // Ignore the actual snapshot and just always take the latest.
                    this.hasToRefreshClusterGrainInterfaceMap |= this.lastUpdatedVersion >= enumerator.Current.Version;
                    await this.UpdateClusterTypeMap();
                }
            }
            catch (Exception exception)
            {
                this.logger.LogError("Error processing membership updates: {Exception}", exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                if (enumerator is object) await enumerator.DisposeAsync();
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug("Stopping membership update processor");
            }
        }

        private async Task UpdateClusterTypeMap()
        {
            while (hasToRefreshClusterGrainInterfaceMap)
            {
                hasToRefreshClusterGrainInterfaceMap = false;

                var members = this.clusterMembership.CurrentSnapshot;
                if (this.lastUpdatedVersion >= members.Version) return;

                if (this.logger.IsEnabled(LogLevel.Debug)) logger.Debug("UpdateClusterTypeMap: refresh start");
                var knownSilosClusterGrainInterfaceMap = grainTypeManager.GrainInterfaceMapsBySilo;

                // Build the new map. Always start by himself
                var newSilosClusterGrainInterfaceMap = new Dictionary<SiloAddress, GrainInterfaceMap>
                {
                    {this.Silo, grainTypeManager.GetTypeCodeMap()}
                };

                var getGrainInterfaceMapTasks = new List<Task<KeyValuePair<SiloAddress, GrainInterfaceMap>>>();

                foreach (var member in members.Members)
                {
                    var siloAddress = member.Key;

                    // Skip this silo and any non-active silos.
                    if (siloAddress.Equals(this.Silo)) continue;
                    if (member.Value.Status != SiloStatus.Active) continue;

                    GrainInterfaceMap value;
                    if (knownSilosClusterGrainInterfaceMap.TryGetValue(siloAddress, out value))
                    {
                        if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace("OnRefreshClusterMapTimer: value already found locally for {SiloAddress}", siloAddress);
                        newSilosClusterGrainInterfaceMap[siloAddress] = value;
                    }
                    else
                    {
                        // Value not found, let's get it
                        if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.Debug("OnRefreshClusterMapTimer: value not found locally for {SiloAddress}", siloAddress);
                        getGrainInterfaceMapTasks.Add(GetTargetSiloGrainInterfaceMap(siloAddress));
                    }
                }

                if (getGrainInterfaceMapTasks.Any())
                {
                    foreach (var keyValuePair in await Task.WhenAll(getGrainInterfaceMapTasks))
                    {
                        if (keyValuePair.Value != null)
                        {
                            newSilosClusterGrainInterfaceMap[keyValuePair.Key] = keyValuePair.Value;
                        }
                    }
                }

                grainTypeManager.SetInterfaceMapsBySilo(newSilosClusterGrainInterfaceMap);

                if (this.versionStore.IsEnabled)
                {
                    await this.GetAndSetDefaultCompatibilityStrategy();
                    foreach (var kvp in await GetStoredCompatibilityStrategies())
                    {
                        this.versionSelectorManager.CompatibilityDirectorManager.SetStrategy(kvp.Key, kvp.Value);
                    }

                    await this.GetAndSetDefaultSelectorStrategy();
                    foreach (var kvp in await GetSelectorStrategies())
                    {
                        this.versionSelectorManager.VersionSelectorManager.SetSelector(kvp.Key, kvp.Value);
                    }
                }

                versionSelectorManager.ResetCache();

                // Either a new silo joined or a refresh failed, so continue until no refresh is required.
                if (hasToRefreshClusterGrainInterfaceMap)
                {
                    if (this.logger.IsEnabled(LogLevel.Debug))
                    {
                        this.logger.LogDebug("OnRefreshClusterMapTimer: cluster type map still requires a refresh and will be refreshed again after a short delay");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                else
                {
                    this.lastUpdatedVersion = members.Version;
                }
            }
        }

        private async Task OnRefreshClusterMapTimer(object _)
        {
            // Check if we have to refresh
            if (!hasToRefreshClusterGrainInterfaceMap)
            {
                if (this.logger.IsEnabled(LogLevel.Trace)) logger.Trace("OnRefreshClusterMapTimer: no refresh required");
                return;
            }

            await this.UpdateClusterTypeMap();
        }

        private async Task GetAndSetDefaultSelectorStrategy()
        {
            try
            {
                var strategy = await this.versionStore.GetSelectorStrategy();
                this.versionSelectorManager.VersionSelectorManager.SetSelector(strategy);
            }
            catch (Exception)
            {
                hasToRefreshClusterGrainInterfaceMap = true;
            }
        }

        private async Task GetAndSetDefaultCompatibilityStrategy()
        {
            try
            {
                var strategy = await this.versionStore.GetCompatibilityStrategy();
                this.versionSelectorManager.CompatibilityDirectorManager.SetStrategy(strategy);
            }
            catch (Exception)
            {
                hasToRefreshClusterGrainInterfaceMap = true;
            }
        }

        private async Task<Dictionary<int, CompatibilityStrategy>> GetStoredCompatibilityStrategies()
        {
            try
            {
                return await this.versionStore.GetCompatibilityStrategies();
            }
            catch (Exception)
            {
                hasToRefreshClusterGrainInterfaceMap = true;
                return new Dictionary<int, CompatibilityStrategy>();
            }
        }

        private async Task<Dictionary<int, VersionSelectorStrategy>> GetSelectorStrategies()
        {
            try
            {
                return await this.versionStore.GetSelectorStrategies();
            }
            catch (Exception)
            {
                hasToRefreshClusterGrainInterfaceMap = true;
                return new Dictionary<int, VersionSelectorStrategy>();
            }
        }

        private async Task<KeyValuePair<SiloAddress, GrainInterfaceMap>> GetTargetSiloGrainInterfaceMap(SiloAddress siloAddress)
        {
            try
            {
                var remoteTypeManager = this.grainFactory.GetSystemTarget<ISiloTypeManager>(Constants.TypeManagerId, siloAddress);
                var siloTypeCodeMap = await scheduler.QueueTask(() => remoteTypeManager.GetSiloTypeCodeMap(), SchedulingContext);
                return new KeyValuePair<SiloAddress, GrainInterfaceMap>(siloAddress, siloTypeCodeMap);
            }
            catch (Exception ex)
            {
				// Will be retried on the next timer hit
                logger.Error(ErrorCode.TypeManager_GetSiloGrainInterfaceMapError, $"Exception when trying to get GrainInterfaceMap for silos {siloAddress}", ex);
				hasToRefreshClusterGrainInterfaceMap = true;
                return new KeyValuePair<SiloAddress, GrainInterfaceMap>(siloAddress, null);
            }
        }

        public void Dispose()
        {
            this.cancellation.Cancel(throwOnFirstException: false);
            if (this.refreshClusterGrainInterfaceMapTimer != null)
            {
                this.refreshClusterGrainInterfaceMapTimer.Dispose();
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            async Task On

        }
    }
}


