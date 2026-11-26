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
using Orleans.Runtime;

namespace Orleans.Durable.Storage;

/// <summary>
/// The silo's <see cref="ILedgerReplicaTransport"/> (orleans.md §5.12, LR-I9): a replica on the silo's own node is reached
/// in process, on the node's LedgerStore, and a replica on any other node through that node's <see cref="ILedgerStoreGrain"/>.
/// </summary>
/// <remarks>
/// <para>
/// A call refuses a payload or range above the payload limit with <see cref="ArgumentException"/> before it is sent
/// (OR-I17), takes one deadline, Orleans' response timeout from when the call is made, against which every wait it makes
/// counts, and then waits for its node's budget in one of three FIFO lanes (<see cref="NodeCallBudget"/>): writes take the
/// write lane and reads take the read lane, each at most <see cref="ReplicaCallLimits.MaxNodeCalls"/> calls and
/// <see cref="ReplicaCallLimits.MaxNodeCallBytes"/> of payloads outstanding to the node so background catch-up or drain
/// reads never head-of-line block foreground quorum writes; contiguous-tail reads, seals, and heartbeats take the control
/// lane, at most <see cref="ReplicaCallLimits.MaxNodeControlCalls"/> calls, so the failover path never waits behind large
/// writes or reads. A call beyond its lane waits in arrival order and fails with
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
/// and the read enters its lane again reserving exactly that, within its deadline. Remote writes and read replies carry
/// payloads directly in zero-copy consume-on-serialize <see cref="WireArcBuffer"/> leases over Durable Storage's pooled
/// pages, with no intermediate copy or shared sender-copy table (OR-I18). A remote call's caller's cancellation ends its
/// wait at once, however Orleans ends the grain call, which it abandons: its request may still land (OR-I16). A
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
    private readonly NodeRegistryResolver? _resolver;
    private readonly IClusterMembershipService? _membership;
    private readonly Func<NodeId, SiloAddress?>? _resolveActiveSilo;
    private readonly Action _invalidateIfStale;
    private readonly Func<NodeId, long, int, TimeProvider, CallBudget> _createBudget;
    private readonly ConcurrentDictionary<NodeId, NodeBudgets> _budgets = new();
    private readonly Lock _jitterLock = new();
    private readonly Random _jitter;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="OrleansLedgerReplicaTransport"/> class for a silo.</summary>
    public OrleansLedgerReplicaTransport(
        LocalNode local,
        IGrainFactory grains,
        NodeRegistryResolver resolver,
        IClusterMembershipService membership,
        ReplicaCallLimits limits,
        TimeProvider timeProvider)
        : this(
            local,
            node => grains.GetGrain<ILedgerStoreGrain>(node.Value),
            resolver.RequestReread,
            limits,
            NodeCallBudget.Create,
            new Random(),
            timeProvider,
            resolveActiveSilo: null,
            resolver.RequestRereadIfStale)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(membership);
        _resolver = resolver;
        _membership = membership;
    }

    /// <summary>Initializes a new instance of the <see cref="OrleansLedgerReplicaTransport"/> class over explicit seams.</summary>
    /// <param name="local">The silo's own node.</param>
    /// <param name="grain">Returns a node's <see cref="ILedgerStoreGrain"/>.</param>
    /// <param name="invalidate">Requests a registry re-read.</param>
    /// <param name="limits">The replica call limits.</param>
    /// <param name="createBudget">Creates one of a node's lanes, from the node, its bytes, its calls, and the clock.</param>
    /// <param name="jitter">Jitters the backoff.</param>
    /// <param name="timeProvider">Times the deadlines and the backoff.</param>
    /// <param name="resolveActiveSilo">Optional resolver returning the active <see cref="SiloAddress"/> for a remote node, or <see langword="null"/> when inactive.</param>
    /// <param name="invalidateIfStale">Optional stale-registry re-read trigger invoked when a remote node is inactive or stopping.</param>
    internal OrleansLedgerReplicaTransport(
        LocalNode local,
        Func<NodeId, ILedgerStoreGrain> grain,
        Action invalidate,
        ReplicaCallLimits limits,
        Func<NodeId, long, int, TimeProvider, CallBudget> createBudget,
        Random jitter,
        TimeProvider timeProvider,
        Func<NodeId, SiloAddress?>? resolveActiveSilo = null,
        Action? invalidateIfStale = null)
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
        _resolveActiveSilo = resolveActiveSilo;
        _invalidateIfStale = invalidateIfStale ?? invalidate;
        Limits = limits;
        _createBudget = createBudget;
        _jitter = jitter;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Fast-fails calls targeting a remote node with no active registered silo in membership or whose resolved silo
    /// incarnation has already reported stopping/unavailable, before entering <see cref="CallBudget"/> or invoking
    /// Orleans grain placement (orleans.md §5.12).
    /// </summary>
    private SiloAddress? EnsureRemoteActive(NodeId node, NodeBudgets budgets)
    {
        SiloAddress? silo;
        if (_resolver is not null && _membership is not null)
        {
            var registry = _resolver.Current;
            var snapshot = _membership.CurrentSnapshot;
            if (ReferenceEquals(registry, Volatile.Read(ref budgets.CachedRegistry))
                && ReferenceEquals(snapshot, Volatile.Read(ref budgets.CachedSnapshot)))
            {
                silo = Volatile.Read(ref budgets.CachedActiveSilo);
            }
            else
            {
                var candidate = registry?.Find(node)?.Silo;
                silo = candidate is not null && snapshot.GetSiloStatus(candidate) == SiloStatus.Active ? candidate : null;
                budgets.UpdateActiveCache(registry, snapshot, silo);
            }
        }
        else if (_resolveActiveSilo is not null)
        {
            silo = _resolveActiveSilo(node);
        }
        else
        {
            return null;
        }

        if (silo is null)
        {
            _invalidateIfStale();
            throw new LedgerReplicaUnavailableException(node, "the node has no active registered silo in membership.");
        }

        var stopping = Volatile.Read(ref budgets.StoppingSilo);
        if (stopping is not null)
        {
            if (stopping.Equals(silo))
            {
                _invalidateIfStale();
                throw new LedgerReplicaUnavailableException(node, string.Create(CultureInfo.InvariantCulture, $"silo {silo} for node {node} is stopping or unavailable."));
            }

            // The registry advanced to a newer active silo incarnation for this node: clear the stale stopping mark.
            _ = Interlocked.CompareExchange(ref budgets.StoppingSilo, null, stopping);
        }

        return silo;
    }

    private void ObserveStoppingRefusal(NodeBudgets budgets, SiloAddress? activeSilo, ReplicaCallRefused refused)
    {
        if (refused.Kind == ReplicaRefusal.Stopping)
        {
            MarkIncarnationStopping(budgets, activeSilo);
        }
    }

    private void ObserveCallFailure(NodeBudgets budgets, SiloAddress? activeSilo, Exception failure)
    {
        if (failure is NodePlacementRefusedException)
        {
            _invalidate();
            return;
        }

        if (IsStoppingOrUnavailableFailure(failure))
        {
            MarkIncarnationStopping(budgets, activeSilo);
        }
    }

    private void MarkIncarnationStopping(NodeBudgets budgets, SiloAddress? activeSilo)
    {
        if (activeSilo is not null)
        {
            Volatile.Write(ref budgets.StoppingSilo, activeSilo);
            _invalidateIfStale();
        }
    }

    private static bool IsStoppingOrUnavailableFailure(Exception failure) => failure switch
    {
        SiloUnavailableException => true,
        OrleansMessageRejectionException { InnerException: SiloUnavailableException } => true,
        OrleansMessageRejectionException rejection => rejection.Message.Contains("stopping", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>Clears any recorded stopping incarnation mark for <paramref name="node"/>; for tests that inject synthetic stopping replies on a live silo.</summary>
    internal void ClearStoppingIncarnation(NodeId node)
    {
        if (_budgets.TryGetValue(node, out var budgets))
        {
            Volatile.Write(ref budgets.StoppingSilo, null);
        }
    }

    /// <summary>Gets the replica call limits.</summary>
    public ReplicaCallLimits Limits { get; }

    /// <summary>Gets every lane of every node called so far.</summary>
    internal IEnumerable<CallBudget> Lanes => _budgets.Values.SelectMany(static budgets => new[] { budgets.Write, budgets.Read, budgets.Control });

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
        var budgets = Budgets(node);
        _ = EnsureRemoteActive(node, budgets);
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
        var controlLane = budgets.Control;
        var backoff = FirstBackoff;
        while (true)
        {
            var activeSilo = EnsureRemoteActive(node, budgets);
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
                    ObserveCallFailure(budgets, activeSilo, failure);
                    ExceptionDispatchInfo.Throw(LedgerReplicaCallResults.Failure(node, failure, cancellationToken));
                    throw new UnreachableException();
                }

                if (answer.Value is NotThisNode)
                {
                    _invalidate();
                }
                else if (answer.Value is ReplicaCallRefused refused)
                {
                    ObserveStoppingRefusal(budgets, activeSilo, refused);
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

    /// <summary>Returns <paramref name="node"/>'s write lane.</summary>
    internal CallBudget Budget(NodeId node) => Budgets(node).Write;

    /// <summary>Returns <paramref name="node"/>'s read lane.</summary>
    internal CallBudget ReadBudget(NodeId node) => Budgets(node).Read;

    /// <summary>Returns <paramref name="node"/>'s control lane, of contiguous-tail reads, seals, and heartbeats.</summary>
    internal CallBudget ControlBudget(NodeId node) => Budgets(node).Control;

    private NodeBudgets Budgets(NodeId node) => _budgets.GetOrAdd(
        node,
        static (node, transport) => new NodeBudgets(
            transport._createBudget(node, transport.Limits.MaxNodeCallBytes, transport.Limits.MaxNodeCalls, transport._timeProvider),
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
        UntilCancelledAsync(call, abandoned, releaseOnEnd: null, cancellationToken);

    internal static Task<TAnswer> UntilCancelledAsync<TAnswer>(
        Task<TAnswer> call,
        Action<TAnswer>? abandoned,
        IDisposable? releaseOnEnd,
        CancellationToken cancellationToken) =>
        !cancellationToken.CanBeCanceled && releaseOnEnd is null
            ? call
            : UntilCancelledCoreAsync(call, abandoned, releaseOnEnd, cancellationToken);

    private static async Task<TAnswer> UntilCancelledCoreAsync<TAnswer>(
        Task<TAnswer> call,
        Action<TAnswer>? abandoned,
        IDisposable? releaseOnEnd,
        CancellationToken cancellationToken)
    {
        var completed = false;
        try
        {
            var answer = cancellationToken.CanBeCanceled
                ? await call.WaitAsync(cancellationToken).ConfigureAwait(false)
                : await call.ConfigureAwait(false);
            completed = true;
            return answer;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveAbandoned(call, abandoned, releaseOnEnd);
            throw;
        }
        catch
        {
            completed = true;
            throw;
        }
        finally
        {
            if (completed)
            {
                releaseOnEnd?.Dispose();
            }
        }
    }

    /// <summary>Observes an abandoned in-flight grain call, releasing any unconsumed buffer or abandoned answer once it settles.</summary>
    internal static void ObserveAbandoned<TAnswer>(Task<TAnswer> call, Action<TAnswer>? abandoned, IDisposable? releaseOnEnd)
    {
        _ = call.ContinueWith(
            static (ended, state) =>
            {
                var (onAbandoned, toRelease) = ((Action<TAnswer>?, IDisposable?))state!;
                try
                {
                    if (ended.IsCompletedSuccessfully)
                    {
                        onAbandoned?.Invoke(ended.Result);
                    }
                    else
                    {
                        _ = ended.Exception;
                    }
                }
                finally
                {
                    toRelease?.Dispose();
                }
            },
            (abandoned, releaseOnEnd),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Releases the DATA of a read's answer which no caller takes.</summary>
    internal static void ReleaseData(RemoteReplicaRead answer)
    {
        if (answer.Value is RemoteReplicaRead.Data data)
        {
            data.Bytes?.Dispose();
        }
    }

    /// <summary>Releases the packed bytes of a range read's answer which no caller takes.</summary>
    internal static void ReleaseRangeBatch(RemoteReplicaRangeRead answer)
    {
        if (answer.Value is RemoteReplicaRangeRead.Batch batch)
        {
            batch.Bytes?.Dispose();
        }
    }

    /// <summary>Releases the packed bytes of a scattered-slot read's answer which no caller takes.</summary>
    internal static void ReleaseSlotsBatch(RemoteReplicaSlotsRead answer)
    {
        if (answer.Value is RemoteReplicaSlotsRead.Batch batch)
        {
            batch.Bytes?.Dispose();
        }
    }

    /// <summary>A node's three lanes (Write, Read, Control) plus its cached active/stopping silo incarnation state.</summary>
    private sealed class NodeBudgets(CallBudget write, CallBudget read, CallBudget control)
    {
        private readonly Lock _cacheLock = new();

        public CallBudget Write { get; } = write;

        public CallBudget Read { get; } = read;

        public CallBudget Control { get; } = control;

        public NodeRegistryValue? CachedRegistry;
        public ClusterMembershipSnapshot? CachedSnapshot;
        public SiloAddress? CachedActiveSilo;
        public SiloAddress? StoppingSilo;

        public void UpdateActiveCache(NodeRegistryValue? registry, ClusterMembershipSnapshot snapshot, SiloAddress? activeSilo)
        {
            lock (_cacheLock)
            {
                Volatile.Write(ref CachedActiveSilo, activeSilo);
                Volatile.Write(ref CachedSnapshot, snapshot);
                Volatile.Write(ref CachedRegistry, registry);
            }
        }
    }

    /// <summary>Base of the transport's replica handles: the node and disposal.</summary>
    private abstract class Replica(OrleansLedgerReplicaTransport transport, NodeId node) : ILedgerReplica
    {
        private int _disposed;

        protected OrleansLedgerReplicaTransport Transport => transport;

        protected NodeId Node => node;

        public abstract ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default);

        public abstract ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default);

        public abstract ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default);

        public bool SupportsReadRange => true;

        public abstract ValueTask<ReplicaRangeRead> ReadRangeAsync(Slot from, Slot below, Slot contiguousTail, int maxSlots, int maxBytes, CancellationToken cancellationToken = default);

        public bool SupportsReadSlots => true;

        public abstract ValueTask<ReplicaSlotsRead> ReadSlotsAsync(ReadOnlyMemory<Slot> slots, Slot contiguousTail, int maxBytes, CancellationToken cancellationToken = default);

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

        public override ValueTask<ReplicaRangeRead> ReadRangeAsync(Slot from, Slot below, Slot contiguousTail, int maxSlots, int maxBytes, CancellationToken cancellationToken = default) =>
            Closed<ReplicaRangeRead>();

        public override ValueTask<ReplicaSlotsRead> ReadSlotsAsync(ReadOnlyMemory<Slot> slots, Slot contiguousTail, int maxBytes, CancellationToken cancellationToken = default) =>
            Closed<ReplicaSlotsRead>();

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

        private static readonly Action<ReplicaRangeRead> ReleaseRange = static read => read.Release();

        private static readonly Action<ReplicaSlotsRead> ReleaseSlots = static read => read.Release();

        private readonly LedgerStoreReplica _replica;
        private readonly CallBudget _writeLane;
        private readonly CallBudget _readLane;
        private readonly CallBudget _controlLane;

        public LocalReplica(OrleansLedgerReplicaTransport transport, NodeId node, LedgerStoreReplica replica)
            : base(transport, node)
        {
            _replica = replica;
            var budgets = transport.Budgets(node);
            _writeLane = budgets.Write;
            _readLane = budgets.Read;
            _controlLane = budgets.Control;
        }

        public override ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            CheckWrite(slot, payload);
            var held = payload.Retain();
            var deadline = Transport.DeadlineFromNow();
            var leaseTask = _writeLane.EnterLeaseAsync(payload.Length, deadline, cancellationToken);
            if (!leaseTask.IsCompletedSuccessfully)
            {
                return WriteSlowAsync(slot, held, contiguousTail, recovery, deadline, leaseTask, servingTask: null, lease: default, cancellationToken);
            }

            var lease = leaseTask.Result;
            var servingTask = Transport.ServingLocallyAsync(Node, deadline, cancellationToken);
            if (!servingTask.IsCompletedSuccessfully)
            {
                return WriteSlowAsync(slot, held, contiguousTail, recovery, deadline, leaseTask: default, servingTask, lease, cancellationToken);
            }

            ValueTask<ReplicaWriteOutcome> operation;
            try
            {
                operation = _replica.WriteAsync(slot, held, contiguousTail, recovery, CancellationToken.None);
            }
            catch (ObjectDisposedException closed) when (Transport._local.State == LocalNode.NodeState.Closed)
            {
                lease.Dispose();
                held.Dispose();
                return ValueTask.FromException<ReplicaWriteOutcome>(new LedgerReplicaUnavailableException(Node, "this silo's node is closed.", closed));
            }
            catch
            {
                lease.Dispose();
                held.Dispose();
                throw;
            }

            return AwaitOperationAsync(operation, lease, held, abandoned: null, cancellationToken);
        }

        private async ValueTask<ReplicaWriteOutcome> WriteSlowAsync(
            Slot slot,
            ArcBuffer held,
            Slot contiguousTail,
            bool recovery,
            long deadline,
            ValueTask<CallBudget.Lease> leaseTask,
            ValueTask? servingTask,
            CallBudget.Lease lease,
            CancellationToken cancellationToken)
        {
            ValueTask<ReplicaWriteOutcome> operation;
            try
            {
                if (servingTask is null)
                {
                    lease = await leaseTask.ConfigureAwait(false);
                    await Transport.ServingLocallyAsync(Node, deadline, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await servingTask.Value.ConfigureAwait(false);
                }

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
                lease = await _readLane.EnterLeaseAsync(0, deadline, cancellationToken).ConfigureAwait(false);
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
                lease = await _readLane.EnterLeaseAsync(0, deadline, cancellationToken).ConfigureAwait(false);
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

        public override async ValueTask<ReplicaRangeRead> ReadRangeAsync(Slot from, Slot below, Slot contiguousTail, int maxSlots, int maxBytes, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LedgerReplicaOperations.ValidateReadRange(from, below, maxSlots, maxBytes);
            var clampedBytes = Math.Min(maxBytes, Transport.Limits.PayloadLimit);
            var lease = default(CallBudget.Lease);
            ValueTask<ReplicaRangeRead> operation;
            try
            {
                var deadline = Transport.DeadlineFromNow();
                lease = await _readLane.EnterLeaseAsync(0, deadline, cancellationToken).ConfigureAwait(false);
                await Transport.ServingLocallyAsync(Node, deadline, cancellationToken).ConfigureAwait(false);
                operation = _replica.ReadRangeAsync(from, below, contiguousTail, maxSlots, clampedBytes, CancellationToken.None);
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

            return await AwaitOperationAsync(operation, lease, held: default, ReleaseRange, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask<ReplicaSlotsRead> ReadSlotsAsync(ReadOnlyMemory<Slot> slots, Slot contiguousTail, int maxBytes, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LedgerReplicaOperations.ValidateReadSlots(slots.Span, maxBytes);
            if (slots.IsEmpty)
            {
                return ReplicaSlotsBatch.Empty;
            }

            if (slots.Length > Transport.Limits.PayloadLimit / sizeof(long))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"A replica slots read of {slots.Length} slots passes the replica payload limit of {Transport.Limits.PayloadLimit}."),
                    nameof(slots));
            }

            var clampedBytes = Math.Min(maxBytes, Transport.Limits.PayloadLimit);
            var lease = default(CallBudget.Lease);
            ValueTask<ReplicaSlotsRead> operation;
            try
            {
                var deadline = Transport.DeadlineFromNow();
                lease = await _readLane.EnterLeaseAsync(0, deadline, cancellationToken).ConfigureAwait(false);
                await Transport.ServingLocallyAsync(Node, deadline, cancellationToken).ConfigureAwait(false);
                operation = _replica.ReadSlotsAsync(slots, contiguousTail, clampedBytes, CancellationToken.None);
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

            return await AwaitOperationAsync(operation, lease, held: default, ReleaseSlots, cancellationToken).ConfigureAwait(false);
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
        private readonly NodeBudgets _budgets;
        private readonly CallBudget _writeLane;
        private readonly CallBudget _readLane;
        private readonly CallBudget _controlLane;

        public RemoteReplica(OrleansLedgerReplicaTransport transport, NodeId node, LedgerId ledger, int windowSize, ILedgerStoreGrain grain)
            : base(transport, node)
        {
            _ledger = ledger;
            _windowSize = windowSize;
            _grain = grain;
            _budgets = transport.Budgets(node);
            _writeLane = _budgets.Write;
            _readLane = _budgets.Read;
            _controlLane = _budgets.Control;
        }

        public override async ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            CheckWrite(slot, payload);
            var deadline = Transport.DeadlineFromNow();
            var backoff = FirstBackoff;
            while (true)
            {
                var activeSilo = Transport.EnsureRemoteActive(Node, _budgets);
                TimeSpan delay;
                using (await _writeLane.EnterLeaseAsync(payload.Length, deadline, cancellationToken).ConfigureAwait(false))
                {
                    RemoteReplicaWriteOutcome answer;
                    try
                    {
                        // Retain a zero-copy WireArcBuffer lease over payload's pooled pages. Orleans' proxy calls
                        // WireArcBufferCopier.DeepCopy synchronously to retain an independent lease for the outbound
                        // message; WireArcBufferCodec consumes it on remote serialization (or the callee consumes it on
                        // a local call), and releaseOnEnd disposes it if the message is dropped before serialization (OR-I18).
                        using var wirePayload = WireArcBuffer.Retain(payload);
                        var request = new LedgerReplicaWriteRequest
                        {
                            Version = LedgerReplicaProtocol.Version,
                            Ledger = _ledger.Value,
                            WindowSize = _windowSize,
                            Slot = slot.Value,
                            ContiguousTail = contiguousTail.Value,
                            Recovery = recovery,
                            Payload = wirePayload,
                        };
                        Task<RemoteReplicaWriteOutcome> call;
                        WireArcBuffer? proxyCopy = null;
                        try
                        {
                            call = _grain.WriteAsync(request, cancellationToken);
                            proxyCopy = wirePayload.DetachLastCopy();
                        }
                        catch
                        {
                            wirePayload.DetachLastCopy()?.Dispose();
                            throw;
                        }

                        var completed = false;
                        try
                        {
                            answer = cancellationToken.CanBeCanceled
                                ? await call.WaitAsync(cancellationToken).ConfigureAwait(false)
                                : await call.ConfigureAwait(false);
                            completed = true;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            ObserveAbandoned(call, abandoned: null, releaseOnEnd: proxyCopy);
                            throw;
                        }
                        catch
                        {
                            completed = true;
                            throw;
                        }
                        finally
                        {
                            if (completed)
                            {
                                proxyCopy?.Dispose();
                            }
                        }
                    }
                    catch (Exception failure)
                    {
                        ThrowCallFailure(failure, activeSilo, cancellationToken);
                        throw new UnreachableException();
                    }

                    if (answer.Value is NotThisNode)
                    {
                        Transport._invalidate();
                    }
                    else if (answer.Value is ReplicaCallRefused refused)
                    {
                        Transport.ObserveStoppingRefusal(_budgets, activeSilo, refused);
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

        public override async ValueTask<ReplicaRangeRead> ReadRangeAsync(Slot from, Slot below, Slot contiguousTail, int maxSlots, int maxBytes, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LedgerReplicaOperations.ValidateReadRange(from, below, maxSlots, maxBytes);
            if (from == below)
            {
                return ReplicaRangeBatch.Empty(from);
            }

            var reserved = Math.Min(maxBytes, Transport.Limits.PayloadLimit);
            var deadline = Transport.DeadlineFromNow();
            var request = new LedgerReplicaReadRangeRequest
            {
                Version = LedgerReplicaProtocol.Version,
                Ledger = _ledger.Value,
                WindowSize = _windowSize,
                From = from.Value,
                Below = below.Value,
                ContiguousTail = contiguousTail.Value,
                MaxSlots = maxSlots,
                MaxBytes = reserved,
            };
            var backoff = FirstBackoff;
            while (true)
            {
                var activeSilo = Transport.EnsureRemoteActive(Node, _budgets);
                TimeSpan delay;
                using (await _readLane.EnterLeaseAsync(reserved, deadline, cancellationToken).ConfigureAwait(false))
                {
                    RemoteReplicaRangeRead answer;
                    try
                    {
                        answer = await UntilCancelledAsync(_grain.ReadRangeAsync(request, cancellationToken), ReleaseRangeBatch, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception failure)
                    {
                        ThrowCallFailure(failure, activeSilo, cancellationToken);
                        throw new UnreachableException();
                    }

                    if (answer.Value is NotThisNode)
                    {
                        Transport._invalidate();
                    }
                    else if (answer.Value is ReplicaCallRefused refused)
                    {
                        Transport.ObserveStoppingRefusal(_budgets, activeSilo, refused);
                    }

                    if (answer.Value is not ReplicaWriteOutcome.Backpressure || !TryNextBackoff(deadline, backoff, out delay))
                    {
                        return LedgerReplicaCallResults.ReadRange(Node, answer, from, maxSlots, reserved);
                    }
                }

                await Task.Delay(delay, Transport._timeProvider, cancellationToken).ConfigureAwait(false);
                backoff = DoubleBackoff(backoff);
            }
        }

        public override async ValueTask<ReplicaSlotsRead> ReadSlotsAsync(ReadOnlyMemory<Slot> slots, Slot contiguousTail, int maxBytes, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            LedgerReplicaOperations.ValidateReadSlots(slots.Span, maxBytes);
            if (slots.IsEmpty)
            {
                return ReplicaSlotsBatch.Empty;
            }

            if (slots.Length > Transport.Limits.PayloadLimit / sizeof(long))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"A replica slots read of {slots.Length} slots passes the replica payload limit of {Transport.Limits.PayloadLimit}."),
                    nameof(slots));
            }

            var reserved = Math.Min(maxBytes, Transport.Limits.PayloadLimit);
            var deadline = Transport.DeadlineFromNow();
            var rawSlots = new long[slots.Length];
            var span = slots.Span;
            for (var i = 0; i < span.Length; i++)
            {
                rawSlots[i] = span[i].Value;
            }

            var request = new LedgerReplicaReadSlotsRequest
            {
                Version = LedgerReplicaProtocol.Version,
                Ledger = _ledger.Value,
                WindowSize = _windowSize,
                Slots = rawSlots,
                ContiguousTail = contiguousTail.Value,
                MaxBytes = reserved,
            };
            var backoff = FirstBackoff;
            while (true)
            {
                var activeSilo = Transport.EnsureRemoteActive(Node, _budgets);
                TimeSpan delay;
                using (await _readLane.EnterLeaseAsync(reserved, deadline, cancellationToken).ConfigureAwait(false))
                {
                    RemoteReplicaSlotsRead answer;
                    try
                    {
                        answer = await UntilCancelledAsync(_grain.ReadSlotsAsync(request, cancellationToken), ReleaseSlotsBatch, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception failure)
                    {
                        ThrowCallFailure(failure, activeSilo, cancellationToken);
                        throw new UnreachableException();
                    }

                    if (answer.Value is NotThisNode)
                    {
                        Transport._invalidate();
                    }
                    else if (answer.Value is ReplicaCallRefused refused)
                    {
                        Transport.ObserveStoppingRefusal(_budgets, activeSilo, refused);
                    }

                    if (answer.Value is not ReplicaWriteOutcome.Backpressure || !TryNextBackoff(deadline, backoff, out delay))
                    {
                        return LedgerReplicaCallResults.ReadSlots(Node, answer, slots.Span, reserved);
                    }
                }

                await Task.Delay(delay, Transport._timeProvider, cancellationToken).ConfigureAwait(false);
                backoff = DoubleBackoff(backoff);
            }
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
                var activeSilo = Transport.EnsureRemoteActive(Node, _budgets);
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
                        ThrowCallFailure(failure, activeSilo, cancellationToken);
                        throw new UnreachableException();
                    }

                    if (answer.Value is NotThisNode)
                    {
                        Transport._invalidate();
                    }
                    else if (answer.Value is ReplicaCallRefused refused)
                    {
                        Transport.ObserveStoppingRefusal(_budgets, activeSilo, refused);
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
                    var activeSilo = Transport.EnsureRemoteActive(Node, _budgets);
                    TimeSpan delay;
                    using (await _readLane.EnterLeaseAsync(reserved, deadline, cancellationToken).ConfigureAwait(false))
                    {
                        RemoteReplicaRead answer;
                        try
                        {
                            answer = await UntilCancelledAsync(_grain.ReadAsync(request, cancellationToken), ReleaseData, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception failure)
                        {
                            ThrowCallFailure(failure, activeSilo, cancellationToken);
                            throw new UnreachableException();
                        }

                        if (answer.Value is NotThisNode)
                        {
                            Transport._invalidate();
                        }
                        else if (answer.Value is ReplicaCallRefused refused)
                        {
                            Transport.ObserveStoppingRefusal(_budgets, activeSilo, refused);
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

        private void ThrowCallFailure(Exception failure, SiloAddress? activeSilo, CancellationToken cancellationToken)
        {
            Transport.ObserveCallFailure(_budgets, activeSilo, failure);
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
}
