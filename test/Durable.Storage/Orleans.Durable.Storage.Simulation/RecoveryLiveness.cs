using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// Bounds a <see cref="Ledger"/>'s recovery once a simulation's faults heal (ledger-protocol.md §8.4, §8.6).
/// </summary>
/// <remarks>
/// <para>
/// Once every replica runs, is connected, and loses no message, each replica answers a request within one round trip
/// <c>R</c>: the request's and the answer's network delays and the node's disk, which a seal meets twice, for the writer
/// writes it waits for and then for its own record.
/// <see cref="Ledger.RecoverAsync(CancellationToken)"/> needs only
/// <c>max(Q, N − Q + 1)</c> replicas and every replica answers, so it repeats nothing, and each of its steps is sent at
/// most one round trip after the step it waits for:
/// </para>
/// <list type="number">
/// <item><description>the seals, whose first <c>N − Q + 1</c> answers start the reads: one round trip;</description></item>
/// <item><description>
/// the reads of the first <c>K</c> positions from <c>c</c>, and the second read of a position whose first read the
/// recovery sent before that replica's seal answer arrived: two round trips;
/// </description></item>
/// <item><description>
/// the reads of the positions the window admits as earlier ones are decided: recovery reads at most the <c>2K + 1</c>
/// positions from <c>c</c> to <c>c + 2K</c>, <c>K</c> at a time, so <c>⌈(2K + 1) / K⌉ − 1</c> further waves, each sent
/// once all seals have answered, of one round trip each;
/// </description></item>
/// <item><description>
/// the recovery writes of the last position found, which end once <c>Q</c> replicas hold it: one round trip.
/// </description></item>
/// </list>
/// <para>
/// The pending recovery writes of a simulation's small values stay far below
/// <see cref="LedgerQuorum.RecoveryByteBudget"/>, so only the window limits the reads.
/// </para>
/// <para>
/// A recovery which was already running when the faults healed may still have a replica operation in flight which the
/// network lost, or one waiting out its backoff. The first fails at the transport's deadline, after which Polly waits
/// at most <see cref="ReplicaRetry.MaximumDelay"/> before repeating it (exponential from
/// <see cref="ReplicaRetry.BaseDelay"/>, with jitter, capped at the maximum), and the second repeats within that delay
/// too, answering one round trip later. From the healing, such a recovery takes at most the deadline, the maximum delay,
/// and one round trip more than a recovery which starts then.
/// </para>
/// </remarks>
public static class RecoveryLiveness
{
    /// <summary>
    /// Returns the longest a healthy replica on <paramref name="environment"/>'s network takes to answer a request: a
    /// network delay each way and two disk writes, each rounded up to the whole milliseconds a timer waits.
    /// </summary>
    /// <param name="environment">The simulation, whose network sets the delays.</param>
    /// <param name="diskLatency">The longest a node's disk takes to complete a write.</param>
    /// <returns>The round trip.</returns>
    public static TimeSpan RoundTrip(SimulationEnvironment environment, TimeSpan diskLatency)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var network = environment.Network;
        return 2 * SimulationEnvironment.WholeMilliseconds(network.BaseMessageDelay + network.MaxJitter)
            + 2 * SimulationEnvironment.WholeMilliseconds(diskLatency);
    }

    /// <summary>
    /// Returns the longest a recovery which starts after the faults heal takes.
    /// </summary>
    /// <param name="window">The ledger's window <c>K</c>, which bounds the positions the recovery reads.</param>
    /// <param name="roundTrip">The longest a healthy replica takes to answer a request, including its disk twice.</param>
    /// <returns>The bound.</returns>
    public static TimeSpan Bound(int window, TimeSpan roundTrip)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        var waves = (2 * window + 1 + window - 1) / window;
        return roundTrip * (1 + 2 + (waves - 1) + 1);
    }

    /// <summary>
    /// Returns the longest a recovery which was running when the faults healed takes from the healing.
    /// </summary>
    /// <param name="window">The ledger's window <c>K</c>, which bounds the positions the recovery reads.</param>
    /// <param name="roundTrip">The longest a healthy replica takes to answer a request, including its disk twice.</param>
    /// <param name="deadline">How long a replica request waits for its answer.</param>
    /// <returns>The bound.</returns>
    public static TimeSpan BoundFromHealing(int window, TimeSpan roundTrip, TimeSpan deadline) =>
        deadline + ReplicaRetry.MaximumDelay + roundTrip + Bound(window, roundTrip);
}
