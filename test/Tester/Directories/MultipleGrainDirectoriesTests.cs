using Orleans.Internal;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces.Directories;
using Xunit;

namespace Tester.Directories
{
    public abstract class MultipleGrainDirectoriesTests : InProcessTestClusterPerTest
    {
        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 2;
        }

        [SkippableFact, TestCategory("Directory"), TestCategory("Functional")]
        public async Task PingGrain()
        {
            var grainOnPrimary = await GetGrainOnPrimary().WaitAsync(TimeSpan.FromSeconds(5));
            var grainOnSecondary = await GetGrainOnSecondary().WaitAsync(TimeSpan.FromSeconds(5));

            // Setup
            var primaryCounter = await grainOnPrimary.Ping();
            var secondaryCounter = await grainOnSecondary.Ping();

            // Each silo see the activation on the other silo
            Assert.Equal(++primaryCounter, await grainOnSecondary.ProxyPing(grainOnPrimary));
            Assert.Equal(++secondaryCounter, await grainOnPrimary.ProxyPing(grainOnSecondary));

            // Shutdown the secondary silo
            await this.HostedCluster.StopSiloAsync(HostedCluster.Silos[1]);

            // Activation on the primary silo should still be there, another activation should be
            // created for the other one
            Assert.Equal(++primaryCounter, await grainOnPrimary.Ping());
            Assert.Equal(1, await grainOnSecondary.Ping());
        }

        private Task<ICustomDirectoryGrain> GetGrainOnPrimary() => GetGrainOnSilo(HostedCluster.Silos[0].SiloAddress);


        private Task<ICustomDirectoryGrain> GetGrainOnSecondary() => GetGrainOnSilo(HostedCluster.Silos[1].SiloAddress);

        private async Task<ICustomDirectoryGrain> GetGrainOnSilo(SiloAddress siloAddress)
        {
            while (true)
            {
                RequestContext.Set(IPlacementDirector.PlacementHintKey, siloAddress);
                var grain = this.GrainFactory.GetGrain<ICustomDirectoryGrain>(Guid.NewGuid());
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(siloAddress.Endpoint.ToString()))
                    return grain;
            }
        }

    }
}
