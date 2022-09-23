#nullable enable

using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Networking.Transport.Sockets;

namespace Orleans.Networking.Transport.Security;

public class TlsMessageTransportFactory : TcpMessageTransportFactory
{
    private readonly string _name;
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions;

    public TlsMessageTransportFactory(string name, IOptionsMonitor<TlsOptions> tlsOptions, ILoggerFactory loggerFactory) : base(loggerFactory)
    {
        _name = name;
        _tlsOptions = tlsOptions;
    }

    public override async ValueTask<MessageTransport> CreateAsync(EndPoint endpointInfo, CancellationToken cancellationToken = default)
    {
        var innerTransport = await base.CreateAsync(endpointInfo, cancellationToken);
        var tlsOptions = _tlsOptions.Get(_name);
        var transport = new ClientTlsMessageTransport(innerTransport, tlsOptions, Logger);
        transport.Start();
        return transport;
    }
}
