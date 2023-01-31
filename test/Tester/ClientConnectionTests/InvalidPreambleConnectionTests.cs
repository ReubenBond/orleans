using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester.ClientConnectionTests
{
    public class InvalidPreambleConnectionTests : TestClusterPerTest
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
        }

        [Fact, TestCategory("Functional")]
        public async Task ShouldCloseConnectionWhenClientSendsInvalidPreambleSize()
        {
            var gatewayMembershipService = this.HostedCluster.Client.ServiceProvider.GetRequiredService<IGatewayMembershipService>();
            await gatewayMembershipService.Refresh();
            var gateways = gatewayMembershipService.CurrentSnapshot.Gateways.Keys.ToList();
            var gwEndpoint = gateways.First().Endpoint;

            using (Socket s = new Socket(gwEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                s.Connect(gwEndpoint);

                Int32 invalidSize = 99999;
                s.Send(BitConverter.GetBytes(invalidSize));

                bool socketClosed = s.Poll(100000, SelectMode.SelectRead) && s.Available == 0;
                Assert.True(socketClosed);
            }
        }
    }
}
