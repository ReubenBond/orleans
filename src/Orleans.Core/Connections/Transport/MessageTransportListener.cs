#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Connections.Transport;

/// <summary>
/// Represents a message transport listener, which provides active message transports.
/// </summary>
public abstract class MessageTransportListener : IAsyncDisposable
{
    /// <summary>
    /// Gets the name of the endpoint.
    /// </summary>
    public required string EndpointName { get; init; }

    /// <summary>
    /// Gets the collection of features available on the listener.
    /// </summary>
    public abstract IFeatureCollection Features { get; }

    /// <summary>
    /// Binds to the configured endpoint and begins listening for incoming connections.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bound endpoint configuration.</returns>
    public abstract ValueTask<EndPointInfo> BindAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts an incoming connection.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The message transport, or <see langword="null"/> if the listener has been stopped.</returns>
    public abstract ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Unbinds from the configured endpoint.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    public abstract ValueTask UnbindAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }
}
