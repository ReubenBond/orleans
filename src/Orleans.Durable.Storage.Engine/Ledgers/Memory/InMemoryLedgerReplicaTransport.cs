using Orleans.Durable.Storage.Engine.Threading;

namespace Orleans.Durable.Storage.Engine.Ledgers.Memory;

/// <summary>
/// Holds process-local ledger replicas, one per <c>(node, ledger)</c>, and each node's tombstones, for tests and
/// simulation (ledger-replicas.md §9.1).
/// </summary>
/// <remarks>
/// <para>
/// LR-I7: "A node holds a ledger's replica from the first write, recovery write, or seal which reaches it, unless the
/// node dropped the ledger". Each creation is decided under the lock which orders the node's drops: a write, recovery
/// write, or seal of a replica the node does not hold creates it, a seal creating it sealed, a read or contiguous-tail
/// read answers as an empty replica would, and every operation on a ledger the node dropped answers <c>Dropped</c>.
/// Every step is atomic under the lock, so a drop is durable for this instance's
/// lifetime before anything answers dropped (conventions.md §13). The replicas and tombstones last for this instance's
/// lifetime.
/// </para>
/// <para>
/// A transport created with a byte budget bounds the bytes outstanding to each node, request payloads and reply bytes
/// alike, a read reserving its largest reply before it is served: a call which lacks budget waits in its node's FIFO
/// queue until its deadline, and only then fails with <see cref="LedgerReplicaQueueTimeoutException"/>, with no effect
/// (ledger-replicas.md §2.2, <see cref="NodeCallBudget"/>).
/// </para>
/// <para>
/// A replica which <see cref="Open"/> returns finds its replica at each operation, so it answers as the node does.
/// <see cref="Node"/> hands out each node's own handle, whose drop removes a replica with every value and records its
/// ledger's tombstone in one step under the lock (LR-I3). A replica records the window of the request which created it.
/// </para>
/// </remarks>
internal sealed class InMemoryLedgerReplicaTransport : ILedgerReplicaTransport, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(NodeId Node, LedgerId Ledger), InMemoryReplicaStorage> _replicas = [];
    private readonly HashSet<(NodeId Node, LedgerId Ledger)> _tombstones = [];
    private readonly Dictionary<NodeId, CallBudget> _budgets = [];
    private readonly Dictionary<NodeId, InMemoryNodeLease> _nodeLeases = [];
    private readonly long _maxNodeCallBytes;
    private readonly TimeSpan _queueDeadline;
    private readonly TimeProvider? _timeProvider;
    private readonly TimeSpan _leaseDuration;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryLedgerReplicaTransport"/> class, which bounds no node's calls
    /// and grants zero lease duration.
    /// </summary>
    public InMemoryLedgerReplicaTransport()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryLedgerReplicaTransport"/> class with RAM-only replica
    /// leases timed by <paramref name="timeProvider"/> (LR-I10).
    /// </summary>
    public InMemoryLedgerReplicaTransport(TimeProvider timeProvider, TimeSpan leaseDuration)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseDuration, TimeSpan.Zero);
        _timeProvider = timeProvider;
        _leaseDuration = leaseDuration;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryLedgerReplicaTransport"/> class which emulates a transport of
    /// payload limit <paramref name="maxPayloadLength"/> and keeps at most <paramref name="maxNodeCallBytes"/> of request
    /// payloads and replies outstanding to each node, each read reserving its largest reply, the payload limit, when it is
    /// admitted (ledger-replicas.md §2.2).
    /// </summary>
    /// <param name="maxPayloadLength">The payload limit: the longest batch a write carries and a read answers.</param>
    /// <param name="maxNodeCallBytes">The bytes outstanding to each node, at least the payload limit.</param>
    /// <param name="queueDeadline">How long a call waits in its node's queue before it fails unsent.</param>
    /// <param name="timeProvider">Times the deadlines.</param>
    /// <param name="leaseDuration">Optional RAM-only lease duration granted by each node.</param>
    public InMemoryLedgerReplicaTransport(
        int maxPayloadLength,
        long maxNodeCallBytes,
        TimeSpan queueDeadline,
        TimeProvider timeProvider,
        TimeSpan leaseDuration = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNodeCallBytes, maxPayloadLength);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(queueDeadline, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseDuration, TimeSpan.Zero);
        MaxPayloadLength = maxPayloadLength;
        _maxNodeCallBytes = maxNodeCallBytes;
        _queueDeadline = queueDeadline;
        _timeProvider = timeProvider;
        _leaseDuration = leaseDuration;
    }

    /// <summary>Gets the payload limit a bounded transport emulates, which a read reserves before it is served.</summary>
    internal int MaxPayloadLength { get; }

    /// <summary>Gets the RAM-only lease duration granted by each node.</summary>
    internal TimeSpan LeaseDuration => _leaseDuration;

    /// <summary>
    /// Gets or sets a test seam awaited while a call holds its budget, before its node serves it, so a test can keep calls
    /// outstanding, as a network's transit would.
    /// </summary>
    internal Func<NodeId, CancellationToken, ValueTask>? Transit { get; set; }

    /// <summary>
    /// Gets a test seam which creates each node's budget in place of <see cref="NodeCallBudget.Create"/>, through which the
    /// transport tests give the transport a budget which breaks one of its rules.
    /// </summary>
    internal Func<NodeId, long, TimeProvider, CallBudget>? CreateBudget { get; init; }

    /// <summary>Gets a value indicating whether the transport bounds the bytes outstanding to each node.</summary>
    internal bool IsBounded => MaxPayloadLength > 0;

