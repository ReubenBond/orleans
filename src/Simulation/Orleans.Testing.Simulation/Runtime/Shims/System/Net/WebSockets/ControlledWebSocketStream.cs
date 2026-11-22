using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Net.WebSockets;

/// <summary>
/// Controlled <see cref="WebSocketStream"/> factories which may make the stream own its <see cref="WebSocket"/>. A
/// stream which owns its socket closes it when disposed, bounding the closing handshake with a
/// <see cref="CancellationTokenSource"/> whose timer is real, so the close would be cancelled by wall-clock time. Each
/// factory builds the stream with the BCL's validation and returns it when its close needs no timer: it does not own
/// the socket, or its close timeout is zero (an already-cancelled token) or infinite. Otherwise it throws
/// <see cref="SimulationApiException"/>.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "Mirrors WebSocketStream, as every controlled shim mirrors its framework type's name.")]
public static class ControlledWebSocketStream
{
    private const string CreateApi = "System.Net.WebSockets.WebSocketStream.Create";

    /// <summary>Controlled <c>WebSocketStream.Create(WebSocket, WebSocketMessageType, bool)</c>.</summary>
    /// <param name="webSocket">The socket the stream wraps.</param>
    /// <param name="writeMessageType">The type of the messages the stream writes.</param>
    /// <param name="ownsWebSocket">Whether disposing the stream closes and disposes the socket.</param>
    /// <returns>The stream.</returns>
    public static WebSocketStream Create(WebSocket webSocket, WebSocketMessageType writeMessageType, bool ownsWebSocket)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CreateApi);
        WebSocketStream stream = WebSocketStream.Create(webSocket, writeMessageType, ownsWebSocket);
        if (ownsWebSocket)
        {
            // The BCL gives an owning stream a 16-second close timeout.
            throw Rejection();
        }

        return stream;
    }

    /// <summary>Controlled <c>WebSocketStream.Create(WebSocket, WebSocketMessageType, TimeSpan)</c>.</summary>
    /// <param name="webSocket">The socket the stream wraps.</param>
    /// <param name="writeMessageType">The type of the messages the stream writes.</param>
    /// <param name="closeTimeout">How long disposing the stream waits for the socket to close gracefully.</param>
    /// <returns>The stream, which owns the socket.</returns>
    public static WebSocketStream Create(WebSocket webSocket, WebSocketMessageType writeMessageType, TimeSpan closeTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CreateApi);
        WebSocketStream stream = WebSocketStream.Create(webSocket, writeMessageType, closeTimeout);
        if (closeTimeout != TimeSpan.Zero && closeTimeout != Timeout.InfiniteTimeSpan)
        {
            throw Rejection();
        }

        return stream;
    }

    private static SimulationApiException Rejection() =>
        new(
            SimulationApiCategory.Timer,
            CreateApi,
            "a WebSocketStream which owns its WebSocket bounds the closing handshake on disposal with a real timer, so the " +
            "close would be cancelled by wall-clock time. Pass closeTimeout: Timeout.InfiniteTimeSpan, or create the stream " +
            "without ownership of the socket, and close the socket yourself before disposing the stream, with a token " +
            "cancelled after a virtual delay.");
}
