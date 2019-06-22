using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Storage;
using Orleans.Versions;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;

namespace Orleans.Runtime.Versions
{
    internal class GrainVersionStore : IVersionStore, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>();
        private readonly IInternalGrainFactory grainFactory;
        private readonly IServiceProvider services;
        private readonly string clusterId;

        private IVersionStoreGrain StoreGrain => this.grainFactory.GetGrain<IVersionStoreGrain>(this.clusterId);

        public GrainVersionStore(
            ILocalSiloDetails siloDetails,
            IInternalGrainFactory grainFactory,
            IServiceProvider services)
        {
            this.grainFactory = grainFactory;
            this.services = services;
            this.clusterId = siloDetails.ClusterId;
            this.IsEnabled = this.services.GetService<IGrainStorage>() != null;
        }

        public bool IsEnabled { get; }

        public async Task SetCompatibilityStrategy(CompatibilityStrategy strategy)
        {
            await this.WaitUntilReady();
            await StoreGrain.SetCompatibilityStrategy(strategy);
        }

        public async Task SetSelectorStrategy(VersionSelectorStrategy strategy)
        {
            await this.WaitUntilReady();
            await StoreGrain.SetSelectorStrategy(strategy);
        }

        public async Task SetCompatibilityStrategy(int interfaceId, CompatibilityStrategy strategy)
        {
            await this.WaitUntilReady();
            await StoreGrain.SetCompatibilityStrategy(interfaceId, strategy);
        }

        public async Task SetSelectorStrategy(int interfaceId, VersionSelectorStrategy strategy)
        {
            await this.WaitUntilReady();
            await StoreGrain.SetSelectorStrategy(interfaceId, strategy);
        }

        public async Task<Dictionary<int, CompatibilityStrategy>> GetCompatibilityStrategies()
        {
            await this.WaitUntilReady();
            return await StoreGrain.GetCompatibilityStrategies();
        }

        public async Task<Dictionary<int, VersionSelectorStrategy>> GetSelectorStrategies()
        {
            await this.WaitUntilReady();
            return await StoreGrain.GetSelectorStrategies();
        }

        public async Task<CompatibilityStrategy> GetCompatibilityStrategy()
        {
            await this.WaitUntilReady();
            return await StoreGrain.GetCompatibilityStrategy();
        }

        public async Task<VersionSelectorStrategy> GetSelectorStrategy()
        {
            await this.WaitUntilReady();
            return await StoreGrain.GetSelectorStrategy();
        }

        private async Task WaitUntilReady()
        {
            if (!this.IsEnabled) ThrowNotEnabled();
            await this.ready.Task;

            void ThrowNotEnabled() => throw new OrleansException("Version store not enabled, make sure the store is configured");
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            Task OnApplicationServicesStart(CancellationToken ct)
            {
                this.ready.TrySetResult(true);
                return Task.CompletedTask;
            }

            Task OnApplicationServicesStop(CancellationToken ct)
            {
                this.ready.TrySetResult(true);
                return Task.CompletedTask;
            }

            lifecycle.Subscribe(
                nameof(GrainVersionStore),
                ServiceLifecycleStage.ApplicationServices,
                OnApplicationServicesStart,
                OnApplicationServicesStop);
        }
    }
}
