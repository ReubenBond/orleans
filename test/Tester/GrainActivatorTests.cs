using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Orleans.Hosting;

using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace UnitTests.General
{
    [TestCategory("BVT"), TestCategory("OneWay")]
    public class OneWayDeactivationTests : OrleansTestingBase, IClassFixture<OneWayDeactivationTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<Configurator>();
                builder.Options.InitialSilosCount = 3;
            }
        }

        public class Configurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.ConfigureLogging(logging => logging.AddDebug());
            }
        }

        public OneWayDeactivationTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// Tests that calling [OneWay] methods on an activation which no longer exists triggers a cache invalidation.
        /// Subsequent calls should reactivate the grain.
        /// </summary>
        [Fact]
        public async Task OneWay_Deactivation_CacheInvalidated()
        {
            IOneWayGrain grainToCallFrom;
            while (true)
            {
                grainToCallFrom = this.fixture.Client.GetGrain<IOneWayGrain>(Guid.NewGuid());
                var grainHost = await grainToCallFrom.GetSiloAddress();
                if (grainHost.Equals(this.fixture.HostedCluster.Primary.SiloAddress))
                {
                    break;
                }
            }

            // Activate the grain & record its address.
            var grainToDeactivate = await grainToCallFrom.GetOtherGrain();
            var initialActivationAddress = await grainToCallFrom.GetActivationAddress(grainToDeactivate);

            // Deactivate the grain.
            await grainToDeactivate.Deactivate();

            // We expect cache invalidation to propagate quickly, but will wait a few seconds just in case.
            var remainingAttempts = 50;
            bool cacheUpdated;
            do
            {
                // Have the first grain make a one-way call to the grain which was deactivated.
                // The purpose of this is to trigger a cache invalidation rejection response.
                _ = grainToCallFrom.NotifyOtherGrain();

                // Ask the first grain for its cached value of the second grain's activation address.
                // This value should eventually be updated to a new activation because of the cache invalidation.
                var activationAddress = await grainToCallFrom.GetActivationAddress(grainToDeactivate);

                Assert.True(--remainingAttempts > 0);

                cacheUpdated = !string.Equals(activationAddress, initialActivationAddress);
                if (!cacheUpdated) await Task.Delay(TimeSpan.FromMilliseconds(100));

            } while (!cacheUpdated);
        }
    }

    [TestCategory("DI")]
    public class GrainActivatorTests : OrleansTestingBase, IClassFixture<GrainActivatorTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<TestSiloBuilderConfigurator>();
            }

            private class TestSiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder.ConfigureServices(services =>
                        services.Replace(ServiceDescriptor.Singleton(typeof(IGrainActivator), typeof(HardcodedGrainActivator))));
                }
            }
        }

        public GrainActivatorTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanUseCustomGrainActivatorToCreateGrains()
        {
            ISimpleDIGrain grain = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            var actual = await grain.GetStringValue();
            Assert.Equal(HardcodedGrainActivator.HardcodedValue, actual);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task CanUseCustomGrainActivatorToReleaseGrains()
        {
            ISimpleDIGrain grain1 = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            long initialReleasedInstances = await grain1.GetLongValue();

            ISimpleDIGrain grain2 = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            long secondReleasedInstances = await grain2.GetLongValue();

            Assert.Equal(initialReleasedInstances, secondReleasedInstances);

            await grain1.DoDeactivate();
            await Task.Delay(250);

            ISimpleDIGrain grain3 = this.fixture.GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId(), grainClassNamePrefix: "UnitTests.Grains.ExplicitlyRegistered");
            long finalReleasedInstances = await grain3.GetLongValue();
            Assert.Equal(initialReleasedInstances + 1, finalReleasedInstances);
        }

        private class HardcodedGrainActivator : DefaultGrainActivator, IGrainActivator
        {
            public const string HardcodedValue = "Hardcoded Test Value";
            private int numberOfReleasedInstances;
            public HardcodedGrainActivator(IServiceProvider service) : base(service)
            {
            }

            public override object Create(IGrainActivationContext context)
            {
                if (context.GrainType == typeof(ExplicitlyRegisteredSimpleDIGrain))
                {
                    return new ExplicitlyRegisteredSimpleDIGrain(new InjectedService(NullLoggerFactory.Instance), HardcodedValue, numberOfReleasedInstances);
                }

                return base.Create(context);
            }

            public override void Release(IGrainActivationContext context, object grain)
            {
                if (context.GrainType == typeof(ExplicitlyRegisteredSimpleDIGrain))
                {
                    numberOfReleasedInstances++;
                }
                else
                {
                    base.Release(context, grain);
                }
            }
        }
    }
}
