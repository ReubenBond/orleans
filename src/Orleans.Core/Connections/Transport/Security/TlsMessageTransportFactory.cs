#nullable enable

using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Connections.Transport.Security;

public class TlsMessageTransportFactory : MessageTransportFactory
{
    private readonly MessageTransportFactory _innerTransportFactory;
    private readonly ILogger<ClientTlsMessageTransport> _logger;
    private readonly string _name;
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions;

    public TlsMessageTransportFactory(string name, MessageTransportFactory innerTransportFactory, IOptionsMonitor<TlsOptions> tlsOptions, ILoggerFactory loggerFactory)
    {
        _innerTransportFactory = innerTransportFactory;
        _logger = loggerFactory.CreateLogger<ClientTlsMessageTransport>();
        _name = name;
        _tlsOptions = tlsOptions;
    }

    /// <inheritdoc/>
    public override async ValueTask<MessageTransport> CreateAsync(EndPoint endpointInfo, CancellationToken cancellationToken = default)
    {
        var innerTransport = await _innerTransportFactory.CreateAsync(endpointInfo, cancellationToken);
        var tlsOptions = _tlsOptions.Get(_name);
        var transport = new ClientTlsMessageTransport(innerTransport, tlsOptions, _logger);
        transport.Start();
        return transport;
    }

    /// <inheritdoc/>
    public override ValueTask DisposeAsync() => _innerTransportFactory.DisposeAsync();
}
