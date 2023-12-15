#nullable enable
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Connections.Transport;

namespace Orleans.Runtime.Messaging;

internal abstract class ConnectionFactory
{
    private readonly MessageTransportConnector _transportConnector;

    protected ConnectionFactory(MessageTransportConnector transportConnector)
    {
        _transportConnector = transportConnector;
    }

    protected abstract Connection CreateConnection(SiloAddress address, MessageTransport context);

    public virtual async ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
    {
        // Connect to the endpoint.
        var transport = await _transportConnector.CreateAsync(GetEndPoint(address), cancellationToken);

        // Create a connection object to represent the connection.
        var connection = CreateConnection(address, transport);
        return connection;
    }

    protected abstract EndPoint GetEndPoint(SiloAddress address);
}