#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: memory-replica-yields
    /// <summary>
    /// Gets or sets a value indicating whether each operation of an unbounded transport's replicas yields to the
    /// scheduler as its request lands and again before its answer returns, so under simulation every interleaving of
    /// concurrent operations, which this provider otherwise applies at once, is a scheduling choice an exploration
    /// enumerates (verification.md §4.1, §7.2). Each operation still applies at one instant under the storage lock.
    /// </summary>
    internal bool YieldsAtLandingAndAnswer { get; set; }
#endif

#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: write-checks-seal-before-landing
    /// <summary>
    /// Gets or sets a value indicating whether a write checks its replica's seal in one step and lands in a later one,
    /// answering from its check, so a write which a seal between them refused is answered written although the replica
    /// never holds its batch: a race mutant of LR-I8 (ledger-replicas.md §2.1), which only an interleaving between the
    /// steps reveals (verification.md §4.1).
    /// </summary>
    internal bool WriteChecksSealBeforeLandingMutant { get; set; }
#endif

    /// <summary>Returns <paramref name="node"/>'s budget, which a bounded transport creates at the node's first call.</summary>
    internal CallBudget Budget(NodeId node)
    {
        if (_timeProvider is null)
        {
            throw new InvalidOperationException("The transport bounds no node's calls.");
        }

        lock (_lock)
        {
            if (!_budgets.TryGetValue(node, out var budget))
            {
                budget = CreateBudget?.Invoke(node, _maxNodeCallBytes, _timeProvider) ?? NodeCallBudget.Create(node, _maxNodeCallBytes, int.MaxValue, _timeProvider);
                _budgets.Add(node, budget);
            }

            return budget;
        }
    }

    /// <summary>
    /// Waits for <paramref name="bytes"/> of a node's <paramref name="budget"/>, then for the call's transit, and returns the
    /// grant the call releases when it ends.
    /// </summary>
    internal async ValueTask<CallBudget.Grant> SendAsync(NodeId node, CallBudget budget, long bytes, CancellationToken cancellationToken)
    {
        var grant = await budget.EnterAsync(bytes, budget.DeadlineAfter(_queueDeadline), cancellationToken).ConfigureAwait(false);
        try
        {
            if (Transit is { } transit)
            {
                await transit(node, cancellationToken).ConfigureAwait(false);
            }

            return grant;
        }
        catch
        {
            grant.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        return new InMemoryLedgerReplica(this, node, ledger, options);
    }

    /// <inheritdoc/>
    public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(
        NodeId node,
        ReadOnlyMemory<LedgerId> ledgers,
        CancellationToken cancellationToken = default)
    {
        if (IsBounded)
        {
            return HeartbeatBoundedAsync(node, Budget(node), ledgers, cancellationToken);
        }

#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: memory-replica-yields
        if (YieldsAtLandingAndAnswer)
        {
            return HeartbeatAcrossYieldsAsync(node, ledgers, cancellationToken);
        }
#endif

        return ValueTask.FromResult(Heartbeat(node, ledgers, cancellationToken));
    }

    /// <summary>
    /// Simulates a crash/restart of <paramref name="node"/>: wipes RAM-only granted leases on every replica of the node
    /// and latches a fresh <c>bootLeaseHorizon</c> while keeping all durable slots, seals, and tombstones intact.
    /// </summary>
    internal void RestartNode(NodeId node)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timeProvider is not null && _leaseDuration > TimeSpan.Zero)
            {
                GetOrCreateNodeLeaseLocked(node)!.ResetBootTimestamp();
            }

            foreach (var (key, replica) in _replicas)
            {
                if (key.Node == node)
                {
                    replica.ResetLeaseOnRestart();
                }
            }
        }
    }

    private async ValueTask<ReplicaHeartbeatRead> HeartbeatBoundedAsync(
        NodeId node,
        CallBudget budget,
        ReadOnlyMemory<LedgerId> ledgers,
        CancellationToken cancellationToken)
    {
        var grant = await SendAsync(node, budget, bytes: 0, cancellationToken).ConfigureAwait(false);
        try
        {
            return Heartbeat(node, ledgers, cancellationToken);
        }
        finally
        {
            grant.Dispose();
        }
    }

