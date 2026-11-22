using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Runtime.Shims;

/// <summary>
/// <para>
/// The deterministic services a simulation host provides to the BCL shims: virtual time, virtual
/// high-resolution timestamps and tick counts, the simulated processor count, deterministic random streams,
/// deterministic identity bytes for GUID generation and deterministic non-cryptographic bytes for controlled
/// <see cref="System.Security.Cryptography.RandomNumberGenerator"/> APIs. A host installs one environment
/// in each runtime before activation; the shims resolve it from the ambient
/// <see cref="SimulationExecutionContext"/> and dispatch to it, passing the currently-active
/// node identity so the environment can return per-node-isolated state.
/// </para>
/// <para>
/// Every method receives the active <see cref="SimulationNodeIdentity"/> (or <see langword="null"/>
/// for cluster-level execution not scoped to a node). Implementations must keep each node's mutable
/// random state isolated from every other node's, and must derive that state only from the
/// application/identity seed domains so consuming randomness here never perturbs the scheduler,
/// network, or fault-injection ("Buggify") domains.
/// </para>
/// </summary>
public interface ISimulationRuntimeEnvironment
{
    /// <summary>Gets the current virtual UTC instant for the given node.</summary>
    /// <param name="node">The active node identity, or <see langword="null"/> for cluster-level execution.</param>
    /// <returns>The deterministic current UTC instant.</returns>
    DateTimeOffset GetUtcNow(SimulationNodeIdentity? node);

    /// <summary>Gets the local time zone the given node observes (used for <c>DateTime.Now</c>/<c>Today</c>).</summary>
    /// <param name="node">The active node identity, or <see langword="null"/> for cluster-level execution.</param>
    /// <returns>The deterministic local time zone.</returns>
    TimeZoneInfo GetLocalTimeZone(SimulationNodeIdentity? node);

    /// <summary>
    /// Gets the simulated processor count the given node observes as <see cref="Environment.ProcessorCount"/>, and
    /// which sizes the work the BCL sizes by it (a <c>Parallel</c> loop's default degree of parallelism, a
    /// <c>Partitioner</c>'s range size). It is fixed for the life of the runtime and independent of the host, so a
    /// seed replays on any machine.
    /// </summary>
    /// <param name="node">The active node identity, or <see langword="null"/> for cluster-level execution.</param>
    /// <returns>The simulated processor count, which is positive.</returns>
    int GetProcessorCount(SimulationNodeIdentity? node);

    /// <summary>
    /// Gets the current virtual high-resolution timestamp (in units of <see cref="System.Diagnostics.Stopwatch.Frequency"/>)
    /// for the given node, as observed by <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>.
    /// </summary>
    /// <param name="node">The active node identity, or <see langword="null"/> for cluster-level execution.</param>
    /// <returns>The deterministic timestamp.</returns>
    long GetTimestamp(SimulationNodeIdentity? node);

    /// <summary>
    /// Gets the number of virtual milliseconds since the runtime's origin, as observed by
    /// <see cref="Environment.TickCount64"/>.
    /// </summary>
    /// <param name="node">The active node identity, or <see langword="null"/> for cluster-level execution.</param>
    /// <returns>The deterministic tick count in milliseconds.</returns>
    long GetTickCount64(SimulationNodeIdentity? node);

    /// <summary>
    /// Gets the node's stable, shared deterministic <see cref="Random"/> - the simulation equivalent of
    /// <see cref="System.Random.Shared"/>. The same instance is returned for the life of the simulation for a
    /// given node, so draws advance a single per-node stream; the instance is isolated per node.
    /// </summary>
    /// <param name="node">The active node identity, or <see langword="null"/> for cluster-level execution.</param>
    /// <returns>The node's shared deterministic random stream.</returns>
    System.Random GetSharedRandom(SimulationNodeIdentity? node);

    /// <summary>
    /// Creates a fresh deterministic <see cref="Random"/> for an unseeded <c>new Random()</c>
    /// construction on the given node. Each construction returns an independent instance seeded from a
    /// per-node monotonic construction counter, so the sequence is reproducible under a fixed schedule
    /// while distinct constructions never share mutable state.
    /// </summary>
    /// <param name="node">The active node identity, or <see langword="null"/> for cluster-level execution.</param>
    /// <returns>A new deterministic random stream.</returns>
    System.Random CreateUnseededRandom(SimulationNodeIdentity? node);

    /// <summary>
    /// Fills <paramref name="destination"/> with deterministic identity bytes drawn from the identity
    /// seed domain for the given node. Used by the deterministic GUID shims; independent of the random
    /// streams so consuming identity bytes never perturbs application randomness.
    /// </summary>
    /// <param name="node">The active node identity, or <see langword="null"/> for cluster-level execution.</param>
    /// <param name="destination">The buffer to fill.</param>
    void FillIdentityBytes(SimulationNodeIdentity? node, Span<byte> destination);

    /// <summary>
    /// Fills <paramref name="destination"/> with deterministic, non-cryptographic bytes for controlled
    /// <see cref="System.Security.Cryptography.RandomNumberGenerator"/> APIs.
    /// </summary>
    /// <param name="node">The active node identity, or <see langword="null"/> for cluster-level execution.</param>
    /// <param name="destination">The buffer to fill.</param>
    void FillCryptoRandomBytes(SimulationNodeIdentity? node, Span<byte> destination);
}
