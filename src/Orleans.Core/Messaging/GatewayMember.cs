using System.Collections.Immutable;
using Orleans.Connections.Transport;
using Orleans.Runtime;

namespace Orleans.Messaging
{
    /// <summary>
    /// Describes a gateway.
    /// </summary>
    [GenerateSerializer, Immutable]
    public sealed class GatewayMember
    {
        public GatewayMember(SiloAddress siloAddress, ImmutableArray<EndpointInfo> endpoints)
        {
            SiloAddress = siloAddress;
            Endpoints = endpoints;
        }

        /// <summary>
        /// Gets the identity of this gateway.
        /// </summary>
        [Id(0)]
        public SiloAddress SiloAddress { get; }

        /// <summary>
        /// Gets the endpoint information for this gateway.
        /// </summary>
        [Id(1)]
        public ImmutableArray<EndpointInfo> Endpoints { get; }

        /// <inheritdoc/>
        public override string ToString() => $"GatewayMember(SiloAddress={SiloAddress}, Endpoints={string.Join(", ", Endpoints)})";
    }   
}
