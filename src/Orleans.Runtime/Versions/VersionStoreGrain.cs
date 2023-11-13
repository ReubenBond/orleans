using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions
{
    [Alias("Orleans.Runtime.Versions.IVersionStoreGrain")]
    internal interface IVersionStoreGrain : IGrainWithStringKey
    {
        [Alias("GetCompatibilityStrategies")]
        Task<Dictionary<GrainInterfaceType, CompatibilityStrategy>> GetCompatibilityStrategies();
        [Alias("GetSelectorStrategies")]
        Task<Dictionary<GrainInterfaceType, VersionSelectorStrategy>> GetSelectorStrategies();
        [Alias("GetCompatibilityStrategy")]
        Task<CompatibilityStrategy> GetCompatibilityStrategy();
        [Alias("GetSelectorStrategy")]
        Task<VersionSelectorStrategy> GetSelectorStrategy();
        [Alias("SetCompatibilityStrategy")]
        Task SetCompatibilityStrategy(CompatibilityStrategy strategy);
        [Alias("SetSelectorStrategy")]
        Task SetSelectorStrategy(VersionSelectorStrategy strategy);
        [Alias("SetCompatibilityStrategy1")]
        Task SetCompatibilityStrategy(GrainInterfaceType interfaceType, CompatibilityStrategy strategy);
        [Alias("SetSelectorStrategy1")]
        Task SetSelectorStrategy(GrainInterfaceType interfaceType, VersionSelectorStrategy strategy);
    }

    [GenerateSerializer]
    [Alias("Orleans.Runtime.Versions.VersionStoreGrainState")]
    internal sealed class VersionStoreGrainState
    {
        [Id(0)]
        public readonly Dictionary<GrainInterfaceType, CompatibilityStrategy> CompatibilityStrategies = new();
        [Id(1)]
        public readonly Dictionary<GrainInterfaceType, VersionSelectorStrategy> VersionSelectorStrategies = new();
        [Id(2)]
        public VersionSelectorStrategy SelectorOverride;
        [Id(3)]
        public CompatibilityStrategy CompatibilityOverride;
    }

    [StorageProvider(ProviderName = ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    internal class VersionStoreGrain : Grain<VersionStoreGrainState>, IVersionStoreGrain
    {
        public async Task SetCompatibilityStrategy(CompatibilityStrategy strategy)
        {
            this.State.CompatibilityOverride = strategy;
            await this.WriteStateAsync();
        }

        public async Task SetSelectorStrategy(VersionSelectorStrategy strategy)
        {
            this.State.SelectorOverride = strategy;
            await this.WriteStateAsync();
        }

        public async Task SetCompatibilityStrategy(GrainInterfaceType ifaceId, CompatibilityStrategy strategy)
        {
            this.State.CompatibilityStrategies[ifaceId] = strategy;
            await this.WriteStateAsync();
        }

        public async Task SetSelectorStrategy(GrainInterfaceType ifaceId, VersionSelectorStrategy strategy)
        {
            this.State.VersionSelectorStrategies[ifaceId] = strategy;
            await this.WriteStateAsync();
        }

        public bool IsEnabled { get; }

        public Task<Dictionary<GrainInterfaceType, CompatibilityStrategy>> GetCompatibilityStrategies()
        {
            return Task.FromResult(this.State.CompatibilityStrategies);
        }

        public Task<Dictionary<GrainInterfaceType, VersionSelectorStrategy>> GetSelectorStrategies()
        {
            return Task.FromResult(this.State.VersionSelectorStrategies);
        }

        public Task<CompatibilityStrategy> GetCompatibilityStrategy()
        {
            return Task.FromResult(this.State.CompatibilityOverride);
        }

        public Task<VersionSelectorStrategy> GetSelectorStrategy()
        {
            return Task.FromResult(this.State.SelectorOverride);
        }
    }
}
