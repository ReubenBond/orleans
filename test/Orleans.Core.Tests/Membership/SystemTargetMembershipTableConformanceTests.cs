using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Clustering.TestKit;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.MembershipTests;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class SystemTargetMembershipTableConformanceTests : MembershipTableConformanceTestsBase
{
    protected override MembershipTableTestFixture CreateConformanceFixture()
    {
        var targets = new Dictionary<string, TargetScope>(StringComparer.Ordinal);
        return new MembershipTableTestFixture(
            nameof(SystemTargetBasedMembershipTable),
            async (serviceId, clusterId, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!targets.TryGetValue(clusterId, out var target))
                {
                    target = await TargetScope.CreateAsync(serviceId, clusterId, cancellationToken);
                    targets.Add(clusterId, target);
                }

                return target.CreateClient();
            });
    }

    private sealed class TargetScope
    {
        private readonly InProcessTestCluster _cluster;
        private readonly InProcessSiloHandle _silo;
        private int _clientCount;

        private TargetScope(InProcessTestCluster cluster)
        {
            _cluster = cluster;
            _silo = Assert.Single(cluster.Silos);
        }

        public static async Task<TargetScope> CreateAsync(string serviceId, string clusterId, CancellationToken cancellationToken)
        {
            var builder = new InProcessTestClusterBuilder(1);
            builder.Options.ServiceId = serviceId;
            builder.Options.ClusterId = $"host-{clusterId}";
            builder.Options.ConfigureFileLogging = false;
            builder.Options.InitializeClientOnDeploy = false;
            builder.ConfigureSiloHost((_, host) =>
            {
                host.Services.AddSingleton(provider => new MembershipTableSystemTarget(
                    provider.GetRequiredService<ILogger<MembershipTableSystemTarget>>(),
                    provider.GetRequiredService<DeepCopier>(),
                    Options.Create(new ClusterOptions { ServiceId = serviceId, ClusterId = clusterId }),
                    provider.GetRequiredService<SystemTargetShared>()));
                host.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(
                    provider => provider.GetRequiredService<MembershipTableSystemTarget>());
            });

            var cluster = builder.Build();
            try
            {
                await cluster.DeployAsync(cancellationToken);
                var scope = new TargetScope(cluster);
                await scope.AssertHostMembershipIsIntact(cancellationToken);
                var proxy = scope.GetGrainFactory().GetSystemTarget<IMembershipTableSystemTarget>(
                    Constants.SystemMembershipTableType, SiloAddress.New(scope._silo.SiloAddress.Endpoint, 0));
                Assert.IsAssignableFrom<GrainReference>(proxy);
                Assert.Empty((await proxy.ReadAllAsync(cancellationToken)).Members);
                return scope;
            }
            catch (Exception failure)
            {
                try
                {
                    await cluster.DisposeAsync();
                }
                catch (Exception cleanup)
                {
                    failure.Data["ClusterDisposalFailure"] = cleanup;
                }

                throw;
            }
        }

        public MembershipTableTestHandle CreateClient()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IOptions<DevelopmentClusterMembershipOptions>>(
                Options.Create(new DevelopmentClusterMembershipOptions { PrimarySiloEndpoint = _silo.SiloAddress.Endpoint }));
            services.AddSingleton(_silo.ServiceProvider.GetRequiredService<ILocalSiloDetails>());
            services.AddSingleton(GetGrainFactory());
            var clientServices = services.BuildServiceProvider();
            var client = new SystemTargetBasedMembershipTable(
                clientServices,
                NullLogger<SystemTargetBasedMembershipTable>.Instance);
            Interlocked.Increment(ref _clientCount);
            return new MembershipTableTestHandle(client, async () =>
            {
                try
                {
                    await clientServices.DisposeAsync();
                }
                finally
                {
                    if (Interlocked.Decrement(ref _clientCount) == 0)
                    {
                        try
                        {
                            await AssertHostMembershipIsIntact(CancellationToken.None);
                        }
                        finally
                        {
                            await _cluster.DisposeAsync();
                        }
                    }
                }
            });
        }

        private IInternalGrainFactory GetGrainFactory() => _silo.ServiceProvider.GetRequiredService<IInternalGrainFactory>();

        private async Task AssertHostMembershipIsIntact(CancellationToken cancellationToken)
        {
            var membership = _silo.ServiceProvider.GetRequiredService<IMembershipTable>();
            Assert.Equal("InProcessMembershipTable", membership.GetType().Name);
            var row = Assert.Single((await membership.ReadAllAsync(cancellationToken)).Members).Item1;
            Assert.Equal(_silo.SiloAddress, row.SiloAddress);
            Assert.Equal(SiloStatus.Active, row.Status);
        }
    }
}
