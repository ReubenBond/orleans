#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Networking.Transport.Sockets;

namespace Orleans.Networking.Transport.Security;

public class TlsMessageTransportFactory : TcpMessageTransportFactory
{
    private readonly TlsOptions _options;

    public TlsMessageTransportFactory(string transportName, IOptionsMonitor<TlsOptions> options, ILoggerFactory loggerFactory) : base(loggerFactory)
    {
        _options = options.Get(transportName);
    }

    public override async ValueTask<MessageTransport> CreateAsync(EndpointInfo endpointInfo, CancellationToken cancellationToken = default)
    {
        var innerTransport = await base.CreateAsync(endpointInfo, cancellationToken);
        var transport = new ClientTlsMessageTransport(innerTransport, _options, Logger);
        transport.Start();
        return transport;
    }
}
