#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Connections.Transport.Security;

namespace Orleans.Connections.Transport;

/// <summary>
/// Middleware which adds TLS to all <see cref="MessageTransport"/> instances created by a <see cref="MessageTransportConnector"/>.
/// </summary>
public sealed class TlsMessageTransportConnectorMiddleware : IMessageTransportConnectorMiddleware
{
    private readonly string _endpointName;
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions;
    private readonly ILoggerFactory _loggerFactory;

    public TlsMessageTransportConnectorMiddleware(string endpointName, IOptionsMonitor<TlsOptions> tlsOptions, ILoggerFactory loggerFactory)
    {
        _endpointName = endpointName;
        _tlsOptions = tlsOptions;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc/>
    public MessageTransportConnector Apply(MessageTransportConnector transport) => new TlsMessageTransportConnector(_endpointName, transport, _tlsOptions, _loggerFactory);
}

/// <summary>
/// Middleware which adds TLS to all <see cref="MessageTransport"/> instances created by a <see cref="MessageTransportListener"/>.
/// </summary>
public sealed class TlsMessageTransportListenerMiddleware : IMessageTransportListenerMiddleware
{
    private readonly string _endpointName;
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions;
    private readonly ILoggerFactory _loggerFactory;

    public TlsMessageTransportListenerMiddleware(string endpointName, IOptionsMonitor<TlsOptions> tlsOptions, ILoggerFactory loggerFactory)
    {
        _endpointName = endpointName;
        _tlsOptions = tlsOptions;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc/>
    public MessageTransportListener Apply(MessageTransportListener input) => new TlsMessageTransportListener(_endpointName, input, _tlsOptions, _loggerFactory);
}
