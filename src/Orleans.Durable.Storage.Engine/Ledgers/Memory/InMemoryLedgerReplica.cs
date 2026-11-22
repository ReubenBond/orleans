using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Threading;

namespace Orleans.Durable.Storage.Engine.Ledgers.Memory;

/// <summary>
/// Provides one process-local ledger replica behind a window-bound facade, for tests and simulation
/// (ledger-replicas.md §9.1).
/// </summary>
/// <remarks>
/// The memory provider applies each operation atomically under its storage lock, so every answer is final when it is
/// given: an oracle for answers at quiescence (conventions.md §13). A facade which
/// <see cref="InMemoryLedgerReplicaTransport.Open"/> returns finds its replica on its node at each operation, so it answers as
/// the node does: its first write, recovery write, or seal creates the replica unless the node dropped the ledger, a
/// seal creating it sealed, and a read or contiguous-tail read of a replica the node neither holds nor dropped answers
/// as an empty one, the contiguous-tail read with its argument (LR-I7). Disposing it leaves the replica.
/// </remarks>
internal sealed class InMemoryLedgerReplica : ILedgerReplica
{
    private readonly InMemoryReplicaStorage? _storage;
    private readonly InMemoryLedgerReplicaTransport? _nodes;
    private readonly CallBudget? _budget;
    private readonly NodeId _node;
    private readonly LedgerId _ledger;
    private readonly int _windowSize;
    private readonly bool _ownsStorage;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryLedgerReplica"/> class over a fresh, exclusively owned
    /// replica.
    /// </summary>
    public InMemoryLedgerReplica(LedgerReplicaOptions options)
    {
        _windowSize = ValidateWindowSize(options);
        _storage = new InMemoryReplicaStorage(_windowSize);
        _ownsStorage = true;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryLedgerReplica"/> class over the replica of
    /// <paramref name="ledger"/> on <paramref name="node"/>, which <paramref name="nodes"/> holds.
    /// </summary>
    internal InMemoryLedgerReplica(InMemoryLedgerReplicaTransport nodes, NodeId node, LedgerId ledger, LedgerReplicaOptions options)
    {
        _windowSize = ValidateWindowSize(options);
        _nodes = nodes;
        _node = node;
        _ledger = ledger;
        _budget = nodes.IsBounded ? nodes.Budget(node) : null;
    }

    /// <inheritdoc/>
    public ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        bool recovery,
        CancellationToken cancellationToken = default)
    {
        LedgerReplicaOperations.ValidateSlot(slot);
        LedgerReplicaOperations.ValidateBatch(payload);
        if (_budget is not null && payload.Length > _nodes!.MaxPayloadLength)
        {
            // The emulated transport refuses a request above its payload limit before it leaves the caller.
            throw new ArgumentException(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The batch is {payload.Length} bytes, longer than the transport's payload limit of {_nodes.MaxPayloadLength}."),
                nameof(payload));
        }

        if (_budget is not null)
        {
            return WriteBoundedAsync(_nodes!, _budget, slot, payload, contiguousTail, recovery, cancellationToken);
        }

#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: write-checks-seal-before-landing
        if (_nodes is { WriteChecksSealBeforeLandingMutant: true })
        {
            return WriteAfterCheckingSealAsync(slot, payload, contiguousTail, recovery, cancellationToken);
        }
#endif

#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: memory-replica-yields
        if (_nodes is { YieldsAtLandingAndAnswer: true })
        {
            return AcrossYieldsAsync(() => Write(slot, payload, contiguousTail, recovery, cancellationToken));
        }
#endif

        return ValueTask.FromResult(Write(slot, payload, contiguousTail, recovery, cancellationToken));
    }

    /// <inheritdoc/>
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
        LedgerReplicaOperations.ValidateSlot(slot);
        if (_budget is not null)
        {
            return ReadBoundedAsync(_nodes!, _budget, slot, contiguousTail, cancellationToken);
        }

#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: memory-replica-yields
        if (_nodes is { YieldsAtLandingAndAnswer: true })
        {
            return AcrossYieldsAsync(() => Read(slot, contiguousTail, cancellationToken));
        }
#endif

