// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;

namespace TestVersionGrains
{
    public class VersionGrainsSiloBuilderConfigurator : IHostConfigurator
    {
        public void Configure(IHostBuilder hostBuilder)
        {
            var cfg = hostBuilder.GetConfiguration();
            var siloCount = int.Parse(cfg["SiloCount"]);
            hostBuilder.UseOrleans((ctx, siloBuilder) =>
            {
                siloBuilder.Configure<SiloMessagingOptions>(options => options.AssumeHomogenousSilosForTesting = false);
                siloBuilder.Configure<GrainVersioningOptions>(options =>
                {
                    options.DefaultCompatibilityStrategy = cfg["CompatibilityStrategy"];
                    options.DefaultVersionSelectorStrategy = cfg["VersionSelectorStrategy"];
                });

                siloBuilder.ConfigureServices(ConfigureServices)
                    .AddMemoryGrainStorageAsDefault();
            });
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddPlacementDirector<VersionAwarePlacementStrategy, VersionAwarePlacementDirector>();
        }
    }
}
