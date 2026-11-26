using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Provides durable, create-once registers over a numbered address space with a positionless seal: one ledger replica
/// (ledger-replicas.md §2.1). The ledger protocol runs over a ledger's replicas (ledger-protocol.md).
/// </summary>
/// <remarks>
/// <para>
/// "The LogDrive is an address space of registers with a random read/write interface."
/// See <c>research/papers/logdrive/paper.md</c>, section 3.1.
/// </para>
/// <para>
/// A caller must not write different values to the same address. The caller retains ownership of
/// write payloads until the returned operation completes; a provider which uses a payload after its operation
/// completes takes its own lease on it (<see cref="ArcBuffer.Retain"/> or <see cref="ArcBuffer.Slice(int, int)"/>).
/// The caller owns and must dispose the batch a <see cref="ReplicaRead"/> holds.
/// </para>
/// <para>
/// Every operation carries the caller's known contiguous tail, <see cref="Slot.Zero"/> when it knows none:
/// every address below it holds DATA on <c>Q</c> of the ledger's replicas. The replica retains the greatest it receives
/// (LR-I6).
/// </para>
/// <para>
/// Every answer reflects durable state (LR-I4, conventions.md §13): <see cref="ReplicaWriteOutcome.Written"/> once DATA
/// durably occupies the address, a batch and <see cref="ReplicaRead.Missing"/> from durable, published state,
/// <see cref="ReplicaWriteOutcome.Sealed"/> and a sealed <see cref="LedgerReplicaTail"/> once the seal is durable, and
/// <c>Dropped</c> once the drop is. An answer which reflects a create, seal, or drop in flight waits for its durable
/// outcome and answers from it. <c>Backpressure</c> comes before the replica retains or writes anything, so it has no
/// effect (outcomes.md §1).
/// </para>
/// <para>
/// The contract has no create and no drop: a ledger replica's node creates it with the first write or seal which
/// reaches it unless the node dropped the ledger, and drops it at the layer above's direction (LR-I3, LR-I7).
/// </para>
/// <para>
/// A replica may be disposed while operations are in flight. Each of them completes or fails and releases its buffers,
/// and an operation which starts after disposal fails with <see cref="ObjectDisposedException"/> (ledger-replicas.md §6.3).
/// </para>
/// </remarks>
public interface ILedgerReplica : IAsyncDisposable
{
    /// <summary>
    /// Durably writes DATA at <paramref name="slot"/>.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="payload">The non-empty DATA value.</param>
    /// <param name="contiguousTail">The caller's known contiguous tail, which the replica retains (LR-I6).</param>
    /// <param name="recovery">
    /// <see langword="false"/> for a writer write, which only the ledger's writer sends and which a sealed replica
    /// refuses; <see langword="true"/> for a recovery write, which copies DATA a replica reported for the address, and
    /// which a sealed replica accepts (ledger-replicas.md §3.2, LR-I5).
    /// </param>
    /// <param name="cancellationToken">Stops the caller's wait; a write already sent may still land.</param>
    /// <returns>
    /// <see cref="ReplicaWriteOutcome.Written"/> once DATA durably occupies the address, this write's or an identical
    /// earlier one (LR-I4); <see cref="ReplicaWriteOutcome.Sealed"/> for a writer write which met a durable seal and stored
    /// nothing (LR-I8), which a recovery write never answers; <see cref="ReplicaWriteOutcome.Dropped"/> once the node's
    /// drop of the ledger is durable (LR-I3); or <see cref="ReplicaWriteOutcome.Backpressure"/>, before the write had any
    /// effect.
    /// </returns>
    ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        bool recovery,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an address, classifying it as missing or DATA.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="contiguousTail">The caller's known contiguous tail, which the replica retains (LR-I6).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The owned batch, <see cref="ReplicaRead.Missing"/>, <see cref="ReplicaWriteOutcome.Dropped"/>, or
    /// <see cref="ReplicaWriteOutcome.Backpressure"/>. Below the known contiguous tail an address is decided; at or above it,
    /// DATA asserts only that a write was attempted (ledger-protocol.md §8.3).
    /// </returns>
    ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        Slot contiguousTail,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads bytes <c>[offset, offset + length)</c> of the DATA at an address, or reports it missing: a byte-range read.
    /// </summary>
    /// <remarks>
    /// Requires <paramref name="offset"/> ≥ 0 and <paramref name="length"/> > 0, which it checks with
    /// <see cref="ArgumentOutOfRangeException"/>; a range which extends past the DATA value answers
    /// <see cref="ReplicaRead.OutsideBatch"/>. A provider which cannot read fewer bytes than a whole value reads it with
    /// <see cref="LedgerReplicaOperations.ReadRangeOfValueAsync"/>.
    /// </remarks>
    ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        int offset,
        int length,
        Slot contiguousTail,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a value indicating whether this replica provides a native batched <see cref="ReadRangeAsync"/>
    /// implementation rather than falling back to sequential point-wise <see cref="ReadAsync(Slot, Slot, CancellationToken)"/> calls.
    /// </summary>
    bool SupportsReadRange => false;

