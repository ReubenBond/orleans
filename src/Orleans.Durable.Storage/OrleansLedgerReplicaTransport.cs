using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Threading;

namespace Orleans.Durable.Storage;

/// <summary>
/// The silo's <see cref="ILedgerReplicaTransport"/> (orleans.md §5.12, LR-I9): a replica on the silo's own node is reached
/// in process, on the node's LedgerStore, and a replica on any other node through that node's <see cref="ILedgerStoreGrain"/>.
/// </summary>
/// <remarks>
/// <para>
/// A call refuses a payload or range above the payload limit with <see cref="ArgumentException"/> before it is sent
/// (OR-I17), takes one deadline, Orleans' response timeout from when the call is made, against which every wait it makes
/// counts, and then waits for its node's budget in one of two FIFO lanes (<see cref="NodeCallBudget"/>): writes and reads
/// share the data lane, at most <see cref="ReplicaCallLimits.MaxNodeCalls"/> calls and
/// <see cref="ReplicaCallLimits.MaxNodeCallBytes"/> of request payloads and replies outstanding to the node; contiguous-tail
/// reads and seals take the control lane, at most <see cref="ReplicaCallLimits.MaxNodeControlCalls"/> calls, so the failover
/// path never waits behind large writes. A call beyond its lane waits in arrival order and fails with
/// <see cref="LedgerReplicaQueueTimeoutException"/>, unsent, only once its deadline passes.
/// </para>
/// <para>
/// A call to the silo's own node enters its lane and then the node's serving gate (<see cref="LocalNode.ServingAsync"/>),
/// so calls made before the registration keep their order. A local read counts as a call and reserves no bytes: its reply
/// is the store's own buffer. The caller's cancellation ends only its own wait; the store operation runs to its end and
/// holds its grant until then.
/// </para>
/// <para>
/// A remote read reserves the reply it carries: a ranged read its length, and a whole read
/// <see cref="ReplicaCallLimits.MaxReadReplyBytes"/>. A batch longer than that is answered <c>TooLarge</c> with its length,
/// and the read enters its lane again reserving exactly that, within its deadline. A remote write, once granted, copies its
/// payload into an Orleans buffer, which every granted remote call of the same payload shares and the last of them
/// releases, so the copies a sender holds stay within its nodes' budgets; Orleans' proxy pins the copy for each message as
/// the call is made, and the caller copies a read reply's DATA into Durable Storage's page pool and releases what Orleans decoded, so
/// no Orleans message refers to a Durable Storage page (OR-I18). A remote call's caller's cancellation ends its wait at once, however
/// Orleans ends the grain call, which it abandons: its request may still land (OR-I16). A
/// remote call answered <c>Backpressure</c> returns its grant, waits a bounded, jittered backoff, and enters its lane again
/// while its deadline allows; it is reported only once its attempts are spent. Each result reaches the caller as orleans.md
/// §5.12's table states (<see cref="LedgerReplicaCallResults"/>); a <c>NotThisNode</c> answer and a placement refusal
/// request a registry re-read (OR-I14).
/// </para>
/// </remarks>
internal sealed class OrleansLedgerReplicaTransport : ILedgerReplicaTransport
{
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan LongestBackoff = TimeSpan.FromSeconds(1);

    private readonly LocalNode _local;
    private readonly Func<NodeId, ILedgerStoreGrain> _grain;
    private readonly Action _invalidate;
    private readonly Func<NodeId, long, int, TimeProvider, CallBudget> _createBudget;
    private readonly ConcurrentDictionary<NodeId, NodeBudgets> _budgets = new();
    private readonly SenderCopies _senderCopies = new();
    private readonly Lock _jitterLock = new();
    private readonly Random _jitter;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="OrleansLedgerReplicaTransport"/> class for a silo.</summary>
    public OrleansLedgerReplicaTransport(LocalNode local, IGrainFactory grains, NodeRegistryResolver resolver, ReplicaCallLimits limits, TimeProvider timeProvider)
        : this(
            local,
            node => grains.GetGrain<ILedgerStoreGrain>(node.Value),
            resolver.RequestReread,
            limits,
            NodeCallBudget.Create,
            new Random(),
            timeProvider)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OrleansLedgerReplicaTransport"/> class over explicit seams.</summary>
    /// <param name="local">The silo's own node.</param>
    /// <param name="grain">Returns a node's <see cref="ILedgerStoreGrain"/>.</param>
    /// <param name="invalidate">Requests a registry re-read.</param>
    /// <param name="limits">The replica call limits.</param>
    /// <param name="createBudget">Creates one of a node's lanes, from the node, its bytes, its calls, and the clock.</param>
    /// <param name="jitter">Jitters the backoff.</param>
    /// <param name="timeProvider">Times the deadlines and the backoff.</param>
    internal OrleansLedgerReplicaTransport(
        LocalNode local,
        Func<NodeId, ILedgerStoreGrain> grain,
        Action invalidate,
        ReplicaCallLimits limits,
        Func<NodeId, long, int, TimeProvider, CallBudget> createBudget,
        Random jitter,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(grain);
        ArgumentNullException.ThrowIfNull(invalidate);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(createBudget);
        ArgumentNullException.ThrowIfNull(jitter);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _local = local;
        _grain = grain;
        _invalidate = invalidate;
        Limits = limits;
        _createBudget = createBudget;
        _jitter = jitter;
        _timeProvider = timeProvider;
    }

