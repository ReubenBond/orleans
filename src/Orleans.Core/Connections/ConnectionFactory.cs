#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Connections.Transport;

namespace Orleans.Runtime.Messaging;

internal abstract class ConnectionFactory
{
    private readonly Dictionary<string, MessageTransportConnector> _transportConnectors;

    protected ConnectionFactory(
        IEnumerable<MessageTransportConnector> transportConnectors)
    {
        _transportConnectors = GetConnectors(transportConnectors)
            .ToDictionary(
                static connector => connector.EndpointName,
                static connector => connector);

        static IEnumerable<MessageTransportConnector> GetConnectors(IEnumerable<MessageTransportConnector> registered)
        {
            // Filter out duplicates and non-valid connectors
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var connector in registered/*.Reverse()*/)
            {
                if (!connector.IsValid) continue;
                if (!seen.Add(connector.EndpointName)) continue;
                yield return connector;
            }
        }
    }

    protected abstract Connection CreateConnection(SiloAddress address, MessageTransport context);

    public virtual async ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
    {
        List<Exception>? exceptions = null;
        var endpoints = new List<EndpointInfo>();
        await foreach (var endpointInfo in GetEndpointInfo(address, cancellationToken))
        {
            endpoints.Add(endpointInfo);
            if (!_transportConnectors.TryGetValue(endpointInfo.Name, out var connector) || !connector.IsValid)
            {
                continue;
            }

            try
            {
                // Connect to the endpoint.
                var transport = await connector.CreateAsync(endpointInfo, cancellationToken);

                // Create a connection object to represent the connection.
                var connection = CreateConnection(address, transport);
                return connection;
            }
            catch (Exception exception)
            {
                (exceptions ??= new()).Add(exception);
            }
        }

        if (exceptions is null or { Count: 0 })
        {
            if (endpoints.Count > 0)
            {
                throw new KeyNotFoundException($"No suitable connector found for peer {address} with endpoints {string.Join(", ", endpoints.Select(ep => ep.Name))}");
            }

            throw new KeyNotFoundException($"Could not find an endpoint for peer {address}");
        }
        else
        {
            throw new AggregateException($"Unable to connect to peer {address} with endpoints {string.Join(", ", endpoints.Select(ep => ep.Name))}. See {nameof(AggregateException.InnerExceptions)} for details.", exceptions);
        }
    }

    protected abstract IAsyncEnumerable<EndpointInfo> GetEndpointInfo(SiloAddress siloAddress, CancellationToken cancellationToken = default);
}
