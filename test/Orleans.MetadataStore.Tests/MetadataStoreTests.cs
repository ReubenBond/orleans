#if false
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.MetadataStore.Tests
{
    [Trait("Category", "MetadataStore")]
    public class MetadataStoreTests : IClassFixture<MetadataStoreTests.Fixture>
    {
        private readonly ITestOutputHelper output;
        private readonly Fixture fixture;
        public class Fixture
        {
            public Fixture()
            {
                var builder = new TestClusterBuilder();
                builder.Options.InitialSilosCount = 3;
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
                builder.AddClientBuilderConfigurator<ClientConfigurator>();
                var testCluster = builder.Build();
                if (testCluster?.Primary == null)
                {
                    testCluster?.Deploy();
                }

                this.HostedCluster = testCluster;
            }

            public TestCluster HostedCluster { get; }

            public IClusterClient Client => this.HostedCluster?.Client;

            public virtual void Dispose() => this.HostedCluster?.StopAllSilos();
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder builder)
            {
                builder
                    //.ConfigureLogging(logging => logging.AddDebug())
                    .UseAzureStorageClustering(options => options.ConnectionString = "UseDevelopmentStorage=true")
                    .UseMetadataStore()
                    .UseMemoryLocalStore()
                    /*.UseSimpleFileSystemStore(ob => ob.Configure(
                        (SimpleFileSystemStoreOptions options, ITypeResolver typeResolver, IGrainFactory grainFactory) =>
                        {
                            options.JsonSettings = OrleansJsonSerializer.GetDefaultSerializerSettings(typeResolver, grainFactory);
                            options.JsonSettings.Formatting = Formatting.Indented;
                            options.Directory = $@"c:\tmp\db\{Guid.NewGuid().GetHashCode():X}";
                        }))*/
                    //.UseLiteDBLocalStore(options => options.ConnectionString = $@"c:\tmp\db\{Guid.NewGuid().GetHashCode():X}.db")
                    .AddStartupTask<BootstrapCluster>();
            }
        }

        internal class BootstrapCluster : IStartupTask
        {
            private readonly ILocalSiloDetails localSiloDetails;
            private readonly ConfigurationManager configurationManager;
            private readonly IClusterMembershipService membershipService;
            private readonly ILogger<BootstrapCluster> log;
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

            public BootstrapCluster(
                ILocalSiloDetails localSiloDetails,
                ConfigurationManager configurationManager,
                IClusterMembershipService membershipService,
                ILogger<BootstrapCluster> log)
            {
                this.localSiloDetails = localSiloDetails;
                this.configurationManager = configurationManager;
                this.membershipService = membershipService;
                this.log = log;
            }

            public async Task Execute(CancellationToken cancellationToken)
            {
                if (this.configurationManager.AcceptedConfiguration?.Configuration is null)
                {
                    await this.configurationManager.ForceLocalConfiguration(
                        new ReplicaSetConfiguration(new Ballot(1, this.configurationManager.NodeId),
                        1,
                        new[] {this.localSiloDetails.SiloAddress},
                        1,
                        1));
                }

                await this.configurationManager.TryAddServer(this.localSiloDetails.SiloAddress);
                _ = Task.Run(RunAsync);
            }

            private async Task RunAsync()
            {
                try
                {
                    var previous = default(ClusterMembershipSnapshot);
                    await foreach (var snapshot in this.membershipService.MembershipUpdates.WithCancellation(this.cancellation.Token))
                    {
                        var update = previous is null ? snapshot.AsUpdate() : snapshot.CreateUpdate(previous);
                        foreach (var change in update.Changes)
                        {
                            await ProcessUpdate(change);
                        }
                    }
                }
                catch
                {

                }
            }

            private async Task ProcessUpdate(ClusterMember update)
            {
                while (true)
                {
                    var (silo, status) = (update.SiloAddress, update.Status);
                    try
                    {
                        log.LogInformation($"Got silo update: {silo} -> {status}");
                        var reference = silo;
                        UpdateResult<ReplicaSetConfiguration> result;
                        switch (status)
                        {
                            case SiloStatus.Active:
                                result = await this.configurationManager.TryAddServer(reference);
                                break;
                            case SiloStatus.Dead:
                                result = await this.configurationManager.TryRemoveServer(reference);
                                break;
                            default:
                                result = default(UpdateResult<ReplicaSetConfiguration>);
                                break;
                        }

                        log.LogInformation($"Update result: {result}");

                        // Continue until a successful result is obtained.
                        if (result.Success) return;
                        await Task.Delay(TimeSpan.FromMilliseconds(500));
                    }
                    catch (Exception exception)
                    {
                        log.LogError($"Exception processing update ({silo}, {status}): {exception}");
                    }
                }
            }
        }

        public class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder builder)
            {
                //builder.ConfigureLogging(logging => logging.AddDebug());
            }
        }

        public MetadataStoreTests(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
        }

        [Fact, Trait("Category", "BVT")]
        public async Task TryUpdate_SingleProposer()
        {
            var log = new XunitLogger(this.output, $"Client-{1}");
            var grain = this.fixture.Client.GetGrain<IMetadataStoreTestGrain>(Guid.NewGuid());
            var result = await grain.TryUpdate("testKey", new MyVersionedData {Value = "initial", Version = 1});
            log.LogInformation($"Wrote data and got answer: {result}");

            await Task.Delay(5000);
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

        [Fact, Trait("Category", "BVT")]
        public async Task TryUpdate_MultiKey()
        {
            await Task.Delay(15000);
            var log = new XunitLogger(this.output, $"Client-{1}");

            const int outerLoopIterations = 100;
            const int innerLoopIterations = 1000;

            var grains = new List<IMetadataStoreTestGrain>(innerLoopIterations);
            var keys = new List<string>(innerLoopIterations);
            var tasks = new List<Task>(innerLoopIterations);
            for (var i = 0; i < innerLoopIterations; i++)
            {
                var grain = this.fixture.Client.GetGrain<IMetadataStoreTestGrain>(Guid.NewGuid());
                var key = i.ToString();
                grains.Add(grain);
                keys.Add(key);
                tasks.Add(grain.Get<MyVersionedData>(key));

                await Task.WhenAll(tasks);
                tasks.Clear();
            }

            var stopwatch = Stopwatch.StartNew();
            for (var j = 0; j < outerLoopIterations; j++)
            {
                var data = new MyVersionedData
                {
                    Version = j + 1,
                    Value = "Cheetos"
                };

                for (var i = 0; i < innerLoopIterations; i++)
                {
                    tasks.Add(grains[i].TryUpdate(keys[i], data));
                }

                await Task.WhenAll(tasks);
                tasks.Clear();
            }

            stopwatch.Stop();
            log.LogInformation($"{outerLoopIterations * innerLoopIterations} writes in {stopwatch.Elapsed.TotalSeconds} seconds");
            log.LogInformation($"{outerLoopIterations * innerLoopIterations / stopwatch.Elapsed.TotalSeconds} tps");
        }
    }
}
#endif
