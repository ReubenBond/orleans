using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Builds the <see cref="LedgerQuorum"/>s the tests run, and calls one carrying a zero contiguous tail.
/// </summary>
internal static class LedgerQuorumTestData
{
    /// <summary>
    /// Builds the <see cref="LedgerQuorum"/> over <paramref name="replicas"/>, in order, which it owns, with quorum
    /// <paramref name="quorum"/>, on <paramref name="timeProvider"/> or the system clock, whose reads below the tail ask
    /// <paramref name="preferredReplica"/> first, when set, and repair it with the batches <paramref name="verifyBatch"/>
    /// accepts.
    /// </summary>
    internal static LedgerQuorum Create(
        IEnumerable<ILedgerReplica> replicas,
        int quorum,
        LedgerReplicaOptions options,
        TimeProvider? timeProvider = null,
        int? preferredReplica = null,
        Action<RecoveryStep>? recoveryObserver = null,
        Func<ArcBuffer, bool>? verifyBatch = null) =>
        new(replicas, quorum, options, timeProvider ?? TimeProvider.System, preferredReplica, recoveryObserver: recoveryObserver, verifyBatch: verifyBatch);

    /// <summary>
    /// Writes <paramref name="payload"/> at <paramref name="slot"/> as the writer, carrying a zero contiguous tail.
    /// </summary>
    internal static ValueTask<LedgerWriteOutcome> WriteAsync(this LedgerQuorum quorum, Slot slot, ArcBuffer payload, CancellationToken cancellationToken = default) =>
        quorum.WriteAsync(slot, payload, Slot.Zero, cancellationToken);

    /// <inheritdoc cref="WriteAsync(LedgerQuorum, Slot, ArcBuffer, CancellationToken)"/>
    internal static ValueTask<LedgerWriteOutcome> WriteAsync(this LedgerQuorum quorum, long slot, ArcBuffer payload, CancellationToken cancellationToken = default) =>
        quorum.WriteAsync(new Slot(slot), payload, Slot.Zero, cancellationToken);

    /// <summary>
    /// Reads slot <paramref name="slot"/>, carrying a zero contiguous tail.
    /// </summary>
    internal static ValueTask<ReplicaRead> ReadAsync(this LedgerQuorum quorum, long slot, CancellationToken cancellationToken = default) =>
        quorum.ReadAsync(new Slot(slot), Slot.Zero, cancellationToken);

    /// <inheritdoc cref="ReadAsync(LedgerQuorum, long, CancellationToken)"/>
    internal static ValueTask<ReplicaRead> ReadAsync(this LedgerQuorum quorum, Slot slot, CancellationToken cancellationToken = default) =>
        quorum.ReadAsync(slot, Slot.Zero, cancellationToken);

    /// <summary>
    /// Checks the seal, carrying a zero contiguous tail.
    /// </summary>
    internal static ValueTask<LedgerTailRead> CheckAsync(this LedgerQuorum quorum, CancellationToken cancellationToken = default) =>
        quorum.CheckAsync(Slot.Zero, cancellationToken);

    /// <summary>
    /// Recovers the ledger from a zero contiguous tail, returning its candidate end.
    /// </summary>
    internal static ValueTask<Slot> RecoverEndAsync(this LedgerQuorum quorum, CancellationToken cancellationToken = default) =>
        quorum.RecoverEndAsync(Slot.Zero, cancellationToken);

    /// <summary>
    /// Recovers the ledger from <paramref name="contiguousTail"/>, returning its candidate end.
    /// </summary>
    internal static async ValueTask<Slot> RecoverEndAsync(this LedgerQuorum quorum, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        (await quorum.RecoverAsync(contiguousTail, cancellationToken)).Recovery.End;

    extension(LedgerRecoveryOutcome outcome)
    {
        /// <summary>
        /// Gets the recovery the outcome holds, or throws when it holds <see cref="ReplicaWriteOutcome.Dropped"/>.
        /// </summary>
        internal LedgerRecovery Recovery =>
            outcome.Value is LedgerRecovery recovery ? recovery : throw new InvalidOperationException($"The recovery answered {outcome.Value}.");
    }
}