    /// <summary>Gets the replica call limits.</summary>
    public ReplicaCallLimits Limits { get; }

    /// <summary>Gets the number of payloads whose shared sender copies are held by outstanding calls.</summary>
    internal int SenderCopyCount => _senderCopies.Count;

    /// <summary>Gets every lane of every node called so far.</summary>
    internal IEnumerable<CallBudget> Lanes => _budgets.Values.SelectMany(static budgets => new[] { budgets.Data, budgets.Control });

    /// <inheritdoc/>
    /// <remarks>
    /// A handle on the silo's own node once it is closed answers every call <see cref="LedgerReplicaUnavailableException"/>,
    /// without effect, as a call through a handle opened earlier does once the node stops (OR-I2).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The silo's node's store has not been opened: a handle is opened only once the silo has passed its
    /// runtime-initialize stage, which decides whether a node is the silo's own.
    /// </exception>
    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        LedgerReplicaOperations.ValidateWindowSize(options.WindowSize);
        if (ledger.Equals(default))
        {
            throw new ArgumentException("The default LedgerId names no ledger.", nameof(ledger));
        }

        if (node != _local.Node)
        {
            return new RemoteReplica(this, node, ledger, options.WindowSize, _grain(node));
        }

        if (_local.StoreIfOpen is { } store)
        {
            try
            {
                return new LocalReplica(this, node, store.OpenReplica(ledger, options));
            }
            catch (ObjectDisposedException) when (_local.State == LocalNode.NodeState.Closed)
            {
                // The node closed while the handle opened: it answers as a closed node's.
            }
        }

