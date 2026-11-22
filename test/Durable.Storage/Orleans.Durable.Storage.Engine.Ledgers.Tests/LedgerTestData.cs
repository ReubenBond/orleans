using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Builds the <see cref="Ledger"/>s the tests run, and carries a caller's contiguous tail into one as an operation of
/// the replica contract carries it: the ledger raises its known contiguous tail to the tail, and the operation carries
/// the greater of the two (LP-I11).
/// </summary>
internal static class LedgerTestData
{
    /// <summary>
    /// Builds the <see cref="Ledger"/> of <see cref="LedgerReplicaTestData.Ledger"/> over <paramref name="replicas"/>, in
    /// order, which it owns, with quorum <paramref name="quorum"/>, on <paramref name="timeProvider"/> or the system clock.
    /// </summary>
    internal static Ledger Create(
        IEnumerable<ILedgerReplica> replicas,
        int quorum,
        LedgerReplicaOptions options,
        TimeProvider? timeProvider = null) =>
        OnNodes(LedgerReplicaTestData.Ledger, replicas, quorum, options, timeProvider ?? TimeProvider.System);

    /// <summary>
    /// Builds the <see cref="Ledger"/> of <paramref name="ledger"/> over <paramref name="replicas"/>, in order, which it owns,
    /// as a ledger on nodes of fresh identities, one per replica, with quorum <paramref name="quorum"/>.
    /// </summary>
    internal static Ledger OnNodes(
        LedgerId ledger,
        IEnumerable<ILedgerReplica> replicas,
        int quorum,
        LedgerReplicaOptions options,
        TimeProvider timeProvider,
        Action<RecoveryStep>? recoveryObserver = null)
    {
        ILedgerReplica[] opened = [.. replicas];
        var nodes = new LedgerLocation.Nodes([.. opened.Select(static _ => NodeId.New())]);
        return new(ledger, LedgerQuorum.ForNodes(ledger, nodes.Replicas, opened, quorum, options, timeProvider, localNode: null, recoveryObserver), nodes);
    }

    /// <summary>
    /// Writes <paramref name="payload"/> at <paramref name="slot"/> as the writer, carrying the greater of
    /// <paramref name="contiguousTail"/> and the known contiguous tail, as a writer whose evidence raised it does. A write
    /// the ledger rejects as an argument raises nothing.
    /// </summary>
    internal static ValueTask<LedgerWriteOutcome> WriteAsync(
        this Ledger ledger,
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        CancellationToken cancellationToken = default)
    {
        LedgerReplicaOperations.ValidateSlot(slot);
        LedgerReplicaOperations.ValidateBatch(payload);
        _ = ledger.RaiseKnownContiguousTail(contiguousTail);
        return ledger.WriteAsync(slot, payload, cancellationToken);
    }

    /// <summary>
    /// Writes <paramref name="payload"/> at slot <paramref name="slot"/> as the writer, carrying the known contiguous tail.
    /// </summary>
    internal static ValueTask<LedgerWriteOutcome> WriteAsync(this Ledger ledger, long slot, ArcBuffer payload, CancellationToken cancellationToken = default) =>
        ledger.WriteAsync(new Slot(slot), payload, cancellationToken);

    /// <summary>
    /// Reads slot <paramref name="slot"/>, carrying the known contiguous tail.
    /// </summary>
    internal static ValueTask<ReplicaRead> ReadAsync(this Ledger ledger, long slot, CancellationToken cancellationToken = default) =>
        ledger.ReadAsync(new Slot(slot), cancellationToken);

    /// <summary>
    /// Checks the seal, carrying the greater of <paramref name="contiguousTail"/> and the known contiguous tail.
    /// </summary>
    internal static ValueTask<LedgerTailRead> ReadTailAsync(this Ledger ledger, Slot contiguousTail, CancellationToken cancellationToken = default)
    {
        _ = ledger.RaiseKnownContiguousTail(contiguousTail);
        return ledger.ReadTailAsync(cancellationToken);
    }

    /// <summary>
    /// Recovers the ledger from the greater of <paramref name="contiguousTail"/> and the known contiguous tail.
    /// </summary>
    internal static ValueTask<RecoveryOutcome> RecoverAsync(this Ledger ledger, Slot contiguousTail, CancellationToken cancellationToken = default)
    {
        _ = ledger.RaiseKnownContiguousTail(contiguousTail);
        return ledger.RecoverAsync(cancellationToken);
    }

    /// <summary>
    /// Returns the batch at slot <paramref name="slot"/>, read carrying the known contiguous tail, or
    /// <see langword="null"/> when the ledger reports it missing.
    /// </summary>
    internal static async Task<byte[]?> ReadBytesAsync(Ledger ledger, long slot, CancellationToken cancellationToken)
    {
        var result = await ledger.ReadAsync(new Slot(slot), cancellationToken);
        if (result.Case != ReplicaReadCase.Batch)
        {
            return null;
        }

        using var batch = result.Batch;
        return batch.ToArray();
    }
}
