using System.Collections.Generic;
using System.Linq;
using Orleans.Networking.Transport;

namespace Orleans.Runtime.Messaging;

internal sealed class EndpointConfigurationProvider
{
    private readonly IEndpointConfigurationProvider[] _providers;
    public EndpointConfigurationProvider(IEnumerable<IEndpointConfigurationProvider> mappers)
    {
        _providers = mappers.ToArray();
    }

    public IEnumerable<EndpointInfo> GetEndpoints(SiloAddress siloAddress)
    {
        foreach (var provider in _providers)
        {
            foreach (var endpoint in provider.GetEndpoints(siloAddress))
            {
                yield return endpoint;
            }
        }
    }
}
