using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Messaging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures the Silo endpoint options
    /// </summary>
    public class EndpointOptions
    {
        private readonly ConnectionBuilderDelegates inboundConnectionBuilder = new ConnectionBuilderDelegates();

        private readonly ConnectionBuilderDelegates gatewayConnectionBuilder = new ConnectionBuilderDelegates();

        private readonly ConnectionBuilderDelegates outboundConnectionBuilder = new ConnectionBuilderDelegates();

        /// <summary>
        /// The IP address used for clustering.
        /// </summary>
        public IPAddress AdvertisedIPAddress { get; set; }

        /// <summary>
        /// The port this silo uses for silo-to-silo communication.
        /// </summary>
        public int SiloPort { get; set; } = DEFAULT_SILO_PORT;
        public const int DEFAULT_SILO_PORT = 11111;

        /// <summary>
        /// The port this silo uses for silo-to-client (gateway) communication. Specify 0 to disable gateway functionality.
        /// </summary>
        public int GatewayPort { get; set; } = DEFAULT_GATEWAY_PORT;
        public const int DEFAULT_GATEWAY_PORT = 30000;

        /// <summary>
        /// The endpoint used to listen for silo to silo communication. 
        /// If not set will default to <see cref="AdvertisedIPAddress"/> + <see cref="SiloPort"/>
        /// </summary>
        public IPEndPoint SiloListeningEndpoint { get; set; }

        /// <summary>
        /// The endpoint used to listen for silo to silo communication. 
        /// If not set will default to <see cref="AdvertisedIPAddress"/> + <see cref="GatewayPort"/>
        /// </summary>
        public IPEndPoint GatewayListeningEndpoint { get; set; }

        public void ConfigureInboundConnections(Action<IConnectionBuilder> configure) => this.inboundConnectionBuilder.Add(configure);
        public void ConfigureGatewayConnections(Action<IConnectionBuilder> configure) => this.gatewayConnectionBuilder.Add(configure);
        public void ConfigureOutboundConnections(Action<IConnectionBuilder> configure) => this.outboundConnectionBuilder.Add(configure);

        internal void ConfigureInboundConnectionBuilder(IConnectionBuilder builder) => this.inboundConnectionBuilder.Invoke(builder);
        public void ConfigureGatewayConnectionBuilder(IConnectionBuilder builder) => this.gatewayConnectionBuilder.Invoke(builder);
        public void ConfigureOutboundConnectionBuilder(IConnectionBuilder builder) => this.outboundConnectionBuilder.Invoke(builder);

        internal class ConnectionBuilderDelegates
        {
            private readonly List<Action<IConnectionBuilder>> configurationDelegates = new List<Action<IConnectionBuilder>>();

            public void Add(Action<IConnectionBuilder> configure)
                => this.configurationDelegates.Add(configure ?? throw new ArgumentNullException(nameof(configure)));

            public void Invoke(IConnectionBuilder builder)
            {
                foreach (var configureDelegate in this.configurationDelegates)
                {
                    configureDelegate(builder);
                }
            }
        }
    }
}