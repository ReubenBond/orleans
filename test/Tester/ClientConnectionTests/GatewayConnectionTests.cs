using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime.Messaging;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Sockets;
using System.Collections.Immutable;

namespace Tester
{
    public class GatewayConnectionTests : TestClusterPerTest
    {
        private class TestGatewayMembershipProvider : IGatewayMembershipProvider
        {
            public MembershipVersion Version { get; set; }

            public ValueTask<GatewayMembershipSnapshot> GetGatewaysAsync(CancellationToken cancellationToken) => new(new GatewayMembershipSnapshot(Gateways, Version));

            /// <summary>
            /// Gets or sets the list of gateways.
            /// </summary>
            public List<GatewayMember> Gateways { get; set; }

            /// <summary>
            /// Adds a gateway described via a TCP endpoint.
            /// </summary>
            /// <param name="endpoint"></param>
            public void AddTcpGateway(IPEndPoint endpoint)
            {
                Gateways.Add(new GatewayMember(
                    SiloAddress.New(endpoint, 0),
                    new[]
                    {
                    new EndpointInfo(ClientOutboundConnectionFactory.DefaultConnectorName)
                    {
                        [TcpMessageTransportConnector.EndpointAddressPropertyName] = endpoint.ToString()
                    }
                    }.ToImmutableArray()));
                Version = Version.Successor();
            }
        }

        private OutsideRuntimeClient runtimeClient;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.UseTestClusterMembership = false;
            builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
            builder.Options.InitialSilosCount = 1;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                hostBuilder.UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder.UseLocalhostClustering();
                });

                hostBuilder.ConfigureServices((context, services) =>
                {
                    var cfg = context.Configuration;
                    var siloPort = int.Parse(cfg[nameof(TestClusterOptions.BaseSiloPort)]);
                    var gatewayPort = int.Parse(cfg[nameof(TestClusterOptions.BaseGatewayPort)]);
                    services.Configure<EndpointOptions>(options =>
                    {
                        options.SiloPort = siloPort;
                        options.GatewayPort = gatewayPort;
                    });
                });
            }
        }

        public class ClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                var basePort = int.Parse(configuration[nameof(TestClusterOptions.BaseGatewayPort)]);
                var primaryGw = new IPEndPoint(IPAddress.Loopback, basePort);
                clientBuilder.Configure<GatewayOptions>(options =>
                {
                    options.GatewayListRefreshPeriod = TimeSpan.FromMilliseconds(100);
                });
                clientBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton(sp =>
                    {
                        var gateway = new TestGatewayMembershipProvider();
                        gateway.AddTcpGateway(primaryGw);
                        return gateway;
                    });
                    services.AddFromExisting<IGatewayMembershipProvider, TestGatewayMembershipProvider>();
                });
            }
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            this.runtimeClient = this.Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
        }

        [Fact, TestCategory("Functional")]
        public async Task NoReconnectionToGatewayNotReturnedByManager()
        {
            // Reduce timeout for this test
            this.runtimeClient.SetResponseTimeout(TimeSpan.FromSeconds(1));

            var connectionCount = 0;
            var timeoutCount = 0;

            // Fake Gateway
            var gatewayMembershipService = this.HostedCluster.Client.ServiceProvider.GetRequiredService<IGatewayMembershipService>();
            await gatewayMembershipService.Refresh();
            var gateways = gatewayMembershipService.CurrentSnapshot;
            var port = gateways.Gateways.First().Key.Endpoint.Port + 2;
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            var evt = new SocketAsyncEventArgs();
            var gatewayManager = this.runtimeClient.ServiceProvider.GetService<TestGatewayMembershipProvider>();
            evt.Completed += (sender, args) =>
            {
                connectionCount++;
                gatewayManager.Gateways.RemoveAll(gw => gw.SiloAddress.Endpoint.Equals(endpoint));
                gatewayManager.Version = gatewayManager.Version.Successor();
            };

            // Add the fake gateway and wait the refresh from the client
            gatewayManager.AddTcpGateway(endpoint);
            await Task.Delay(200);

            using (var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                // Start the fake gw
                socket.Bind(endpoint);
                socket.Listen(1);
                socket.AcceptAsync(evt);

                // Make a bunch of calls
                for (var i = 0; i < 100; i++)
                {
                    try
                    {
                        var g = this.Client.GetGrain<ISimpleGrain>(i);
                        await g.SetA(i);
                    }
                    catch (TimeoutException)
                    {
                        timeoutCount++;
                    }
                }
                socket.Close();
            }

            // Check that we only connected once to the fake GW
            Assert.Equal(1, connectionCount);
            Assert.Equal(1, timeoutCount);
        }

        [Fact, TestCategory("Functional")]
        public async Task ConnectionFromDifferentClusterIsRejected()
        {
            // Arrange
            var gatewayMembershipService = this.HostedCluster.Client.ServiceProvider.GetRequiredService<IGatewayMembershipService>();
            await gatewayMembershipService.Refresh();
            var gateways = gatewayMembershipService.CurrentSnapshot;
            var gwEndpoint = gateways.Gateways.First().Key.Endpoint;
            var exceptions = new List<Exception>();

            Task<bool> RetryFunc(Exception exception, CancellationToken cancellationToken)
            {
                Assert.IsType<ConnectionFailedException>(exception);
                exceptions.Add(exception);
                return Task.FromResult(false);
            }

            // Close current client connection
            await this.HostedCluster.StopClusterClientAsync();
            var hostBuilder = new HostBuilder().UseOrleansClient(
                (ctx, clientBuilder) =>
                {
                    clientBuilder.Configure<ClientMessagingOptions>(
                        options => { options.ResponseTimeoutWithDebugger = TimeSpan.FromSeconds(10); });
                    clientBuilder.Configure<ClusterOptions>(
                        options =>
                        {
                            options.ClusterId = "myClusterId";
                        })
                        .UseStaticClustering(gwEndpoint)
                        .UseConnectionRetryFilter(RetryFunc);
                    ;
                });
            var host = hostBuilder.Build();
            var exception = await Assert.ThrowsAsync<ConnectionFailedException>(async () => await host.StartAsync());
            Assert.Contains("Unable to connect to", exception.Message);
            await host.StopAsync();
        }
    }
}