//#define USE_SQL_SERVER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Internal;
using Xunit;
using Xunit.Abstractions;
using System.Threading;
using System.Collections.Immutable;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Sockets;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Messaging;

namespace UnitTests.MessageCenterTests
{
    public class GatewaySelectionTest
    {
        protected readonly ITestOutputHelper output;

        protected static readonly List<SiloAddress> gatewayAddresses = new[]
        {
            SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 0), 0),
            SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1), 0),
            SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 2), 0),
            SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 3), 0),
        }.ToList();
        
        public GatewaySelectionTest(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("BVT"), TestCategory("Gateway")]
        public async Task GatewaySelection()
        {
            var listProvider = new GatewayMembershipService(Options.Create(new GatewayOptions()), new TestListProvider(gatewayAddresses), NullLoggerFactory.Instance.CreateLogger<GatewayMembershipService>());
            await Test_GatewaySelection(listProvider);
        }

        protected async Task Test_GatewaySelection(IGatewayMembershipService gatewayMembershipService)
        {
            await gatewayMembershipService.Refresh();
            var snapshot = gatewayMembershipService.CurrentSnapshot;
            Assert.True(snapshot.Gateways.Count > 0, $"Found some gateways. Data = {snapshot}");

            var gatewayManager = new GatewayManager(Options.Create(new GatewayOptions()), gatewayMembershipService, NullLoggerFactory.Instance, null);
            await gatewayManager.StartAsync(CancellationToken.None);

            var counts = new int[4];

            for (int i = 0; i < 2300; i++)
            {
                var ip = gatewayManager.GetLiveGateway();
                var addr = ip.Endpoint.Address;
                Assert.Equal(IPAddress.Loopback, addr);  // "Incorrect IP address returned for gateway"
                Assert.True(0 < ip.Endpoint.Port && ip.Endpoint.Port < 5, "Incorrect IP port returned for gateway");
                counts[ip.Endpoint.Port - 1]++;
            }

            // The following needed to be changed as the gateway manager now round-robins through the available gateways, rather than
            // selecting randomly based on load numbers.
            //Assert.True((500 < counts[0]) && (counts[0] < 1500), "Gateway selection is incorrectly skewed");
            //Assert.True((500 < counts[1]) && (counts[1] < 1500), "Gateway selection is incorrectly skewed");
            //Assert.True((125 < counts[2]) && (counts[2] < 375), "Gateway selection is incorrectly skewed");
            //Assert.True((25 < counts[3]) && (counts[3] < 75), "Gateway selection is incorrectly skewed");
            //Assert.True((287 < counts[0]) && (counts[0] < 1150), "Gateway selection is incorrectly skewed");
            //Assert.True((287 < counts[1]) && (counts[1] < 1150), "Gateway selection is incorrectly skewed");
            //Assert.True((287 < counts[2]) && (counts[2] < 1150), "Gateway selection is incorrectly skewed");
            //Assert.True((287 < counts[3]) && (counts[3] < 1150), "Gateway selection is incorrectly skewed");

            int low = 2300 / 4;
            int up = 2300 / 4;
            Assert.True((low <= counts[0]) && (counts[0] <= up), "Gateway selection is incorrectly skewed. " + counts[0]);
            Assert.True((low <= counts[1]) && (counts[1] <= up), "Gateway selection is incorrectly skewed. " + counts[1]);
            Assert.True((low <= counts[2]) && (counts[2] <= up), "Gateway selection is incorrectly skewed. " + counts[2]);
            Assert.True((low <= counts[3]) && (counts[3] <= up), "Gateway selection is incorrectly skewed. " + counts[3]);
        }

        private class TestListProvider : IGatewayMembershipProvider
        {
            private readonly GatewayMembershipSnapshot _snapshot;

            public TestListProvider(List<SiloAddress> gateways)
            {
                List<GatewayMember> members = new List<GatewayMember>();
                foreach (var gw in gateways)
                {
                    var ep = new EndpointInfo(ClientOutboundConnectionFactory.DefaultConnectorName)
                {
                    [TcpMessageTransportConnector.EndpointAddressPropertyName] = gw.Endpoint.ToString(),
                };
                    members.Add(new GatewayMember(gw, ImmutableArray.Create(new[] { ep })));
                }

                _snapshot = new GatewayMembershipSnapshot(members, default(MembershipVersion).Successor());
            }

            public ValueTask<GatewayMembershipSnapshot> GetGatewaysAsync(CancellationToken cancellationToken) => new(_snapshot);
        }
    }
}
