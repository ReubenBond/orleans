using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Connections.Transport;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.TestingHost.UnixSocketTransport;

public static class UnixDomainSocketConnectionExtensions
{
    public static IListenerBuilder UseUnixDomainSockets(this IListenerBuilder listenerBuilder)
    {
        listenerBuilder.Services.AddSingletonNamedService<MessageTransportListener>(listenerBuilder.EndpointName, (sp, name) =>
            new UnixDomainSocketMessageTransportListener(
                name,
                sp.GetRequiredService<IOptionsMonitor<UnixDomainSocketMessageTransportListenerOptions>>(),
                sp.GetRequiredService<ILoggerFactory>()));
        return listenerBuilder;
    }

    public static IConnectorBuilder UseUnixDomainSockets(this IConnectorBuilder connectorBuilder)
    {
        connectorBuilder.Services.AddSingletonNamedService<MessageTransportConnector>(connectorBuilder.EndpointName, (sp, name) =>
            new UnixDomainSocketMessageTransportConnector(
                name,
                sp.GetRequiredService<ILoggerFactory>()));
        return connectorBuilder;
    }
}
