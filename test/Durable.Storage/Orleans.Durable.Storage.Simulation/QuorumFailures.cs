using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// The failures a quorum's operation may report under a simulation's faults: an <see cref="AggregateException"/> of its
/// replicas' failures when no decision reaches a quorum (ledger-replicas.md §6.4), each of which one of the faults explains.
/// </summary>
/// <remarks>
/// <para>
/// A replica's request which misses its deadline fails with <see cref="TimeoutException"/>, whether the network lost
/// it, the node was paused, isolated, or down, or the node crashed while serving it; and, where the simulation damages
/// media, a replica reports the damage (<see cref="LedgerReplicaCorruptException"/>, LS-I5). Any other failure of a replica
/// is a defect the scenario must report, rather than count as the quorum's unavailability. A <c>Dropped</c> answer is a
/// quorum's answer, not a failure, and the scenario's check of its answers reports it, since no scenario drops the ledger
/// its quorum uses (LP-I5).
/// </para>
/// <para>
/// A <see cref="Ledger.RecoverAsync(CancellationToken)"/> repeats a replica's missed deadline
/// until the replica answers, so a recovery admits no failure at all: it ends once <c>max(Q, N − Q + 1)</c> replicas
/// answer (ledger-protocol.md §6.5).
/// </para>
/// </remarks>
public static class QuorumFailures
{
    /// <summary>
    /// Returns whether every failure <paramref name="failure"/> carries is one the simulation's faults explain.
    /// </summary>
    /// <param name="failure">The quorum's failure.</param>
    /// <param name="mediaDamage">Whether the simulation damages media, so that a replica may report corruption.</param>
    /// <returns><see langword="true"/> when the scenario may count the failure as the quorum's unavailability.</returns>
    public static bool IsAdmitted(AggregateException failure, bool mediaDamage)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var replicaFailures = failure.Flatten().InnerExceptions;
        return replicaFailures.Count != 0 && replicaFailures.All(replicaFailure => replicaFailure switch
        {
            TimeoutException => true,
            LedgerReplicaCorruptException => mediaDamage,
            _ => false,
        });
    }
}
