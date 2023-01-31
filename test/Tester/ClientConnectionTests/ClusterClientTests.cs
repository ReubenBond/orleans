using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester.ClientConnectionTests
{
    [TestCategory("Functional")]
    public class ClusterClientTests : TestClusterPerTest
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
        }

        /// <summary>
        /// Ensures that ClusterClient.Connect can be retried.
        /// </summary>
        [Fact]
        public async Task ConnectIsRetryableTest()
        {
            var gatewayMembershipService = this.HostedCluster.Client.ServiceProvider.GetRequiredService<IGatewayMembershipService>();
            await gatewayMembershipService.Refresh();

            // Create a client with no gateway endpoint and then add a gateway endpoint when the client fails to connect.
            var gatewayProvider = new TestGatewayMembershipProvider();
            gatewayProvider.Snapshot = new GatewayMembershipSnapshot(Array.Empty<GatewayMember>(), default);
            var exceptions = new List<Exception>();

            Task<bool> RetryFunc(Exception exception, CancellationToken cancellationToken)
            {
                Assert.IsType<SiloUnavailableException>(exception);
                exceptions.Add(exception);
                gatewayProvider.Snapshot = gatewayMembershipService.CurrentSnapshot;
                return Task.FromResult(true);
            }

            using var host = new HostBuilder().UseOrleansClient((ctx, clientBuilder) =>
                {
                    clientBuilder
                        .Configure<ClusterOptions>(options =>
                        {
                            var existingClientOptions = this.HostedCluster.ServiceProvider
                                .GetRequiredService<IOptions<ClusterOptions>>().Value;
                            options.ClusterId = existingClientOptions.ClusterId;
                            options.ServiceId = existingClientOptions.ServiceId;
                        })
                        .ConfigureServices(services => services.AddSingleton<IGatewayMembershipProvider>(gatewayProvider))
                        .UseConnectionRetryFilter(RetryFunc);
                })
                .Build();

            var client = host.Services.GetRequiredService<IClusterClient>();

            await host.StartAsync();
            Assert.Single(exceptions);
            await host.StopAsync();
        }

        private class TestGatewayMembershipProvider : IGatewayMembershipProvider
        {
            public GatewayMembershipSnapshot Snapshot { get; set; }

            public ValueTask<GatewayMembershipSnapshot> GetGatewaysAsync(CancellationToken cancellationToken) => new(Snapshot);
        }
    }
}