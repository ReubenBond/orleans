using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Orleans.Networking.Transport;

namespace Orleans.Runtime.Messaging;

public interface IEndpointConfigurationProvider
{
    IEnumerable<EndpointInfo> GetEndpoints(SiloAddress siloAddress);
}

internal sealed class GatewayEndpointConfigurationProvider : IEndpointConfigurationProvider
{
    public IEnumerable<EndpointInfo> GetEndpoints(SiloAddress siloAddress)
    {
        // Find all matching gateways from the gateway list provider and return an EndpointInfo for each.
        yield return new EndpointInfo
        {
            Endpoint = siloAddress.Endpoint,
            EndpointName = "gateway",
            TransportName = "tcp",
            Configuration = new ConfigurationBuilder().Build()
        };
    }
}