        return new ClosedReplica(this, node);
    }

    /// <inheritdoc/>
    public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default)
    {
        if (ledgers.IsEmpty)
        {
            throw new ArgumentException("A heartbeat names at least one ledger.", nameof(ledgers));
        }

        if (ledgers.Length > Limits.PayloadLimit / sizeof(ulong))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A replica call's heartbeat of {ledgers.Length} ledgers passes the replica payload limit of {Limits.PayloadLimit}."),
                nameof(ledgers));
        }

        var span = ledgers.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i].Equals(default))
            {
                throw new ArgumentException("The default LedgerId names no ledger.", nameof(ledgers));
            }
        }

        return node == _local.Node
            ? HeartbeatLocalAsync(node, ledgers, cancellationToken)
            : HeartbeatRemoteAsync(node, ledgers, cancellationToken);
    }

    private async ValueTask<ReplicaHeartbeatRead> HeartbeatLocalAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken)
    {
        var controlLane = ControlBudget(node);
        var lease = default(CallBudget.Lease);
        ValueTask<ReplicaHeartbeatReply> operation;
        try
        {
            var deadline = DeadlineFromNow();
            lease = await controlLane.EnterLeaseAsync(0, deadline, cancellationToken).ConfigureAwait(false);
            await ServingLocallyAsync(node, deadline, cancellationToken).ConfigureAwait(false);
            var store = _local.StoreIfOpen
                ?? throw new LedgerReplicaUnavailableException(node, "this silo's node is closed.");
            operation = store.HeartbeatReplicasAsync(ledgers, CancellationToken.None);
        }
        catch (ObjectDisposedException closed) when (_local.State == LocalNode.NodeState.Closed)
        {
            lease.Dispose();
            throw new LedgerReplicaUnavailableException(node, "this silo's node is closed.", closed);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        if (operation.IsCompletedSuccessfully)
        {
            var result = operation.Result;
            lease.Dispose();
            return new ReplicaHeartbeatRead(result);
        }

        if (!cancellationToken.CanBeCanceled || operation.IsCompleted)
        {
            try
            {
                return new ReplicaHeartbeatRead(await operation.ConfigureAwait(false));
            }
            catch (ObjectDisposedException closed) when (_local.State == LocalNode.NodeState.Closed)
            {
                throw new LedgerReplicaUnavailableException(node, "this silo's node is closed.", closed);
            }
            finally
            {
                lease.Dispose();
            }
        }

        var running = operation.AsTask();
        var answered = false;
        try
        {
            var answer = await running.WaitAsync(cancellationToken).ConfigureAwait(false);
            answered = true;
            return new ReplicaHeartbeatRead(answer);
        }
        catch (ObjectDisposedException closed) when (running.IsFaulted && _local.State == LocalNode.NodeState.Closed)
        {
            answered = true;
            throw new LedgerReplicaUnavailableException(node, "this silo's node is closed.", closed);
        }
        finally
        {
            if (answered)
            {
                lease.Dispose();
            }
            else
            {
                _ = ReleaseHeartbeatLeaseOnceEndedAsync(running, lease);
            }
        }
    }

    private static async Task ReleaseHeartbeatLeaseOnceEndedAsync(Task<ReplicaHeartbeatReply> running, CallBudget.Lease lease)
    {
        using (lease)
        {
            await ((Task)running).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!running.IsCompletedSuccessfully)
            {
                _ = running.Exception;
            }
        }
    }

    private async ValueTask<ReplicaHeartbeatRead> HeartbeatRemoteAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken)
    {
        var deadline = DeadlineFromNow();
        var rawLedgers = new ulong[ledgers.Length];
        var span = ledgers.Span;
        for (var i = 0; i < span.Length; i++)
        {
            rawLedgers[i] = span[i].Value;
        }

        var request = new LedgerReplicaHeartbeatRequest
        {
            Version = LedgerReplicaProtocol.Version,
            Ledgers = rawLedgers,
        };
        var grain = _grain(node);
        var controlLane = ControlBudget(node);
        var backoff = FirstBackoff;
        while (true)
        {
            TimeSpan delay;
            using (await controlLane.EnterLeaseAsync(0, deadline, cancellationToken).ConfigureAwait(false))
            {
                RemoteReplicaHeartbeatRead answer;
                try
                {
                    answer = await UntilCancelledAsync(grain.HeartbeatAsync(request, cancellationToken), abandoned: null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    if (failure is NodePlacementRefusedException)
                    {
                        _invalidate();
                    }

                    ExceptionDispatchInfo.Throw(LedgerReplicaCallResults.Failure(node, failure, cancellationToken));
                    throw new UnreachableException();
                }

                if (answer.Value is NotThisNode)
                {
                    _invalidate();
                }

                if (answer.Value is not ReplicaWriteOutcome.Backpressure || !TryNextHeartbeatBackoff(deadline, backoff, out delay))
                {
                    return LedgerReplicaCallResults.Heartbeat(node, answer, ledgers.Length);
                }
            }

            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, LongestBackoff.Ticks));
        }
    }

    private bool TryNextHeartbeatBackoff(long deadline, TimeSpan backoff, out TimeSpan delay)
    {
        delay = Jitter(backoff);
        return Remaining(deadline) > delay;
    }

    /// <summary>Returns <paramref name="node"/>'s data lane, of writes and reads.</summary>
    internal CallBudget Budget(NodeId node) => Budgets(node).Data;

    /// <summary>Returns <paramref name="node"/>'s control lane, of contiguous-tail reads and seals.</summary>
    internal CallBudget ControlBudget(NodeId node) => Budgets(node).Control;

    private NodeBudgets Budgets(NodeId node) => _budgets.GetOrAdd(
        node,
        static (node, transport) => new NodeBudgets(
            transport._createBudget(node, transport.Limits.MaxNodeCallBytes, transport.Limits.MaxNodeCalls, transport._timeProvider),
            transport._createBudget(node, 1, transport.Limits.MaxNodeControlCalls, transport._timeProvider)),
        this);

    /// <summary>Returns the deadline of a call made now, an absolute timestamp of the transport's clock.</summary>
    private long DeadlineFromNow() =>
        checked(_timeProvider.GetTimestamp() + (long)(Limits.Deadline.TotalSeconds * _timeProvider.TimestampFrequency));

    private TimeSpan Remaining(long deadline)
    {
        var remaining = _timeProvider.GetElapsedTime(_timeProvider.GetTimestamp(), deadline);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private TimeSpan Jitter(TimeSpan backoff)
    {
        lock (_jitterLock)
        {
            return backoff * (0.5 + _jitter.NextDouble() / 2);
        }
    }

    private void CheckPayload(int length, string paramName)
    {
        if (length > Limits.PayloadLimit)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A replica call of {length} bytes passes the replica payload limit of {Limits.PayloadLimit}."),
                paramName);
        }
    }

    /// <summary>
    /// Waits, within the call's deadline, for the silo's own node's serving gate (OR-I2): a wait the deadline ends fails
    /// with <see cref="LedgerReplicaQueueTimeoutException"/>, unsent, and a node whose registration failed or which stops
    /// with <see cref="LedgerReplicaUnavailableException"/>, without effect.
    /// </summary>
    private ValueTask ServingLocallyAsync(NodeId node, long deadline, CancellationToken cancellationToken)
    {
        var serving = _local.ServingAsync(node, cancellationToken);
        if (serving.IsCompletedSuccessfully)
        {
            return CheckLocalServing(node, serving.Result);
        }

        return ServingLocallySlowAsync(node, serving, deadline, cancellationToken);
    }

    private static ValueTask CheckLocalServing(NodeId node, LocalNodeServing answer)
    {
        if (answer.Value is LedgerStore)
        {
            return ValueTask.CompletedTask;
        }

        if (answer.Value is LocalNodeServing.Diskless)
        {
            return ValueTask.FromException(new LedgerReplicaUnavailableException(node, "this silo's node is diskless and holds no LedgerStore."));
        }

        Debug.Assert(answer.Value is LocalNodeServing.Stopping, "The silo's own node is the one its calls name.");
        return ValueTask.FromException(new LedgerReplicaUnavailableException(node, "this silo's node is stopping."));
    }

    private async ValueTask ServingLocallySlowAsync(NodeId node, ValueTask<LocalNodeServing> serving, long deadline, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        LocalNodeServing answer;
        try
        {
            answer = await serving.AsTask().WaitAsync(Remaining(deadline), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new LedgerReplicaQueueTimeoutException(node, _timeProvider.GetElapsedTime(started));
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new LedgerReplicaUnavailableException(node, "this silo's node is not registered.", failure);
        }

        await CheckLocalServing(node, answer).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a grain call until its caller cancels, and then abandons it: the caller's wait ends at once, whatever
    /// Orleans does with the message, which an outgoing grain call filter may hold before Orleans registers the call's
    /// token. The request may still land, so the outcome is unknown (OR-I16). An answer the call returns before the wait
    /// ends is the caller's; one which arrives once the call was abandoned goes to <paramref name="abandoned"/>, once,
    /// which releases what it holds; and a failure is observed.
    /// </summary>
    internal static Task<TAnswer> UntilCancelledAsync<TAnswer>(Task<TAnswer> call, Action<TAnswer>? abandoned, CancellationToken cancellationToken) =>
        !cancellationToken.CanBeCanceled ? call : UntilCancelledSlowAsync(call, abandoned, cancellationToken);

    private static async Task<TAnswer> UntilCancelledSlowAsync<TAnswer>(Task<TAnswer> call, Action<TAnswer>? abandoned, CancellationToken cancellationToken)
    {
        try
        {
            return await call.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = call.ContinueWith(
                static (ended, state) =>
                {
                    if (ended.IsCompletedSuccessfully)
                    {
                        ((Action<TAnswer>?)state)?.Invoke(ended.Result);
                    }
                    else
                    {
                        _ = ended.Exception;
                    }
                },
                abandoned,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>Releases the DATA of a read's answer which no caller takes.</summary>
    internal static void ReleaseData(RemoteReplicaRead answer)
    {
        if (answer.Value is RemoteReplicaRead.Data data)
        {
            data.Bytes.Dispose();
        }
    }

    /// <summary>A node's two lanes.</summary>
    private sealed record NodeBudgets(CallBudget Data, CallBudget Control);

    /// <summary>Base of the transport's replica handles: the node and disposal.</summary>
    private abstract class Replica(OrleansLedgerReplicaTransport transport, NodeId node) : ILedgerReplica
    {
        private int _disposed;

        protected OrleansLedgerReplicaTransport Transport => transport;

        protected NodeId Node => node;

        public abstract ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default);

        public abstract ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default);

        public abstract ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default);

        public abstract ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default);

        public virtual ValueTask DisposeAsync()
        {
            _ = Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }

        protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        /// <summary>Validates a write's slot and payload as the contract states, within the payload limit.</summary>
        protected void CheckWrite(Slot slot, ArcBuffer payload)
        {
            LedgerReplicaOperations.ValidateSlot(slot);
            LedgerReplicaOperations.ValidateBatch(payload);
            transport.CheckPayload(payload.Length, nameof(payload));
        }

        /// <summary>Validates a range as the contract states, within the payload limit.</summary>
        protected void CheckRange(Slot slot, int offset, int length)
        {
            LedgerReplicaOperations.ValidateSlot(slot);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            transport.CheckPayload(length, nameof(length));
        }
    }

    /// <summary>A handle on the silo's own node once it is closed: every call fails as unavailable, without effect (OR-I2).</summary>
    private sealed class ClosedReplica(OrleansLedgerReplicaTransport transport, NodeId node) : Replica(transport, node)
    {
        public override ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
            Closed<ReplicaWriteOutcome>();

        public override ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) => Closed<ReplicaRead>();

        public override ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            Closed<ReplicaRead>();

        public override ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) => Closed<ReplicaTailRead>();

        private ValueTask<T> Closed<T>()
        {
            ThrowIfDisposed();
            return ValueTask.FromException<T>(new LedgerReplicaUnavailableException(Node, "this silo's node is closed."));
        }
    }

    /// <summary>A replica on the silo's own node, reached in process on its LedgerStore within the node's lanes.</summary>
    private sealed class LocalReplica : Replica
    {
        private static readonly Action<ReplicaRead> ReleaseRead = static read =>
        {
            if (read.TryGetValue(out ArcBuffer batch))
            {
                batch.Dispose();
            }
        };

        private readonly LedgerStoreReplica _replica;
        private readonly CallBudget _dataLane;
        private readonly CallBudget _controlLane;

        public LocalReplica(OrleansLedgerReplicaTransport transport, NodeId node, LedgerStoreReplica replica)
            : base(transport, node)
        {
            _replica = replica;
            var budgets = transport.Budgets(node);
            _dataLane = budgets.Data;
            _controlLane = budgets.Control;
        }

        public override async ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            CheckWrite(slot, payload);
            var held = payload.Retain();
            var lease = default(CallBudget.Lease);
            ValueTask<ReplicaWriteOutcome> operation;
            try
            {
                var deadline = Transport.DeadlineFromNow();
                lease = await _dataLane.EnterLeaseAsync(payload.Length, deadline, cancellationToken).ConfigureAwait(false);
                await Transport.ServingLocallyAsync(Node, deadline, cancellationToken).ConfigureAwait(false);
                operation = _replica.WriteAsync(slot, held, contiguousTail, recovery, CancellationToken.None);
            }
            catch (ObjectDisposedException closed) when (Transport._local.State == LocalNode.NodeState.Closed)
            {
                lease.Dispose();
                held.Dispose();
                throw new LedgerReplicaUnavailableException(Node, "this silo's node is closed.", closed);
            }
            catch
            {
                lease.Dispose();
                held.Dispose();
                throw;
            }

            return await AwaitOperationAsync(operation, lease, held, abandoned: null, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LedgerReplicaOperations.ValidateSlot(slot);
            var lease = default(CallBudget.Lease);
            ValueTask<ReplicaRead> operation;
            try
            {
                var deadline = Transport.DeadlineFromNow();
                lease = await _dataLane.EnterLeaseAsync(0, deadline, cancellationToken).ConfigureAwait(false);
                await Transport.ServingLocallyAsync(Node, deadline, cancellationToken).ConfigureAwait(false);
                operation = _replica.ReadAsync(slot, contiguousTail, CancellationToken.None);
            }
            catch (ObjectDisposedException closed) when (Transport._local.State == LocalNode.NodeState.Closed)
            {
                lease.Dispose();
                throw new LedgerReplicaUnavailableException(Node, "this silo's node is closed.", closed);
            }
            catch
            {
                lease.Dispose();
                throw;
            }

            return await AwaitOperationAsync(operation, lease, held: default, ReleaseRead, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            CheckRange(slot, offset, length);
            var lease = default(CallBudget.Lease);
            ValueTask<ReplicaRead> operation;
            try
            {
                var deadline = Transport.DeadlineFromNow();
                lease = await _dataLane.EnterLeaseAsync(0, deadline, cancellationToken).ConfigureAwait(false);
                await Transport.ServingLocallyAsync(Node, deadline, cancellationToken).ConfigureAwait(false);
                operation = _replica.ReadAsync(slot, offset, length, contiguousTail, CancellationToken.None);
            }
            catch (ObjectDisposedException closed) when (Transport._local.State == LocalNode.NodeState.Closed)
            {
                lease.Dispose();
                throw new LedgerReplicaUnavailableException(Node, "this silo's node is closed.", closed);
            }
            catch
            {
                lease.Dispose();
                throw;
            }

            return await AwaitOperationAsync(operation, lease, held: default, ReleaseRead, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var lease = default(CallBudget.Lease);
            ValueTask<ReplicaTailRead> operation;
            try
            {
                var deadline = Transport.DeadlineFromNow();
                lease = await _controlLane.EnterLeaseAsync(0, deadline, cancellationToken).ConfigureAwait(false);
                await Transport.ServingLocallyAsync(Node, deadline, cancellationToken).ConfigureAwait(false);
                operation = _replica.ReadTailAsync(contiguousTail, seal, CancellationToken.None);
            }
            catch (ObjectDisposedException closed) when (Transport._local.State == LocalNode.NodeState.Closed)
            {
                lease.Dispose();
                throw new LedgerReplicaUnavailableException(Node, "this silo's node is closed.", closed);
            }
            catch
            {
                lease.Dispose();
                throw;
            }

            return await AwaitOperationAsync(operation, lease, held: default, abandoned: null, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            await _replica.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Releases a store operation's lease and the payload it holds once the operation ends, and disposes the answer
        /// <paramref name="abandoned"/> names, which no caller takes.
        /// </summary>
        private static async Task ReleaseOnceEndedAsync<T>(Task<T> operation, CallBudget.Lease lease, ArcBuffer held, Action<T>? abandoned)
        {
            using (lease)
            using (held)
            {
                await ((Task)operation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (operation.IsCompletedSuccessfully)
                {
                    abandoned?.Invoke(operation.Result);
                }
                else
                {
                    _ = operation.Exception;
                }
            }
        }

        /// <summary>
        /// Awaits an already-started local store <paramref name="operation"/> while holding <paramref name="lease"/> and
        /// <paramref name="held"/>, ending only the caller's wait on cancellation and handing release of the in-flight
        /// operation to <see cref="ReleaseOnceEndedAsync{T}"/>.
        /// </summary>
        private ValueTask<T> AwaitOperationAsync<T>(ValueTask<T> operation, CallBudget.Lease lease, ArcBuffer held, Action<T>? abandoned, CancellationToken cancellationToken)
        {
            if (operation.IsCompletedSuccessfully)
            {
                var result = operation.Result;
                lease.Dispose();
                held.Dispose();
                return new(result);
            }

            return !cancellationToken.CanBeCanceled || operation.IsCompleted
                ? AwaitUncancellableOperationAsync(operation, lease, held)
                : AwaitCancellableOperationAsync(operation.AsTask(), lease, held, abandoned, cancellationToken);
        }

        private async ValueTask<T> AwaitUncancellableOperationAsync<T>(ValueTask<T> operation, CallBudget.Lease lease, ArcBuffer held)
        {
            try
            {
                return await operation.ConfigureAwait(false);
            }
            catch (ObjectDisposedException closed) when (Transport._local.State == LocalNode.NodeState.Closed)
            {
                throw new LedgerReplicaUnavailableException(Node, "this silo's node is closed.", closed);
            }
            finally
            {
                lease.Dispose();
                held.Dispose();
            }
        }

        private async ValueTask<T> AwaitCancellableOperationAsync<T>(Task<T> running, CallBudget.Lease lease, ArcBuffer held, Action<T>? abandoned, CancellationToken cancellationToken)
        {
            var answered = false;
            try
            {
                var answer = await running.WaitAsync(cancellationToken).ConfigureAwait(false);
                answered = true;
                return answer;
            }
            catch (ObjectDisposedException closed) when (running.IsFaulted && Transport._local.State == LocalNode.NodeState.Closed)
            {
                answered = true;
                throw new LedgerReplicaUnavailableException(Node, "this silo's node is closed.", closed);
            }
            finally
            {
                if (answered)
                {
                    lease.Dispose();
                    held.Dispose();
                }
                else
                {
                    _ = ReleaseOnceEndedAsync(running, lease, held, abandoned);
                }
            }
        }
    }

    /// <summary>A replica on another node, reached through its <see cref="ILedgerStoreGrain"/>.</summary>
    private sealed class RemoteReplica : Replica
    {
        private readonly LedgerId _ledger;
        private readonly int _windowSize;
        private readonly ILedgerStoreGrain _grain;
        private readonly CallBudget _dataLane;
        private readonly CallBudget _controlLane;

        public RemoteReplica(OrleansLedgerReplicaTransport transport, NodeId node, LedgerId ledger, int windowSize, ILedgerStoreGrain grain)
            : base(transport, node)
        {
            _ledger = ledger;
            _windowSize = windowSize;
            _grain = grain;
            var budgets = transport.Budgets(node);
            _dataLane = budgets.Data;
            _controlLane = budgets.Control;
        }

        public override async ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            CheckWrite(slot, payload);
            var deadline = Transport.DeadlineFromNow();
            var senders = Transport._senderCopies;
            var backoff = FirstBackoff;
            while (true)
            {
                TimeSpan delay;
                using (await _dataLane.EnterLeaseAsync(payload.Length, deadline, cancellationToken).ConfigureAwait(false))
                {
                    RemoteReplicaWriteOutcome answer;
                    try
                    {
                        // Acquired once granted, so the copies a sender holds stay within its nodes' budgets, and released
                        // once the caller's wait ends: the caller may then release its payload, whose pages key the copy
                        // (OR-I18).
                        using var sender = senders.Acquire(payload);
                        var request = new LedgerReplicaWriteRequest
                        {
                            Version = LedgerReplicaProtocol.Version,
                            Ledger = _ledger.Value,
                            WindowSize = _windowSize,
                            Slot = slot.Value,
                            ContiguousTail = contiguousTail.Value,
                            Recovery = recovery,
                            Payload = sender.Copy,
                        };
                        answer = await UntilCancelledAsync(_grain.WriteAsync(request, cancellationToken), abandoned: null, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception failure)
                    {
                        ThrowCallFailure(failure, cancellationToken);
                        throw new UnreachableException();
                    }

                    if (answer.Value is NotThisNode)
                    {
                        Transport._invalidate();
                    }

                    if (answer.Value is not ReplicaWriteOutcome.Backpressure || !TryNextBackoff(deadline, backoff, out delay))
                    {
                        return LedgerReplicaCallResults.Write(Node, answer, recovery);
                    }
                }

                await Task.Delay(delay, Transport._timeProvider, cancellationToken).ConfigureAwait(false);
                backoff = DoubleBackoff(backoff);
            }
        }

        public override ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LedgerReplicaOperations.ValidateSlot(slot);
            return ReadCoreAsync(slot, contiguousTail, offset: 0, length: 0, cancellationToken);
        }

        public override ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            CheckRange(slot, offset, length);
            return ReadCoreAsync(slot, contiguousTail, offset, length, cancellationToken);
        }

        public override async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var deadline = Transport.DeadlineFromNow();
            var request = new LedgerReplicaTailRequest
            {
                Version = LedgerReplicaProtocol.Version,
                Ledger = _ledger.Value,
                WindowSize = _windowSize,
                ContiguousTail = contiguousTail.Value,
                Seal = seal,
            };
            var backoff = FirstBackoff;
            while (true)
            {
                TimeSpan delay;
                using (await _controlLane.EnterLeaseAsync(0, deadline, cancellationToken).ConfigureAwait(false))
                {
                    RemoteReplicaTailRead answer;
                    try
                    {
                        answer = await UntilCancelledAsync(_grain.ReadTailAsync(request, cancellationToken), abandoned: null, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception failure)
                    {
                        ThrowCallFailure(failure, cancellationToken);
                        throw new UnreachableException();
                    }

                    if (answer.Value is NotThisNode)
                    {
                        Transport._invalidate();
                    }

                    if (answer.Value is not ReplicaWriteOutcome.Backpressure || !TryNextBackoff(deadline, backoff, out delay))
                    {
                        return LedgerReplicaCallResults.Tail(Node, answer, contiguousTail);
                    }
                }

                await Task.Delay(delay, Transport._timeProvider, cancellationToken).ConfigureAwait(false);
                backoff = DoubleBackoff(backoff);
            }
        }

        /// <summary>
        /// Reads a whole value, reserving <see cref="ReplicaCallLimits.MaxReadReplyBytes"/> and, when the batch is longer,
        /// exactly its length; or a range of <paramref name="length"/>, reserving that.
        /// </summary>
        private async ValueTask<ReplicaRead> ReadCoreAsync(Slot slot, Slot contiguousTail, int offset, int length, CancellationToken cancellationToken)
        {
            var deadline = Transport.DeadlineFromNow();
            var reserved = length > 0 ? length : Transport.Limits.MaxReadReplyBytes;
            while (true)
            {
                var request = new LedgerReplicaReadRequest
                {
                    Version = LedgerReplicaProtocol.Version,
                    Ledger = _ledger.Value,
                    WindowSize = _windowSize,
                    Slot = slot.Value,
                    ContiguousTail = contiguousTail.Value,
                    Offset = offset,
                    Length = length,
                    MaxReplyBytes = reserved,
                };
                var backoff = FirstBackoff;
                while (true)
                {
                    TimeSpan delay;
                    using (await _dataLane.EnterLeaseAsync(reserved, deadline, cancellationToken).ConfigureAwait(false))
                    {
                        RemoteReplicaRead answer;
                        try
                        {
                            answer = await UntilCancelledAsync(_grain.ReadAsync(request, cancellationToken), ReleaseData, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception failure)
                        {
                            ThrowCallFailure(failure, cancellationToken);
                            throw new UnreachableException();
                        }

                        if (answer.Value is NotThisNode)
                        {
                            Transport._invalidate();
                        }

                        if (answer.Value is not ReplicaWriteOutcome.Backpressure || !TryNextBackoff(deadline, backoff, out delay))
                        {
                            if (length == 0 && answer.Value is RemoteReplicaRead.TooLarge tooLarge && tooLarge.Length > reserved && tooLarge.Length <= Transport.Limits.PayloadLimit)
                            {
                                reserved = tooLarge.Length;
                                break;
                            }

                            return LedgerReplicaCallResults.Read(Node, answer, reserved, ranged: length > 0);
                        }
                    }

                    await Task.Delay(delay, Transport._timeProvider, cancellationToken).ConfigureAwait(false);
                    backoff = DoubleBackoff(backoff);
                }
            }
        }

        private void ThrowCallFailure(Exception failure, CancellationToken cancellationToken)
        {
            if (failure is NodePlacementRefusedException)
            {
                Transport._invalidate();
            }

            ExceptionDispatchInfo.Throw(LedgerReplicaCallResults.Failure(Node, failure, cancellationToken));
        }

        private bool TryNextBackoff(long deadline, TimeSpan backoff, out TimeSpan delay)
        {
            delay = Transport.Jitter(backoff);
            return Transport.Remaining(deadline) > delay;
        }

        private static TimeSpan DoubleBackoff(TimeSpan backoff) =>
            TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, LongestBackoff.Ticks));
    }

    /// <summary>
    /// The Orleans copies of the payloads which granted remote calls carry, one per payload while any of its calls holds it,
    /// keyed by the payload's first page, its offset, and its length: a caller holds its payload, and so its pages,
    /// throughout every call which carries it, so no other payload has the same key while the copy is held (OR-I18). The
    /// last call to release a copy releases the shared pin; each message keeps the pin Orleans' proxy took as the call was
    /// made, which Orleans never releases, so the GC reclaims the pages.
    /// </summary>
    private sealed class SenderCopies
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<Key, Entry> _entries = [];

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>Returns the payload's copy, copying the payload into a new one when no call holds it.</summary>
        public Lease Acquire(ArcBuffer payload)
        {
            var key = new Key(payload.First, payload.Offset, payload.Length);
            Entry entry;
            lock (_lock)
            {
                ref var held = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, key, out _);
                held ??= new Entry();
                held.References++;
                entry = held;
            }

            try
            {
                return new Lease(this, key, entry, entry.GetOrCreate(payload));
            }
            catch
            {
                Release(key, entry);
                throw;
            }
        }

        private void Release(Key key, Entry entry)
        {
            lock (_lock)
            {
                if (--entry.References != 0)
                {
                    return;
                }

                _ = _entries.Remove(key);
            }

            entry.DisposeCopy();
        }

        public readonly record struct Key(ArcBufferPage Page, int Offset, int Length);

        /// <summary>A payload's copy, made once by the first call which needs it, and the calls which hold it, under the owner's lock.</summary>
        public sealed class Entry
        {
            private readonly Lock _initLock = new();
            private bool _created;
            private OrleansBuffer _copy;

            public int References { get; set; }

            public OrleansBuffer GetOrCreate(ArcBuffer payload)
            {
                if (Volatile.Read(ref _created))
                {
                    return _copy;
                }

                lock (_initLock)
                {
                    if (!_created)
                    {
                        _copy = WireBuffers.CopyToOrleans(payload);
                        Volatile.Write(ref _created, true);
                    }

                    return _copy;
                }
            }

            public void DisposeCopy()
            {
                if (Volatile.Read(ref _created))
                {
                    _copy.Dispose();
                }
            }
        }

        /// <summary>One call's hold on a payload's copy.</summary>
        public readonly struct Lease(SenderCopies owner, Key key, Entry entry, OrleansBuffer copy) : IDisposable
        {
            public OrleansBuffer Copy { get; } = copy;

            public void Dispose() => owner.Release(key, entry);
        }
    }
}
