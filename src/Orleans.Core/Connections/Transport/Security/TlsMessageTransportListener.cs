#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Connections.Transport.Security;

/// <summary>
/// Message transport listener which configures transports for TLS.
/// </summary>
public class TlsMessageTransportListener : MessageTransportListener
{
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions;
    private readonly MessageTransportListener _innerListener;
    private readonly ILogger _logger;

    [SetsRequiredMembers]
    public TlsMessageTransportListener(string endpointName, MessageTransportListener innerListener, IOptionsMonitor<TlsOptions> tlsOptions, ILoggerFactory loggerFactory)
    {
        EndpointName = endpointName;
        _tlsOptions = tlsOptions;
        _innerListener = innerListener;
        _logger = loggerFactory.CreateLogger<ServerTlsMessageTransport>();
    }

    /// <inheritdoc/>
    public override IFeatureCollection Features => _innerListener.Features;

    /// <inheritdoc/>
    public override async ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var innerTransport = await _innerListener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        if (innerTransport is null)
        {
            return null;
        }

        var transport = new ServerTlsMessageTransport(innerTransport, _tlsOptions.Get(EndpointName), _logger);
        transport.Start();
        return transport;
    }

    /// <inheritdoc/>
    public override async ValueTask<EndPointInfo> BindAsync(CancellationToken cancellationToken = default)
    {
        var innerInfo = await _innerListener.BindAsync(cancellationToken);

        // If there is any other information we want to add to the endpoint info we can do it here.
        return innerInfo;
    }

    /// <inheritdoc/>
    public override ValueTask UnbindAsync(CancellationToken cancellationToken = default) => _innerListener.UnbindAsync(cancellationToken);
}
