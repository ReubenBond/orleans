#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
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
        _transportConnectors = transportConnectors.ToDictionary(
            static connector => connector.Features.Get<IEndPointNameFeature>()?.EndPointName ?? throw new InvalidOperationException($"{nameof(MessageTransportConnector)} {connector} is missing required feature {nameof(IEndPointNameFeature)}"),
            static connector => connector);
    }

    protected abstract Connection CreateConnection(SiloAddress address, MessageTransport context);

    public virtual async ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
    {
        // Get the collection of endpoints for this peer
        // For each, try to get the connector with a matching name.
        // If a connector is found, use that connector to connect.
        // Repeat until success.
        // If no connectors are found, throw.

        List<Exception>? exceptions = null;
        var endPoints = new List<EndPointInfo>();
        await foreach (var endPointInfo in GetEndpointInfo(address, cancellationToken))
        {
            endPoints.Add(endPointInfo);
            if (!_transportConnectors.TryGetValue(endPointInfo.Name, out var connector))
            {
                continue;
            }

            try
            {
                // Connect to the endpoint.
                var transport = await connector.CreateAsync(endPointInfo, cancellationToken);

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
            if (endPoints.Count > 0)
            {
                throw new KeyNotFoundException($"No suitable connector found for peer {address} with endpoints {string.Join(", ", endPoints.Select(ep => ep.Name))}");
            }

            throw new KeyNotFoundException($"Could not find an endpoint for peer {address}");
        }
        else
        {
            throw new AggregateException($"Unable to connect to peer {address} with endpoints {string.Join(", ", endPoints.Select(ep => ep.Name))}. See {nameof(AggregateException.InnerExceptions)} for details.", exceptions);
        }
    }

    protected abstract IAsyncEnumerable<EndPointInfo> GetEndpointInfo(SiloAddress siloAddress, CancellationToken cancellationToken = default);
}
