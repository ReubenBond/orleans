using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>The <see cref="ILedgerReplica"/> facade over the replica of one ledger in a LedgerStore (ledgerstore.md §2.1).</summary>
/// <remarks>
/// <para>
/// Every DATA write and every seal is one record in the node's WAL, published to the replica once it is durable.
/// Different slots become durable in either order; reads and mutations of one slot are linearizable (LogDrive paper,
/// section 3.1: "writes on the LogDrive can linearize out of address order"). Every answer reflects durable, published
/// state (LR-I4).
/// </para>
/// <para>
/// The replica finds its replica at each operation. A replica the store's catalog lists serves every operation of the
/// window its create record holds, and refuses one of another window (LR-I6). For one it does not list, the catalog
/// decides (LR-I7): when it holds the ledger's tombstone, every operation answers <c>Dropped</c> (LS-I2); otherwise a
/// write, recovery write, or seal creates the replica with a create record first, and a read or contiguous-tail read answers as an empty, unsealed replica. An operation whose WAL
/// write fails fails with the WAL's failure, whose failed storage operation stops the process; a store stopped so, which
/// a test or simulation observes, answers nothing more (conventions.md §14). A slot whose latest record may lie in a
/// damaged WAL range reports <see cref="LedgerReplicaCorruptException"/> (LS-I5). The slot table is checkpointed in the
/// background (LS-I8), so reopening replays only the WAL after the newest checkpoint. Disposing the replica releases it;
/// the store stays open.
/// </para>
/// </remarks>
public sealed class LedgerStoreReplica : ILedgerReplica
{
    private readonly LedgerId _ledger;
    private readonly int _windowSize;

    // The ledger's live replica once an operation found it: the store never replaces it, and a drop refuses every
    // operation through it.
    private LiveReplica? _live;
    private int _disposed;

    internal LedgerStoreReplica(LedgerStore store, LedgerId ledger, LedgerReplicaOptions options)
    {
        Store = store;
        _ledger = ledger;
        _windowSize = options.WindowSize;
    }

    internal LedgerStore Store { get; }

    /// <summary>
    /// Gets the live replica of the replica's ledger, which the catalog must list.
    /// </summary>
    internal LiveReplica Ledger =>
        Store.FindLive(_ledger) ?? throw new InvalidOperationException($"The LedgerStore does not list ledger {_ledger}.");

