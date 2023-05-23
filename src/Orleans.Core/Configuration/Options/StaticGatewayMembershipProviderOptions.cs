using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Sockets;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for configuring a static list of gateways.
    /// </summary>
    /// <remarks>>
    /// See <see cref="ClientBuilderExtensions.UseStaticClustering(IClientBuilder, System.Net.IPEndPoint[])"/> for more information.
    /// </remarks>
    public class StaticGatewayMembershipProviderOptions
    {
        /// <summary>
        /// Gets or sets the list of gateways.
        /// </summary>
        public List<GatewayMember> Gateways { get; set; } = new();

        /// <summary>
        /// Adds a gateway described via a TCP endpoint.
        /// </summary>
        /// <param name="endpoint">The endpoint.</param>
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
        }
    }
}
