using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using UnitTests.GrainInterfaces;
using Orleans.Hosting;
using Orleans.TestingHost;
using UnitTests.Grains;
using Orleans.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace TestVersionGrains
{
    public class VersionGrainsSiloBuilderConfigurator : ISiloBuilderConfigurator
    {
        public void Configure(IHostBuilder hostBuilder, ISiloBuilder siloBuilder)
        {
            var cfg = hostBuilder.GetConfiguration();
            var siloCount = int.Parse(cfg["SiloCount"]);
            var refreshInterval = TimeSpan.Parse(cfg["RefreshInterval"]);
            siloBuilder.Configure<SiloMessagingOptions>(options => options.AssumeHomogenousSilosForTesting = false);
            siloBuilder.Configure<TypeManagementOptions>(options => options.TypeMapRefreshInterval = refreshInterval);
            siloBuilder.Configure<GrainVersioningOptions>(options =>
            {
                options.DefaultCompatibilityStrategy = cfg["CompatibilityStrategy"];
                options.DefaultVersionSelectorStrategy = cfg["VersionSelectorStrategy"];
            });

            siloBuilder.ConfigureServices(this.ConfigureServices)
                 .AddMemoryGrainStorageAsDefault();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingletonNamedService<PlacementStrategy, VersionAwarePlacementStrategy>(nameof(VersionAwarePlacementStrategy));
            services.AddSingletonKeyedService<Type, IPlacementDirector, VersionAwarePlacementDirector>(typeof(VersionAwarePlacementStrategy));
        }
    }

    public class VersionGrainsClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.Configure<GatewayOptions>(options => options.PreferedGatewayIndex = 0);
        }
    }
}