#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: memory-replica-yields
    private async ValueTask<ReplicaHeartbeatRead> HeartbeatAcrossYieldsAsync(
        NodeId node,
        ReadOnlyMemory<LedgerId> ledgers,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        var answer = Heartbeat(node, ledgers, cancellationToken);
        await Task.Yield();
        return answer;
    }
#endif

    private ReplicaHeartbeatRead Heartbeat(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ledgers.IsEmpty)
            {
                return new ReplicaHeartbeatReply(_leaseDuration, ReadOnlyMemory<ReplicaHeartbeatStatus>.Empty);
            }

            var outcomes = new ReplicaHeartbeatStatus[ledgers.Length];
            var span = ledgers.Span;
            for (var i = 0; i < span.Length; i++)
            {
                var key = (node, span[i]);
                if (_replicas.TryGetValue(key, out var replica))
                {
                    outcomes[i] = replica.Heartbeat();
                }
                else if (_tombstones.Contains(key))
                {
                    outcomes[i] = ReplicaHeartbeatStatus.Dropped;
                }
                else
                {
                    outcomes[i] = ReplicaHeartbeatStatus.Absent;
                }
            }

            return new ReplicaHeartbeatReply(_leaseDuration, outcomes);
        }
    }

    /// <summary>
    /// Returns the handle of <paramref name="node"/>, through which its host lists and drops its replicas (ledger-replicas.md
    /// §2.3).
    /// </summary>
    /// <param name="node">The node identity.</param>
    /// <returns>The node's handle.</returns>
    public ILedgerStore Node(NodeId node) => new InMemoryLedgerStore(this, node);

    private ValueTask DropReplicaAsync(NodeId node, LedgerId ledger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_tombstones.Add((node, ledger)))
            {
                return ValueTask.CompletedTask;
            }

            if (_replicas.Remove((node, ledger), out var replica))
            {
                // The replica refuses every later operation through any facade before the lock is released.
                replica.Drop();
                replica.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    private ValueTask<IReadOnlyList<LedgerId>> ListReplicasAsync(NodeId node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ValueTask.FromResult<IReadOnlyList<LedgerId>>(
                [.. _replicas.Keys.Where(replica => replica.Node == node).Select(static replica => replica.Ledger).Order()]);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        InMemoryReplicaStorage[] replicas;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            replicas = [.. _replicas.Values];
            _replicas.Clear();
        }

        foreach (var replica in replicas)
        {
            replica.Dispose();
        }
    }

    /// <summary>
    /// Finds the replica of <paramref name="ledger"/> on <paramref name="node"/> for a mutation, creating it when the
    /// node neither holds it nor dropped the ledger, or returns <see langword="null"/>, creating nothing, when the node
    /// dropped it (LR-I7).
    /// </summary>
    internal InMemoryReplicaStorage? FindOrCreate(NodeId node, LedgerId ledger, int windowSize)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_replicas.TryGetValue((node, ledger), out var existing))
            {
                return existing;
            }

            if (_tombstones.Contains((node, ledger)))
            {
                return null;
            }

            var nodeLease = GetOrCreateNodeLeaseLocked(node);
            var replica = new InMemoryReplicaStorage(windowSize, nodeLease);
            _replicas.Add((node, ledger), replica);
            return replica;
        }
    }

    private InMemoryNodeLease? GetOrCreateNodeLeaseLocked(NodeId node)
    {
        if (_timeProvider is null || _leaseDuration <= TimeSpan.Zero)
        {
            return null;
        }

        if (!_nodeLeases.TryGetValue(node, out var lease))
        {
            lease = new InMemoryNodeLease(_timeProvider, _leaseDuration);
            _nodeLeases.Add(node, lease);
        }

        return lease;
    }

    /// <summary>
    /// Finds what <paramref name="node"/> holds of <paramref name="ledger"/> for a read: its replica, its tombstone, or
    /// nothing, when the read answers as an empty replica would; it creates nothing (LR-I7).
    /// </summary>
    internal StorageLookup Find(NodeId node, LedgerId ledger)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_replicas.TryGetValue((node, ledger), out var existing))
            {
                return existing;
            }

            return _tombstones.Contains((node, ledger)) ? ReplicaWriteOutcome.Dropped.Instance : StorageLookup.Absent.Instance;
        }
    }

    /// <summary>
    /// One node's handle over the replicas it holds.
    /// </summary>
    private sealed class InMemoryLedgerStore(InMemoryLedgerReplicaTransport nodes, NodeId node) : ILedgerStore
    {
        public NodeId NodeId => node;

        public ValueTask<IReadOnlyList<LedgerId>> ListReplicasAsync(CancellationToken cancellationToken = default) =>
            nodes.ListReplicasAsync(node, cancellationToken);

        public ValueTask DropReplicaAsync(LedgerId ledger, CancellationToken cancellationToken = default) =>
            nodes.DropReplicaAsync(node, ledger, cancellationToken);
    }
}
