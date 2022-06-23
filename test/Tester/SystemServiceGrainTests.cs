using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.Hosting;
using System;
using Orleans.Runtime;

namespace UnitTests.General
{
    public interface IPingService : ISystemService
    {
        ValueTask Ping();
    }

    public class PingService : IPingService
    {
        public ValueTask Ping() => default;
    }

    [TestCategory("BVT")]
    public class SystemServiceGrainTests : OrleansTestingBase, IClassFixture<SystemServiceGrainTests.Fixture>
    {
        private readonly Fixture _fixture;

        public SystemServiceGrainTests(Fixture fixture)
        {
            _fixture = fixture;
        }
        
        [Fact]
        public async Task SystemService_PingTest()
        {
            var svc = _fixture.GrainFactory.GetGrain<IPingService>(IdSpan.Create(Guid.NewGuid().ToString()));
            await svc.Ping();
        }

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
                builder.AddClientBuilderConfigurator<ClientConfigurator>();
            }

            private class SiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                }
            }

            private class ClientConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                }
            }
        }

    }
}
