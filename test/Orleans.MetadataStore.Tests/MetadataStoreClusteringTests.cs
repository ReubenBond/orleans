#if false
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.TestingHost;
using Serilog;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.MetadataStore.Tests
{
    [Trait("Category", "MetadataStore")]
    public class MetadataStoreClusteringTests : IClassFixture<MetadataStoreClusteringTests.Fixture>
    {
        private readonly ITestOutputHelper output;
        private readonly Fixture fixture;

        public class Fixture : IAsyncLifetime
        {
            private const int NumSilos = 3;
            private readonly CancellationTokenSource shutdownToken = new CancellationTokenSource();
            private readonly object lockObj = new object();
            private ImmutableList<InProcessSiloHandle> siloHandles = ImmutableList<InProcessSiloHandle>.Empty;

            public TestCluster HostedCluster { get; private set; }

            public IClusterClient Client => this.HostedCluster?.Client;

            public async Task DisposeAsync()
            {
                if (this.HostedCluster is TestCluster cluster)
                {
                    foreach (var silo in cluster.GetActiveSilos().ToList())
                    {
                        await silo.StopSiloAsync(stopGracefully: true);
                    }
                }

                shutdownToken.Cancel();
            }

            public async Task InitializeAsync()
            {
                var builder = new TestClusterBuilder(0)
                {
                    Options =
                    {
                        UseTestClusterMembership = false,
                        InitializeClientOnDeploy = false
                    },
                    CreateSiloAsync = CreateSiloAsync
                };
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
                builder.AddClientBuilderConfigurator<ClientConfigurator>();
                var testCluster = builder.Build();

                // Ensure silos see their values from config.
                _ = Task.Run(() => UpdateSiloNodes());

                // Start the new silos.
                await testCluster.StartAdditionalSilosAsync(NumSilos);

                UpdateClientGateways(testCluster);

                _ = Task.Run(async () =>
                {
                    while (!shutdownToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5));
                        UpdateClientGateways(testCluster);
                    }
                });

                testCluster.InitializeClient();
                this.HostedCluster = testCluster;
            }

            private async Task<SiloHandle> CreateSiloAsync(string siloName, IConfiguration configuration)
            {
                var silo = TestClusterHostFactory.CreateSiloHost(siloName, configuration);
                var siloDetails = silo.Services.GetRequiredService<ILocalSiloDetails>();
                var handle = new InProcessSiloHandle
                {
                    Name = siloName,
                    SiloHost = silo,
                    SiloAddress = siloDetails.SiloAddress,
                    GatewayAddress = siloDetails.GatewayAddress,
                };

                lock (lockObj)
                {
                    siloHandles = siloHandles.Add(handle);
                }

                await silo.StartAsync().ConfigureAwait(false);
                return handle;
            }

            private async Task UpdateSiloNodes()
            {
                var didReload = false;
                while (!shutdownToken.IsCancellationRequested)
                {
                    if (didReload) await Task.Delay(TimeSpan.FromSeconds(2));
                    else await Task.Delay(TimeSpan.FromSeconds(15));

                    var seedNodes = siloHandles.Select(s => s.SiloAddress.Endpoint).ToArray();
                    didReload = false;
                    foreach (var siloHandle in siloHandles)
                    {
                        var services = siloHandle.SiloHost.Services;
                        var options = services.GetRequiredService<IOptionsMonitor<MetadataStoreClusteringOptions>>();
                        var updater = services.GetOptionsUpdater<MetadataStoreClusteringOptions>();
                        updater.ConfigureOptions = o =>
                        {
                            o.MinimumNodes = NumSilos / 2 + 1;
                            o.SeedNodes = seedNodes;
                        };

                        if (options.CurrentValue.SeedNodes is null || !seedNodes.SequenceEqual(options.CurrentValue.SeedNodes))
                        {
                            didReload = true;
                            updater.Reload();
                        }
                    }
                }
            }

            private void UpdateClientGateways(TestCluster testCluster)
            {
                var services = testCluster.Client?.ServiceProvider;
                if (services is null) return;
                var options = services.GetRequiredService<IOptionsMonitor<StaticGatewayListProviderOptions>>();
                var updater = services.GetOptionsUpdater<StaticGatewayListProviderOptions>();

                var allGateways = testCluster.GetActiveSilos().Select(s => s.GatewayAddress.ToGatewayUri()).ToList();
                updater.ConfigureOptions = o => o.Gateways = allGateways;

                if (options.CurrentValue.Gateways is null || !allGateways.SequenceEqual(options.CurrentValue.Gateways))
                {
                    updater.Reload();
                }
            }
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder builder)
            {
                builder
                    .Configure<SiloMessagingOptions>(o => o.ResponseTimeoutWithDebugger = TimeSpan.FromSeconds(30))
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<MetadataStoreMembershipTable>();
                        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, MetadataStoreMembershipTable>();
                        services.AddFromExisting<IMembershipTable, MetadataStoreMembershipTable>();

                        // Use dynamically refreshed options to contain the seed nodes.
                        services.AddDynamicOptions<MetadataStoreClusteringOptions>();

                        /*
                        Log.Logger = new LoggerConfiguration()
                            .WriteTo.Seq("http://localhost:5341")
                            .Enrich.WithProperty("Node", builder.GetConfigurationValue("SiloPort"))
                            .CreateLogger();
                         */
                        services.AddLogging(logging => logging.AddDebug());
                    })
                    .UseMetadataStore()
                    .UseMemoryLocalStore();
            }
        }

        public class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder builder)
            {
                builder.ConfigureLogging(logging => logging.AddDebug());
                builder.Configure<ClientMessagingOptions>(o => o.ResponseTimeoutWithDebugger = TimeSpan.FromSeconds(30));
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IGatewayListProvider, StaticGatewayListProvider>();
                    services.AddDynamicOptions<StaticGatewayListProviderOptions>();
                });
            }
        }

        public MetadataStoreClusteringTests(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
        }

        [Fact, Trait("Category", "BVT")]
        public async Task MetadataStore_Membership_Basic()
        {
            var log = new XunitLogger(this.output, $"Client-{1}");
            var grain = this.fixture.Client.GetGrain<IMetadataStoreTestGrain>(Guid.NewGuid());
            var result = await grain.TryUpdate("testKey", new MyVersionedData {Value = "initial", Version = 1});
            log.LogInformation($"Wrote data and got answer: {result}");

            var configResult = await grain.Get<ReplicaSetConfiguration>("MDS.Config");
            log.LogInformation($"Read config and got answer: {configResult}");

            var readResult = await grain.Get<MyVersionedData>("testKey");
            log.LogInformation($"Read data and got answer: {readResult}");

            var data = readResult.Value;
            for (var i = 0; i < 1000; i++)
            {
                data.Version++;
                //data.Value = data.Value + ", " + i;
                result = await grain.TryUpdate("testKey", data);
                //log.LogInformation($"Wrote data and got answer: {JsonConvert.SerializeObject(result, Formatting.Indented)}");
                data = result.Value;
            }
        }

        public ValueTask DisposeAsync()
        {
            throw new NotImplementedException();
        }
    }
}
#endif
