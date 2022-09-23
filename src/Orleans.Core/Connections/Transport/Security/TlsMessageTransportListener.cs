#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Networking.Transport.Sockets;

namespace Orleans.Networking.Transport.Security;

public class TlsMessageTransportListener : TcpMessageTransportListener
{
    private readonly TlsOptions _options;

    [SetsRequiredMembers]
    public TlsMessageTransportListener(IPEndPoint localEndpoint, TlsOptions options, ILoggerFactory loggerFactory) : base(localEndpoint, loggerFactory)
    {
        _options = options;
    }

    public override async ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var innerTransport = await base.AcceptAsync(cancellationToken);
        if (innerTransport is null)
        {
            return null;
        }

        var transport = new ServerTlsMessageTransport(innerTransport, _options, Logger);
        transport.Start();
        return transport;
    }
}