    /// <summary>
    /// Captures changed slot-table leaves and the seal below an exact cut at or below the observed WAL durable frontier,
    /// moving the replay start of a later recovery to that cut. A replica the catalog does not list has nothing to
    /// capture.
    /// </summary>
    public async ValueTask CheckpointAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (FindForRead() is LiveReplica live)
        {
            await live.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        bool recovery,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        LedgerReplicaOperations.ValidateSlot(slot);
        LedgerReplicaOperations.ValidateBatch(payload);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _live) is { } live)
        {
            return live.WriteAsync(slot, payload, contiguousTail, recovery, _windowSize, cancellationToken);
        }

        return WriteAfterCreateAsync(slot, payload, contiguousTail, recovery, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ledgerstore.md §2.1: "Reads the whole record and returns bytes <c>[offset, offset + length)</c>".
    /// </remarks>
    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        int offset,
        int length,
        Slot contiguousTail,
        CancellationToken cancellationToken = default) =>
        LedgerReplicaOperations.ReadRangeOfValueAsync(this, slot, offset, length, contiguousTail, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        Slot contiguousTail,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        LedgerReplicaOperations.ValidateSlot(slot);
        cancellationToken.ThrowIfCancellationRequested();
        return FindForRead() switch
        {
            LiveReplica live => live.ReadAsync(slot, contiguousTail, _windowSize, cancellationToken),
            ReplicaWriteOutcome.Dropped dropped => new(new ReplicaRead(dropped)),
            ReplicaLookup.Absent => new(new ReplicaRead(ReplicaRead.Missing.Instance)),
            null => throw new UnreachableException("A lookup holds a case."),
        };
    }

    /// <inheritdoc/>
    public bool SupportsReadRange => true;

    /// <inheritdoc/>
    public ValueTask<ReplicaRangeRead> ReadRangeAsync(
        Slot from,
        Slot below,
        Slot contiguousTail,
        int maxSlots,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        LedgerReplicaOperations.ValidateReadRange(from, below, maxSlots, maxBytes);
        cancellationToken.ThrowIfCancellationRequested();
        return FindForRead() switch
        {
            LiveReplica live => live.ReadRangeAsync(from, below, contiguousTail, _windowSize, maxSlots, maxBytes, cancellationToken),
            ReplicaWriteOutcome.Dropped dropped => new(new ReplicaRangeRead(dropped)),
            ReplicaLookup.Absent => new(new ReplicaRangeRead(ReplicaRangeBatch.Empty(from))),
            null => throw new UnreachableException("A lookup holds a case."),
        };
    }

    /// <inheritdoc/>
    public bool SupportsReadSlots => true;

    /// <inheritdoc/>
    public ValueTask<ReplicaSlotsRead> ReadSlotsAsync(
        ReadOnlyMemory<Slot> slots,
        Slot contiguousTail,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        LedgerReplicaOperations.ValidateReadSlots(slots.Span, maxBytes);
        cancellationToken.ThrowIfCancellationRequested();
        return FindForRead() switch
        {
            LiveReplica live => live.ReadSlotsAsync(slots, contiguousTail, _windowSize, maxBytes, cancellationToken),
            ReplicaWriteOutcome.Dropped dropped => new(new ReplicaSlotsRead(dropped)),
            ReplicaLookup.Absent => new(new ReplicaSlotsRead(ReplicaSlotsBatch.Empty)),
            null => throw new UnreachableException("A lookup holds a case."),
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A seal creates the replica it reaches, sealed (ledgerstore.md §8.3). A replica the catalog does not list, read
    /// without sealing, answers as an empty one: its contiguous tail is the argument, unsealed, and it retains nothing. An
    /// operation cancelled before it starts touches no storage (ledger-replicas.md §6.3), so a cancelled seal marks nothing.
    /// </remarks>
    public ValueTask<ReplicaTailRead> ReadTailAsync(
        Slot contiguousTail,
        bool seal,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (seal)
        {
            if (Volatile.Read(ref _live) is { } live)
            {
                return live.ReadTailAsync(contiguousTail, seal: true, _windowSize, cancellationToken);
            }

            return SealAfterCreateAsync(contiguousTail, cancellationToken);
        }

        return FindForRead() switch
        {
            LiveReplica live => live.ReadTailAsync(contiguousTail, seal: false, _windowSize, cancellationToken),
            ReplicaWriteOutcome.Dropped dropped => new(new ReplicaTailRead(dropped)),
            ReplicaLookup.Absent => new(new ReplicaTailRead(new LedgerReplicaTail(contiguousTail, IsSealed: false))),
            null => throw new UnreachableException("A lookup holds a case."),
        };
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _ = Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private async ValueTask<ReplicaWriteOutcome> WriteAfterCreateAsync(
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        bool recovery,
        CancellationToken cancellationToken) =>
        await CreateLiveAsync(cancellationToken).ConfigureAwait(false) is { } live
            ? await live.WriteAsync(slot, payload, contiguousTail, recovery, _windowSize, cancellationToken).ConfigureAwait(false)
            : ReplicaWriteOutcome.Dropped.Instance;

    private async ValueTask<ReplicaTailRead> SealAfterCreateAsync(
        Slot contiguousTail,
        CancellationToken cancellationToken) =>
        await CreateLiveAsync(cancellationToken).ConfigureAwait(false) is { } sealing
            ? await sealing.ReadTailAsync(contiguousTail, seal: true, _windowSize, cancellationToken).ConfigureAwait(false)
            : new ReplicaTailRead(ReplicaWriteOutcome.Dropped.Instance);

    private async ValueTask<LiveReplica?> CreateLiveAsync(CancellationToken cancellationToken)
    {
        var live = await Store.CreateLiveAsync(_ledger, _windowSize, cancellationToken).ConfigureAwait(false);
        if (live is not null)
        {
            Volatile.Write(ref _live, live);
        }

        return live;
    }

    /// <summary>
    /// Returns what the store holds of the ledger for a read, keeping a live replica it finds; it creates nothing.
    /// </summary>
    private ReplicaLookup FindForRead()
    {
        if (Volatile.Read(ref _live) is { } cached)
        {
            return cached;
        }

        var found = Store.FindForRead(_ledger);
        if (found is LiveReplica live)
        {
            Volatile.Write(ref _live, live);
        }

        return found;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
