using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Connections.Transport;

namespace Orleans.Runtime.Messaging;

internal abstract class ConnectionFactory
{
    private readonly IMessageTransportFactoryProvider[] _transportFactoryProviders;
    private readonly IOptionsMonitor<TransportFactoryOptions> _transportFactoryOptions;

    protected ConnectionFactory(
        IEnumerable<IMessageTransportFactoryProvider> transportFactoryProviders,
        IOptionsMonitor<TransportFactoryOptions> transportFactoryOptions)
    {
        _transportFactoryProviders = transportFactoryProviders.ToArray();
        _transportFactoryOptions = transportFactoryOptions;
    }

    protected abstract Connection CreateConnection(SiloAddress address, MessageTransport context);

    public virtual async ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
    {
        var endpoint = address.Endpoint;
        if (!TryGetTransportFactory(endpoint, out var transportFactory))
        {
            throw new KeyNotFoundException($"Could not find an endpoint for peer {address}");
        }

        // Connect to the endpoint.
        var transport = await transportFactory.CreateAsync(endpoint, cancellationToken);

        var options = _transportFactoryOptions.CurrentValue;
        foreach (var middleware in options.Middleware)
        {
            transport = middleware(transport);
        }

        // Create a connection object to represent the connection.
        var connection = CreateConnection(address, transport);
        return connection;
    }

    protected virtual bool TryGetTransportFactory(EndPoint endpoint, out MessageTransportFactory transportFactory)
    {
        foreach (var provider in _transportFactoryProviders)
        {
            if (provider.TryGetMessageTransportFactory(endpoint, out transportFactory))
            {
                return true;
            }
        }

        transportFactory = null;
        return false;
    }
}
