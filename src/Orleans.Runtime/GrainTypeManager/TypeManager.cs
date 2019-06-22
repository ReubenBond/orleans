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
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Providers;

namespace Orleans.Runtime
{
    internal class TypeManager : SystemTarget, IClusterTypeManager, ISiloTypeManager, IDisposable, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly ILogger logger;
        private readonly GrainTypeManager grainTypeManager;
        private readonly ImplicitStreamSubscriberTable implicitStreamSubscriberTable;
        private readonly IInternalGrainFactory grainFactory;
        private readonly CachedVersionSelectorManager versionSelectorManager;
        private readonly IClusterMembership clusterMembership;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly OrleansTaskScheduler scheduler;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly IAsyncTimer refreshTimer;
        private readonly IVersionStore versionStore;
        private bool needsRefresh;
        private MembershipVersion lastUpdatedVersion;

        public TypeManager(
            ILocalSiloDetails localSiloDetails,
            GrainTypeManager grainTypeManager,
            OrleansTaskScheduler scheduler,
            IOptions<TypeManagementOptions> typeManagementOptions,
            ImplicitStreamSubscriberTable implicitStreamSubscriberTable,
            IInternalGrainFactory grainFactory,
            CachedVersionSelectorManager versionSelectorManager,
            ILoggerFactory loggerFactory,
            IClusterMembership clusterMembership,
            IFatalErrorHandler fatalErrorHandler,
            IAsyncTimerFactory timerFactory,
            IVersionStore versionStore,
            SiloProviderRuntime siloProviderRuntime)
            : base(Constants.TypeManagerId, localSiloDetails.SiloAddress, loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<TypeManager>();
            this.grainTypeManager = grainTypeManager ?? throw new ArgumentNullException(nameof(grainTypeManager));
            this.implicitStreamSubscriberTable = implicitStreamSubscriberTable ?? throw new ArgumentNullException(nameof(implicitStreamSubscriberTable));
            this.grainFactory = grainFactory;
            this.versionSelectorManager = versionSelectorManager;
            this.clusterMembership = clusterMembership;
            this.fatalErrorHandler = fatalErrorHandler;
            this.versionStore = versionStore;
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            siloProviderRuntime.RegisterSystemTarget(this);

            // We need this so we can place needed local activations
            this.grainTypeManager.SetInterfaceMapsBySilo(new Dictionary<SiloAddress, GrainInterfaceMap>
            {
                {this.Silo, grainTypeManager.GetTypeCodeMap()}
            });

            this.refreshTimer = timerFactory.Create(
                typeManagementOptions.Value.TypeMapRefreshInterval,
                nameof(TypeManager) + ".RefreshTypeMap");
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

        private async Task ProcessMembershipUpdates()
        {
            IAsyncEnumerator<ClusterMembershipSnapshot> enumerator = default;
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug("Starting to process membership updates");
                enumerator = this.clusterMembership.MembershipUpdates.GetAsyncEnumerator(this.cancellation.Token);
                var membershipUpdate = enumerator.MoveNextAsync().AsTask();
                var timerTick = this.refreshTimer.NextTick();
                while (true)
                {
                    var task = await Task.WhenAny(membershipUpdate, timerTick);
                    if (this.cancellation.IsCancellationRequested || !await task)
                    {
                        return;
                    }

                    if (ReferenceEquals(task, membershipUpdate))
                    {
                        membershipUpdate = enumerator.MoveNextAsync().AsTask();
                    }

                    if (ReferenceEquals(task, timerTick))
                    {
                        timerTick = this.refreshTimer.NextTick();
                    }

                    await this.scheduler.QueueTask(() => this.UpdateClusterTypeMap(), this.SchedulingContext);
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
            this.needsRefresh |= this.lastUpdatedVersion >= this.clusterMembership.CurrentSnapshot.Version;
            while (needsRefresh)
            {
                needsRefresh = false;

                var members = this.clusterMembership.CurrentSnapshot;

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

                    if (knownSilosClusterGrainInterfaceMap.TryGetValue(siloAddress, out var value))
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
                if (needsRefresh)
                {
                    if (this.logger.IsEnabled(LogLevel.Debug))
                    {
                        this.logger.LogDebug("OnRefreshClusterMapTimer: cluster type map still requires a refresh and will be refreshed again after a short delay");
                    }

                    if (!await this.refreshTimer.NextTick(TimeSpan.FromSeconds(1))) return;
                }
                else
                {
                    this.lastUpdatedVersion = members.Version;
                }
            }
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
                needsRefresh = true;
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
                needsRefresh = true;
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
                needsRefresh = true;
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
                needsRefresh = true;
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
				needsRefresh = true;
                return new KeyValuePair<SiloAddress, GrainInterfaceMap>(siloAddress, null);
            }
        }

        public void Dispose()
        {
            this.cancellation.Cancel(throwOnFirstException: false);
            this.refreshTimer.Dispose();
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var tasks = new List<Task>(1);
            Task OnRuntimeGrainServicesStart(CancellationToken ct)
            {
                tasks.Add(Task.Run(() => this.ProcessMembershipUpdates()));
                return Task.CompletedTask;
            }

            async Task OnRuntimeGrainServicesStop(CancellationToken ct)
            {
                this.cancellation.Cancel(throwOnFirstException: false);
                this.refreshTimer.Dispose();
                await Task.WhenAny(ct.WhenCancelled(), Task.WhenAll(tasks));
            }

            lifecycle.Subscribe(
                nameof(TypeManager),
                ServiceLifecycleStage.RuntimeGrainServices,
                OnRuntimeGrainServicesStart,
                OnRuntimeGrainServicesStop);
        }
    }
}


