#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Connections.Transport.Security;

/// <summary>
/// Message transport factory which configures transports for TLS.
/// </summary>
public class TlsMessageTransportConnector : MessageTransportConnector
{
    private readonly string _name;
    private readonly MessageTransportConnector _innerConnector;
    private readonly ILogger<ClientTlsMessageTransport> _logger;
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions;

    public TlsMessageTransportConnector(string name, MessageTransportConnector innerTransportFactory, IOptionsMonitor<TlsOptions> tlsOptions, ILoggerFactory loggerFactory)
    {
        _innerConnector = innerTransportFactory;
        _logger = loggerFactory.CreateLogger<ClientTlsMessageTransport>();
        _name = name;
        _tlsOptions = tlsOptions;
    }

    /// <inheritdoc/>
    public override IFeatureCollection Features => _innerConnector.Features;

    /// <inheritdoc/>
    public override async ValueTask<MessageTransport> CreateAsync(EndPointInfo endpointInfo, CancellationToken cancellationToken = default)
    {
        var innerTransport = await _innerConnector.CreateAsync(endpointInfo, cancellationToken);
        var tlsOptions = _tlsOptions.Get(_name);
        var transport = new ClientTlsMessageTransport(innerTransport, tlsOptions, _logger);
        transport.Start();
        return transport;
    }

    /// <inheritdoc/>
    public override ValueTask DisposeAsync() => _innerConnector.DisposeAsync();
}