        return ValueTask.FromResult(Read(slot, contiguousTail, cancellationToken));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A seal of a replica the node does not hold creates it sealed; a contiguous-tail read of one answers its argument,
    /// unsealed, and retains nothing (LR-I6, LR-I7).
    /// </remarks>
    public ValueTask<ReplicaTailRead> ReadTailAsync(
        Slot contiguousTail,
        bool seal,
        CancellationToken cancellationToken = default)
    {
        if (_budget is not null)
        {
            return ReadTailBoundedAsync(_nodes!, _budget, contiguousTail, seal, cancellationToken);
        }

#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: memory-replica-yields
        if (_nodes is { YieldsAtLandingAndAnswer: true })
        {
            return AcrossYieldsAsync(() => ReadTail(contiguousTail, seal, cancellationToken));
        }
#endif

        return ValueTask.FromResult(ReadTail(contiguousTail, seal, cancellationToken));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsStorage)
        {
            _storage!.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static int ValidateWindowSize(LedgerReplicaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        LedgerReplicaOperations.ValidateWindowSize(options.WindowSize);
        return options.WindowSize;
    }

#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: memory-replica-yields
    /// <summary>
    /// Applies <paramref name="operation"/> between two yields to the scheduler: one as its request lands and one before
    /// its answer returns.
    /// </summary>
    private static async ValueTask<T> AcrossYieldsAsync<T>(Func<T> operation)
    {
        await Task.Yield();
        var answer = operation();
        await Task.Yield();
        return answer;
    }
#endif

#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: write-checks-seal-before-landing
    /// <summary>
    /// The mutant's write: checks the seal, yields, lands, and answers written when its check found the replica unsealed,
    /// whatever the landing answered.
    /// </summary>
    private async ValueTask<ReplicaWriteOutcome> WriteAfterCheckingSealAsync(
        Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var check = ReadTail(contiguousTail, seal: false, cancellationToken);
        await Task.Yield();
        var landed = Write(slot, payload, contiguousTail, recovery, cancellationToken);
        await Task.Yield();
        return check.TryGetValue(out LedgerReplicaTail tail) && !tail.IsSealed && landed is ReplicaWriteOutcome.Sealed
            ? ReplicaWriteOutcome.Written.Instance
            : landed;
    }
#endif

    private ReplicaWriteOutcome Write(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken) =>
        StorageForMutation(cancellationToken) is { } storage
            ? storage.Write(slot.Value, payload, contiguousTail, recovery, _windowSize)
            : ReplicaWriteOutcome.Dropped.Instance;

    private ReplicaRead Read(Slot slot, Slot contiguousTail, CancellationToken cancellationToken) =>
        StorageForRead(cancellationToken) switch
        {
            InMemoryReplicaStorage storage => storage.Read(slot.Value, contiguousTail, _windowSize),
            ReplicaWriteOutcome.Dropped dropped => new ReplicaRead(dropped),
            StorageLookup.Absent => new ReplicaRead(ReplicaRead.Missing.Instance),
            null => throw new UnreachableException("A lookup holds a case."),
        };

    // A bounded transport's call holds its request payload's bytes of its node's budget until it ends.
    private async ValueTask<ReplicaWriteOutcome> WriteBoundedAsync(InMemoryLedgerReplicaTransport nodes, CallBudget budget, Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken)
    {
        var grant = await nodes.SendAsync(_node, budget, payload.Length, cancellationToken).ConfigureAwait(false);
        try
        {
            return Write(slot, payload, contiguousTail, recovery, cancellationToken);
        }
        finally
        {
            grant.Dispose();
        }
    }

    // A read reserves its largest reply, the payload limit, when it is admitted, as the per-node budget counts reply bytes.
    private async ValueTask<ReplicaRead> ReadBoundedAsync(InMemoryLedgerReplicaTransport nodes, CallBudget budget, Slot slot, Slot contiguousTail, CancellationToken cancellationToken)
    {
        var grant = await nodes.SendAsync(_node, budget, nodes.MaxPayloadLength, cancellationToken).ConfigureAwait(false);
        try
        {
            return Read(slot, contiguousTail, cancellationToken);
        }
        finally
        {
            grant.Dispose();
        }
    }

    private async ValueTask<ReplicaTailRead> ReadTailBoundedAsync(InMemoryLedgerReplicaTransport nodes, CallBudget budget, Slot contiguousTail, bool seal, CancellationToken cancellationToken)
    {
        var grant = await nodes.SendAsync(_node, budget, bytes: 0, cancellationToken).ConfigureAwait(false);
        try
        {
            return ReadTail(contiguousTail, seal, cancellationToken);
        }
        finally
        {
            grant.Dispose();
        }
    }

    private ReplicaTailRead ReadTail(Slot contiguousTail, bool seal, CancellationToken cancellationToken)
    {
        if (seal)
        {
            // A seal finds no replica only when the node dropped the ledger, since it creates one otherwise (LR-I7).
            return StorageForMutation(cancellationToken) is { } sealing
                ? sealing.ReadTail(contiguousTail, seal: true, _windowSize)
                : new ReplicaTailRead(ReplicaWriteOutcome.Dropped.Instance);
        }

        return StorageForRead(cancellationToken) switch
        {
            InMemoryReplicaStorage storage => storage.ReadTail(contiguousTail, seal: false, _windowSize),
            ReplicaWriteOutcome.Dropped dropped => new ReplicaTailRead(dropped),
            StorageLookup.Absent => new ReplicaTailRead(new LedgerReplicaTail(contiguousTail, IsSealed: false)),
            null => throw new UnreachableException("A lookup holds a case."),
        };
    }

    /// <summary>
    /// Returns the replica for a mutation, creating it when the node neither holds it nor dropped the ledger, or
    /// <see langword="null"/> when the node dropped the ledger (LR-I7).
    /// </summary>
    private InMemoryReplicaStorage? StorageForMutation(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return _storage ?? _nodes!.FindOrCreate(_node, _ledger, _windowSize);
    }

    /// <summary>
    /// Returns what the node holds of the ledger for a read: the replica, its tombstone, or nothing (LR-I7).
    /// </summary>
    private StorageLookup StorageForRead(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return _storage is { } storage ? storage : _nodes!.Find(_node, _ledger);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
