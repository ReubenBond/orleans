using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Net.WebSockets;

/// <summary>
/// Rejection of the <see cref="global::System.Net.WebSockets.WebSocket"/> factories over a stream:
/// <c>CreateFromStream</c> (both overloads) and <c>CreateClientWebSocket</c>. They run the framework's WebSocket protocol
/// over a stream the caller supplies, such as a simulated connection, and that implementation sends keep-alive pings from
/// a real timer on the thread pool and bounds the closing handshake with a real-time timeout, so its traffic on the stream
/// depends on wall-clock time and runs outside the simulation's coordinator; the rewriter injects this rejection before
/// each call site instead.
/// </summary>
public static class ControlledWebSocket
{
    /// <summary>Rejection injected before a <c>WebSocket</c> factory over a stream.</summary>
    /// <param name="apiName">The rejected API, supplied by the rewriter.</param>
    public static void RejectUnsupported(string apiName)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        throw new SimulationApiException(
            SimulationApiCategory.Timer,
            apiName,
            "The framework's WebSocket over a stream sends keep-alive pings from a real timer on the thread pool and " +
            "times out its closing handshake in real time, outside the coordinator; model the connection with the " +
            "system under test's own message transport.");
    }
}
