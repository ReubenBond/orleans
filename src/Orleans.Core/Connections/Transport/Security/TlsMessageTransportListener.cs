#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Connections.Transport.Sockets;

namespace Orleans.Connections.Transport.Security;

public class TlsMessageTransportListener : TcpMessageTransportListener
{
    private readonly IOptionsMonitor<TlsOptions> _options;

    [SetsRequiredMembers]
    public TlsMessageTransportListener(TransportListenerOptions listenOptions, IOptionsMonitor<TlsOptions> options, ILoggerFactory loggerFactory) : base(listenOptions, loggerFactory)
    {
        _options = options;
    }

    public override async ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var innerTransport = await base.AcceptAsync(cancellationToken).ConfigureAwait(false);
        if (innerTransport is null)
        {
            return null;
        }

        var transport = new ServerTlsMessageTransport(innerTransport, _options.CurrentValue, Logger);
        transport.Start();
        return transport;
    }
}
