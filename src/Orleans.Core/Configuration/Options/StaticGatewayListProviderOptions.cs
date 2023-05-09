using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using Orleans.Connections.Transport;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for configuring a static list of gateways.
    /// </summary>
    /// <remarks>>
    /// See <see cref="ClientBuilderExtensions.UseStaticClustering(IClientBuilder, System.Net.IPEndPoint[])"/> for more information.
    /// </remarks>
    public class StaticGatewayListProviderOptions
    {
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
                    new EndpointInfo("gw")
                    {
                        ["ep"] = endpoint.ToString()
                    }
                }.ToImmutableArray()));
        }
    }
}