    /// <summary>
    /// Reads up to <paramref name="maxSlots"/> consecutive slots starting at <paramref name="from"/> below
    /// <paramref name="below"/> whose length-prefixed payloads fit within <paramref name="maxBytes"/>, stopping at the
    /// first missing, corrupt, or oversized slot after <paramref name="from"/> (ledger-replicas.md §2.1).
    /// </summary>
    /// <remarks>
    /// Providers without a native batched range path inherit the point-wise fallback in
    /// <see cref="LedgerReplicaOperations.ReadRangePointWiseAsync"/>.
    /// </remarks>
    ValueTask<ReplicaRangeRead> ReadRangeAsync(
        Slot from,
        Slot below,
        Slot contiguousTail,
        int maxSlots,
        int maxBytes,
        CancellationToken cancellationToken = default) =>
        LedgerReplicaOperations.ReadRangePointWiseAsync(this, from, below, contiguousTail, maxSlots, maxBytes, cancellationToken);

    /// <summary>
    /// Gets a value indicating whether this replica provides a native batched <see cref="ReadSlotsAsync"/>
    /// implementation rather than falling back to sequential point-wise <see cref="ReadAsync(Slot, Slot, CancellationToken)"/> calls.
    /// </summary>
    bool SupportsReadSlots => false;

    /// <summary>
    /// Reads the strictly ascending list of decided <paramref name="slots"/> whose framed payloads fit within
    /// <paramref name="maxBytes"/>, skipping missing, faulty, or oversized slots so present slots return in one call
    /// (ledger-replicas.md §2.1).
    /// </summary>
    /// <remarks>
    /// Providers without a native batched scattered-slot path inherit the point-wise fallback in
    /// <see cref="LedgerReplicaOperations.ReadSlotsPointWiseAsync"/>.
    /// </remarks>
    ValueTask<ReplicaSlotsRead> ReadSlotsAsync(
        ReadOnlyMemory<Slot> slots,
        Slot contiguousTail,
        int maxBytes,
        CancellationToken cancellationToken = default) =>
        LedgerReplicaOperations.ReadSlotsPointWiseAsync(this, slots, contiguousTail, maxBytes, cancellationToken);

    /// <summary>
    /// Reads the replica's contiguous tail and whether it is sealed, sealing it first when <paramref name="seal"/> is set.
    /// </summary>
    /// <param name="contiguousTail">The caller's known contiguous tail, which the replica retains (LR-I6).</param>
    /// <param name="seal">
    /// Whether to seal first: afterwards the replicas the seal reached answer every writer write
    /// <see cref="ReplicaWriteOutcome.Sealed"/> (LR-I8, LP-I6). Idempotent.
    /// </param>
    /// <param name="cancellationToken">Stops the caller's wait; a seal already sent may still take effect.</param>
    /// <returns>
    /// The replica's retained contiguous tail after the call and its seal, which a sealing read answers only once the
    /// seal and every writer write the replica accepted before it are durable and published, so a read served after the
    /// answer sees each of those writes (LR-I8); <see cref="ReplicaWriteOutcome.Dropped"/>; or
    /// <see cref="ReplicaWriteOutcome.Backpressure"/>.
    /// </returns>
    ValueTask<ReplicaTailRead> ReadTailAsync(
        Slot contiguousTail,
        bool seal,
        CancellationToken cancellationToken = default);
}
