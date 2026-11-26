using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The storage of one ledger below its <see cref="Ledger"/> (ledger-protocol.md §2.7): it decides each slot, checks the
/// seal, and recovers the ledger's candidate end. <see cref="LedgerQuorum"/> implements it over the ledger's <c>N</c>
/// node replicas; the object-store ledger over one copy in an object store (object-ledgers.md, row 7.3).
/// </summary>
/// <remarks>
/// <para>
/// Every member carries <c>acknowledged</c>, its <see cref="Ledger"/>'s known contiguous tail: every slot below it is
/// decided. The quorum carries it to its replicas as their contiguous tail (LP-I11); an implementation which keeps one
/// copy may take it as its reads' bound and its recovery's evidence. The sequencer, the window, in-order completion, and
/// writer lifetimes stay in the <see cref="Ledger"/> and its <see cref="LedgerWriter"/>, shared by every implementation.
/// </para>
/// <para>
/// Every answer reflects durable state, and an unknown outcome asserts nothing (LL-I8); the answers follow LL-I1 to LL-I9.
/// </para>
/// </remarks>
internal interface ILedgerStorage : IAsyncDisposable
{
    /// <summary>
    /// Gets the ledger's window <c>K</c>, which its writer's sequencer shares and which bounds a recovery's reads.
    /// </summary>
    int WindowSize { get; }

    /// <summary>
    /// Writes <paramref name="payload"/> at <paramref name="slot"/> as the ledger's writer: <c>Written</c> once the batch is
    /// durable at the slot and decided unless a recovery ends the ledger at or below it (LL-I2); <c>Sealed</c> once the
    /// write met the ledger's seal, or its storage was reclaimed, since a drop answers as a seal (LL-I4); or
    /// <c>Backpressure</c> when the storage refused the write for now and it could not decide, which asserts nothing.
    /// </summary>
    ValueTask<LedgerWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot acknowledged, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the seal, fixing its tail before it reads the seal (LL-I5): a <see cref="LedgerTail"/> whose
    /// <see cref="LedgerTail.IsSealed"/> false proves no recovery completed before the call began, and whose tail is a
    /// lower bound of the ledger's decided prefix at most every candidate end; or <c>Backpressure</c>.
    /// </summary>
    ValueTask<LedgerTailRead> CheckAsync(Slot acknowledged, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seals the ledger and recovers a candidate end, with the decided prefix it learned on the way (LL-I3); or
    /// <c>Dropped</c> once the ledger's storage was reclaimed.
    /// </summary>
    ValueTask<LedgerRecoveryOutcome> RecoverAsync(Slot acknowledged, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one slot. Below <paramref name="acknowledged"/> it answers the slot's batch, <c>Dropped</c>,
    /// <c>Backpressure</c>, or a failure, never <c>Missing</c> (LL-I6); at or above it, an answer decides nothing.
    /// <c>Backpressure</c> asserts nothing, and the caller may retry the read.
    /// </summary>
    ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot acknowledged, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads bytes <c>[offset, offset + length)</c> of the batch at one slot, a byte-range read, answering as
    /// <see cref="ReadAsync(Slot, Slot, CancellationToken)"/> does, or <see cref="ReplicaRead.OutsideBatch"/> when the
    /// range extends past the batch. Distinct from <see cref="ReadRangeAsync"/>, which reads consecutive slots.
    /// </summary>
    ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot acknowledged, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the consecutive slots <c>[from, below)</c> in slot order (ledger-protocol.md §2.7, object-ledgers.md §8.4).
    /// Each yielded item holds that slot's <see cref="ArcBuffer"/>, which the caller owns and disposes, or ends the
    /// enumeration with <see cref="ReplicaWriteOutcome.Dropped"/>, <see cref="ReplicaWriteOutcome.Backpressure"/>, or (only
    /// at or above <paramref name="acknowledged"/>) <see cref="ReplicaRead.Missing"/>. Any read-ahead buffers not yielded
    /// are released when enumeration ends or fails.
    /// </summary>
    /// <param name="from">The first slot.</param>
    /// <param name="below">The slot after the last; at least <paramref name="from"/>.</param>
    /// <param name="acknowledged">The ledger's known contiguous tail.</param>
    /// <param name="window">
    /// An optional shared in-flight read semaphore: each concurrent storage read holds a place in it until its bytes are
    /// taken, waiting for a place only when this scan holds no read in flight.
    /// </param>
    /// <param name="cancellationToken">Stops the enumeration.</param>
    IAsyncEnumerable<ReplicaRead> ReadRangeAsync(
        Slot from,
        Slot below,
        Slot acknowledged,
        SemaphoreSlim? window = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a value indicating whether this storage supports native batched scattered-slot reads via
    /// <see cref="ReadSlotsAsync"/>.
    /// </summary>
    bool SupportsReadSlots => false;

    /// <summary>
    /// Reads a strictly ascending set of decided <paramref name="slots"/> below <paramref name="acknowledged"/> in as
    /// few batched replica calls as possible, returning all requested slots packed in a <see cref="ReplicaSlotsBatch"/>,
    /// or <see cref="ReplicaWriteOutcome.Dropped"/> / <see cref="ReplicaWriteOutcome.Backpressure"/>.
    /// </summary>
    ValueTask<ReplicaSlotsRead> ReadSlotsAsync(
        ReadOnlyMemory<Slot> slots,
        Slot acknowledged,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This ledger storage does not support batched scattered-slot reads.");

    /// <summary>
    /// Offers <paramref name="acknowledged"/> to the storage as a hint it may publish to readers in other processes, as
    /// BookKeeper's explicit last add confirmed is (ledger-protocol.md §8.2). It asserts nothing to the front, never throws,
    /// returns without waiting for what it sends, and sends nothing once the storage's reads stopped or it was disposed.
    /// </summary>
    void WriteContiguousTail(Slot acknowledged);

    /// <summary>
    /// Learns a contiguous tail from the storage's own copy without a seal check: the greater of
    /// <paramref name="acknowledged"/> and a tail the storage holds evidence of, which is at most the ledger's contiguous
    /// tail (LP-I11, ledger-protocol.md §8.2). A storage with no copy of its own to read answers
    /// <paramref name="acknowledged"/> with no request; an answer which teaches nothing, as a failure does, answers it
    /// too.
    /// </summary>
    ValueTask<Slot> LearnTailAsync(Slot acknowledged, CancellationToken cancellationToken = default);

    /// <summary>
    /// Suppresses read-repair recovery writes on the preferred replica for this storage (used by
    /// <see cref="LedgerReader"/> once the ledger map records the ledger as <c>Closed(T)</c>, where every slot below
    /// <c>T</c> is already durable on <c>Q</c> replicas per LP-I7 and the ledger is pending drain and drop).
    /// </summary>
    void SuppressReadRepair() { }

    /// <summary>
    /// Stops the reads, seal checks, and recoveries, in progress and later, which fail with
    /// <see cref="ObjectDisposedException"/>, while writes go on (ledger-protocol.md §6.3).
    /// </summary>
    Task StopReadsAsync();
}
