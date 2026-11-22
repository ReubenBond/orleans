using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// Reaches the ledger replicas on simulated LedgerStore nodes from one client node across Clockwork's network: the
/// replica transport behind <see cref="ILedgerReplicaTransport"/> (docs/spec/verification.md §7.2).
/// </summary>
/// <remarks>
/// <para>
/// Every replica operation is one request to the replica's node through <see cref="SimulatedRpc"/>, carrying the
/// operation's arguments unchanged (LR-I9), so it meets the network's partitions, loss, duplication, delay, and
/// reordering, the node's pauses, and its crashes. A node which does not answer within the deadline fails the operation
/// with <see cref="TimeoutException"/>, as an unavailable node does (ledger-replicas.md §2.2), and the caller's cancellation
/// ends its wait while the request may still land (§6.3). A node's handle, which <see cref="Node"/> returns, lists and
/// drops its replicas on the node's own scheduler lane, as the node's reconciliation does (§2.3).
/// </para>
/// <para>
/// Each node's <see cref="ReplicaAudit"/> records what the clients asked of its replicas and what its running store
/// acknowledged, which <see cref="LedgerStoreInvariants"/> checks the node against, and each node logs every answer it
/// gives, with its drops and restarts, in the run's <see cref="LedgerTrace"/>. The ledger and the replicas are thin
/// adapters over the <see cref="ILedgerReplica"/> contract, so a change to the contract changes only this file.
/// </para>
/// <para>
/// A replica counts each exchange from its call until the caller's task completes, with the answer, at the deadline, or
/// at cancellation, and a check after every step requires that a replica is disposed only once no exchange with it is in
/// flight (ledger-replicas.md §6.3).
/// </para>
/// </remarks>
public sealed class SimulatedLedgerReplicaTransport : ILedgerReplicaTransport
{
    private readonly Dictionary<NodeId, (LedgerStoreNode Node, ReplicaAudit Audit)> _nodes;
    private readonly List<Action<ReplicaServing>> _servingObservers = [];
    private string? _disposalViolation;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulatedLedgerReplicaTransport"/> class.
    /// </summary>
    /// <param name="environment">The simulation.</param>
    /// <param name="client">The node whose requests this instance sends.</param>
    /// <param name="nodes">The nodes, each started at least once so its identity exists.</param>
    /// <param name="deadline">How long a request waits for its answer.</param>
    public SimulatedLedgerReplicaTransport(
        SimulationEnvironment environment,
        SimulationNode client,
        IEnumerable<LedgerStoreNode> nodes,
        TimeSpan deadline)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(nodes);
        Environment = environment;
        Client = client;
        Nodes = [.. nodes];
        _nodes = Nodes.ToDictionary(static node => node.NodeId, static node => (node, new ReplicaAudit()));
        Deadline = deadline;
        LedgerTrace = new LedgerTrace(environment);
        foreach (var node in Nodes)
        {
            node.OnRecovered(store => LedgerTrace.RestartedAsync(node, store));
            node.OnDamaged(() => LedgerTrace.Damaged(node));
        }

        environment.Invariants.Add(
            Invariant($"{client.NetworkAddress}: a replica is disposed only once no exchange with it is in flight (ledger-replicas §6.3)"),
            () => _disposalViolation);
    }

    /// <summary>
    /// Gets the node whose requests this instance sends.
    /// </summary>
    public SimulationNode Client { get; }

    /// <summary>
    /// Gets the nodes this instance reaches.
    /// </summary>
    public IReadOnlyList<LedgerStoreNode> Nodes { get; }

    /// <summary>
    /// Gets how long a request waits for its answer.
    /// </summary>
    public TimeSpan Deadline { get; }

    /// <summary>
    /// Gets the log of the ledger protocol the nodes serve and the clients run, and its checks.
    /// </summary>
    public LedgerTrace LedgerTrace { get; }

    internal SimulationEnvironment Environment { get; }

    /// <summary>
    /// Returns the ledger of <paramref name="node"/>'s replicas.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>The ledger.</returns>
    public ReplicaAudit Audit(LedgerStoreNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Find(node.NodeId).Audit;
    }

    /// <inheritdoc/>
    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
        Open(node, ledger, options, Client.NetworkAddress);

    /// <inheritdoc/>
    public async ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default)
    {
        if (ledgers.IsEmpty)
        {
            throw new ArgumentException("A heartbeat names at least one ledger.", nameof(ledgers));
        }

        var ids = ledgers.ToArray();
        var (target, _) = Find(node);
        var reply = await Environment.Rpc.CallAsync(
            Client,
            target.Node,
            () => target.ServeAsync(store => store.HeartbeatReplicasAsync(ids, CancellationToken.None)),
            Deadline,
            discard: null,
            cancellationToken);
        return new ReplicaHeartbeatRead(reply);
    }

    /// <summary>
    /// Opens a handle over <paramref name="node"/>'s replica of <paramref name="ledger"/> whose requests the node logs as
    /// <paramref name="client"/>'s.
    /// </summary>
    /// <param name="node">The node identity.</param>
    /// <param name="ledger">The ledger ID.</param>
    /// <param name="options">The replica's options.</param>
    /// <param name="client">The client the ledger trace names.</param>
    /// <returns>The replica.</returns>
    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options, string client)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(client);
        var (target, audit) = Find(node);
        audit.Open(ledger, options);
        return new SimulatedLedgerReplica(this, target, audit, ledger, options, client);
    }

    /// <summary>
    /// Opens <paramref name="client"/>'s handle over every node's replica of <paramref name="ledger"/>, each wrapped so the
    /// ledger trace logs the client's requests and answers.
    /// </summary>
    /// <param name="ledger">The ledger ID.</param>
    /// <param name="options">The replicas' options.</param>
    /// <param name="client">The client.</param>
    /// <returns>The replicas, in the nodes' order.</returns>
    public IReadOnlyList<ILedgerReplica> OpenTraced(LedgerId ledger, LedgerReplicaOptions options, string client) =>
        [.. Nodes.Select(node => LedgerTrace.Traced(Open(node.NodeId, ledger, options, client), client, node.Address, ledger))];

    /// <summary>
    /// Opens <paramref name="client"/>'s <see cref="Ledger"/> of <paramref name="ledger"/> over every node's replica,
    /// traced as <see cref="OpenTraced"/> traces them, on the simulation's clock, whose recoveries log each answer they
    /// handle in the ledger trace: one per client, as one per process would be.
    /// </summary>
    /// <param name="ledger">The ledger ID.</param>
    /// <param name="options">The ledger's options.</param>
    /// <param name="quorum">The quorum <c>Q</c>.</param>
    /// <param name="client">The client.</param>
    /// <returns>The <see cref="Ledger"/>, which owns its replicas.</returns>
    internal Ledger OpenLedger(LedgerId ledger, LedgerReplicaOptions options, int quorum, string client) =>
        OpenLedger(ledger, OpenQuorum(ledger, options, quorum, client));

    /// <summary>
    /// Opens the <see cref="Ledger"/> of <paramref name="ledger"/> over <paramref name="quorum"/>, which
    /// <see cref="OpenQuorum"/> opened, whose recoveries log each answer they handle in the ledger trace.
    /// </summary>
    /// <param name="ledger">The ledger ID.</param>
    /// <param name="quorum">The quorum, which the ledger owns.</param>
    /// <returns>The <see cref="Ledger"/>.</returns>
    internal Ledger OpenLedger(LedgerId ledger, LedgerQuorum quorum)
    {
        var opened = new Ledger(ledger, quorum, new LedgerLocation.Nodes([.. Nodes.Select(static node => node.NodeId)]));
        LedgerTrace.Register(opened, [.. Nodes.Select(static node => node.Address)]);
        return opened;
    }

    /// <summary>
    /// Opens <paramref name="client"/>'s <see cref="LedgerQuorum"/> of <paramref name="ledger"/> over every node's replica,
    /// traced as <see cref="OpenTraced"/> traces them, on the simulation's clock, whose recoveries log each answer they
    /// handle in the ledger trace (<see cref="LedgerTrace.RecoveryObserver"/>).
    /// </summary>
    /// <param name="ledger">The ledger ID.</param>
    /// <param name="options">The ledger's options.</param>
    /// <param name="quorum">The quorum <c>Q</c>.</param>
    /// <param name="client">The client.</param>
    /// <returns>The <see cref="LedgerQuorum"/>, which owns its replicas.</returns>
    internal LedgerQuorum OpenQuorum(LedgerId ledger, LedgerReplicaOptions options, int quorum, string client) =>
        new(OpenTraced(ledger, options, client), quorum, options, SimulationEnvironment.Clock, recoveryObserver: LedgerTrace.RecoveryObserver);

    /// <summary>
    /// Returns the handle of <paramref name="node"/>, whose listings and drops run on the node's own scheduler lane,
    /// without crossing the network, as the node's reconciliation lists and drops its own replicas (ledger-replicas.md §2.3).
    /// Its drops are recorded in the node's ledger and the ledger trace.
    /// </summary>
    /// <param name="node">The node identity.</param>
    /// <returns>The node's handle.</returns>
    public ILedgerStore Node(NodeId node)
    {
        var (target, audit) = Find(node);
        return new SimulatedLedgerStore(target, audit, LedgerTrace);
    }

    /// <summary>
    /// Records that a replica was disposed with exchanges in flight; the first such disposal is the violation the check
    /// reports.
    /// </summary>
    internal void DisposedInFlight(string violation) => _disposalViolation ??= violation;

    /// <summary>
    /// Calls <paramref name="observer"/> as a node's running store begins to serve each delivery of a replica request, on
    /// the node's lane, before the store sees the request, so a fault injector can crash the node while it serves it, or
    /// hold its answer (<see cref="ReplicaServing.HoldAnswer"/>).
    /// </summary>
    /// <param name="observer">The observer.</param>
    public void OnServing(Action<ReplicaServing> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _servingObservers.Add(observer);
    }

    /// <summary>
    /// Gets a value indicating whether an observer watches each delivery a node begins to serve.
    /// </summary>
    internal bool ObservesServing => _servingObservers.Count > 0;

    /// <summary>
    /// Tells the serving observers that <paramref name="serving"/> begins.
    /// </summary>
    internal void BeginServing(ReplicaServing serving)
    {
        foreach (var observer in _servingObservers)
        {
            observer(serving);
        }
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private (LedgerStoreNode Node, ReplicaAudit Audit) Find(NodeId node) =>
        _nodes.TryGetValue(node, out var target)
            ? target
            : throw new ArgumentException(Invariant($"No simulated node has identity {node}."), nameof(node));
}

/// <summary>
/// One delivery of a replica request which a node's running store serves, as <see cref="SimulatedLedgerReplicaTransport.OnServing"/>
/// reports it.
/// </summary>
/// <param name="node">The node.</param>
/// <param name="client">The client which sent the request.</param>
/// <param name="ledger">The ledger.</param>
/// <param name="operation">The operation.</param>
/// <param name="address">The address the request names, if any.</param>
public sealed class ReplicaServing(LedgerStoreNode node, string client, LedgerId ledger, LedgerOperation operation, long? address)
{
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets the node.
    /// </summary>
    public LedgerStoreNode Node { get; } = node;

    /// <summary>
    /// Gets the client which sent the request.
    /// </summary>
    public string Client { get; } = client;

    /// <summary>
    /// Gets the ledger.
    /// </summary>
    public LedgerId Ledger { get; } = ledger;

    /// <summary>
    /// Gets the operation.
    /// </summary>
    public LedgerOperation Operation { get; } = operation;

    /// <summary>
    /// Gets the address the request names, if any.
    /// </summary>
    public long? Address { get; } = address;

    /// <summary>
    /// Gets a value indicating whether the store completed the request, with its answer or a failure.
    /// </summary>
    public bool IsComplete { get; private set; }

    /// <summary>
    /// Gets a task which completes once the store completed the request, with its answer or a failure.
    /// </summary>
    public Task Completed => _completed.Task;

    /// <summary>
    /// Gets the task the node's answer waits for, once the store gives it, if <see cref="HoldAnswer"/> held it.
    /// </summary>
    internal Task? Release { get; private set; }

    /// <summary>
    /// Holds the answer the store gives until <paramref name="release"/> completes, as a node which has not yet sent an
    /// answer holds it: the store's record is durable and published once it answers, so the node's other answers may
    /// show it, and a crash of the node's process meanwhile discards the answer, so the request lands without one.
    /// </summary>
    /// <param name="release">The task whose completion lets the answer go.</param>
    /// <exception cref="InvalidOperationException">The request is a read, whose answer owns its payload.</exception>
    public void HoldAnswer(Task release)
    {
        ArgumentNullException.ThrowIfNull(release);
        Release = Operation is LedgerOperation.Read
            ? throw new InvalidOperationException("A read's answer owns its payload, which a crash would leave unreleased.")
            : release;
    }

    /// <summary>
    /// Records that the store completed the request.
    /// </summary>
    internal void Complete()
    {
        IsComplete = true;
        _ = _completed.TrySetResult();
    }
}

/// <summary>
/// One simulated node's handle: its listings and drops run on the node's scheduler lane, and its drops are recorded in
/// the node's ledger and the ledger trace.
/// </summary>
internal sealed class SimulatedLedgerStore(LedgerStoreNode node, ReplicaAudit audit, LedgerTrace trace) : ILedgerStore
{
    public NodeId NodeId => node.NodeId;

    public async ValueTask<IReadOnlyList<LedgerId>> ListReplicasAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await node.ServeLocallyAsync(static store => store.ListReplicasAsync(CancellationToken.None)).WaitAsync(cancellationToken);
    }

    public async ValueTask DropReplicaAsync(LedgerId ledger, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await node.DropReplicaAsync(ledger, audit, () => trace.Dropped(node, ledger)).WaitAsync(cancellationToken);
    }
}

/// <summary>
/// What the clients asked of one node's replicas and what its running store acknowledged (ledgerstore.md §5).
/// </summary>
/// <remarks>
/// A request is recorded when it is sent, whether or not it lands. An acknowledgement is recorded when the store which
/// serves it acknowledges it and still runs, whether or not its answer reaches the client: it is what the store has
/// promised. An entry is acknowledged by a write answered written, a writer's or a recovery's, after which the address
/// holds DATA (LR-I1). A writer write, a recovery write, or a seal creates the replica it reaches (LR-I7), so each is
/// also a requested creation, and its answer an acknowledged one. The answers which report the replica's seal, a writer
/// write's and a contiguous-tail read's, are recorded in the order the store gives them (LS-I11, LR-I8).
/// </remarks>
public sealed class ReplicaAudit
{
    private readonly HashSet<LedgerId> _requestedCreates = [];
    private readonly HashSet<LedgerId> _acknowledgedCreates = [];
    private readonly HashSet<LedgerId> _requestedDrops = [];
    private readonly HashSet<LedgerId> _acknowledgedDrops = [];
    private readonly Dictionary<LedgerId, LedgerReplicaOptions> _options = [];
    private readonly HashSet<(LedgerId Ledger, long Address)> _entries = [];
    private readonly List<(LedgerId Ledger, bool IsSealed, string Answer)> _sealAnswers = [];

    /// <summary>
    /// Gets the ledgers whose creation a client requested.
    /// </summary>
    public IReadOnlySet<LedgerId> RequestedCreates => _requestedCreates;

    /// <summary>
    /// Gets the ledgers whose creation the store acknowledged.
    /// </summary>
    public IReadOnlySet<LedgerId> AcknowledgedCreates => _acknowledgedCreates;

    /// <summary>
    /// Gets the ledgers whose drop a client requested.
    /// </summary>
    public IReadOnlySet<LedgerId> RequestedDrops => _requestedDrops;

    /// <summary>
    /// Gets the ledgers whose drop the store acknowledged.
    /// </summary>
    public IReadOnlySet<LedgerId> AcknowledgedDrops => _acknowledgedDrops;

    /// <summary>
    /// Gets the store's answers which report a replica's seal, in the order it gave them: whether each reported the
    /// replica sealed, and what it answered.
    /// </summary>
    public IReadOnlyList<(LedgerId Ledger, bool IsSealed, string Answer)> SealAnswers => _sealAnswers;

    /// <summary>
    /// Returns every acknowledged DATA entry, by ledger and address.
    /// </summary>
    /// <returns>The entries, ordered by ledger and address.</returns>
    public IReadOnlyList<(LedgerId Ledger, Slot Address)> AcknowledgedEntries() =>
        [
            .. _entries
                .OrderBy(static entry => entry.Ledger.Value)
                .ThenBy(static entry => entry.Address)
                .Select(static entry => (entry.Ledger, new Slot(entry.Address))),
        ];

    /// <summary>
    /// Returns the options a client opened <paramref name="ledger"/>'s replica with.
    /// </summary>
    /// <param name="ledger">The ledger ID.</param>
    /// <returns>The options.</returns>
    public LedgerReplicaOptions Options(LedgerId ledger) => _options[ledger];

    internal void Open(LedgerId ledger, LedgerReplicaOptions options) => _options[ledger] = options;

    internal void RequestCreate(LedgerId ledger) => _ = _requestedCreates.Add(ledger);

    internal void AcknowledgeCreate(LedgerId ledger) => _ = _acknowledgedCreates.Add(ledger);

    internal void RequestDrop(LedgerId ledger) => _ = _requestedDrops.Add(ledger);

    internal void AcknowledgeDrop(LedgerId ledger)
    {
        _ = _acknowledgedDrops.Add(ledger);
        _ = _entries.RemoveWhere(entry => entry.Ledger == ledger);
    }

    /// <summary>
    /// Records an acknowledged DATA entry, which no later write replaces (LR-I1).
    /// </summary>
    internal void Acknowledge(LedgerId ledger, Slot slot) => _ = _entries.Add((ledger, slot.Value));

    /// <summary>
    /// Records an answer which reports the replica's seal: a writer write's, sealed when it answered
    /// <see cref="ReplicaAnswers.Sealed"/>, or a contiguous-tail read's.
    /// </summary>
    internal void AcknowledgeSealAnswer(LedgerId ledger, bool isSealed, string answer) => _sealAnswers.Add((ledger, isSealed, answer));
}

/// <summary>
/// One replica on a simulated node as a client reaches it, each of whose operations is a request to that node, recording
/// in the node's ledger what the node's store acknowledges and logging the node's answer in the ledger trace.
/// </summary>
/// <remarks>
/// A request carries its payload's bytes, as a serialized message does, because the node may serve it after the
/// caller's deadline has returned the caller's buffer; each delivery wraps its own buffer around them and releases it
/// once the store has completed the write. A read's payload belongs to the caller once delivered, and is released when
/// the answer is lost.
/// </remarks>
internal sealed class SimulatedLedgerReplica(
    SimulatedLedgerReplicaTransport nodes,
    LedgerStoreNode node,
    ReplicaAudit audit,
    LedgerId ledger,
    LedgerReplicaOptions options,
    string client) : ILedgerReplica
{
    private readonly Dictionary<int, string> _inFlight = [];
    private int _exchanges;

    public ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
    {
        var call = nodes.LedgerTrace.TakeCall();
        var message = Serialize(payload);
        audit.RequestCreate(ledger);
        return Call(
            Describe(recovery ? "recovery write" : "write", slot),
            () => Serve(
                call,
                recovery ? LedgerOperation.RecoveryWrite : LedgerOperation.WriterWrite,
                slot,
                contiguousTail,
                async replica =>
                {
                    using var delivered = ArcBuffer.Wrap(message);
                    return await replica.WriteAsync(slot, delivered, contiguousTail, recovery, CancellationToken.None);
                },
                result =>
                {
                    if (result.Value is ReplicaWriteOutcome.Dropped)
                    {
                        return (LedgerTrace.DroppedAnswer, null);
                    }

                    audit.AcknowledgeCreate(ledger);
                    if (result.Value is ReplicaWriteOutcome.Written)
                    {
                        audit.Acknowledge(ledger, slot);
                    }

                    if (!recovery)
                    {
                        audit.AcknowledgeSealAnswer(ledger, result.Value is ReplicaWriteOutcome.Sealed, Describe("writer write", slot));
                    }

                    return (result.Case.ToString(), null);
                },
                discard: null),
            discard: null,
            cancellationToken);
    }

    public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
    {
        var call = nodes.LedgerTrace.TakeCall();
        return Call(
            Describe("read", slot),
            () => Serve(call, LedgerOperation.Read, slot, contiguousTail, replica => replica.ReadAsync(slot, contiguousTail, CancellationToken.None), Read, DisposePayload),
            DisposePayload,
            cancellationToken);
    }

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default)
    {
        var call = nodes.LedgerTrace.TakeCall();
        return Call(
            Describe("read", slot),
            () => Serve(
                call,
                LedgerOperation.Read,
                slot,
                contiguousTail,
                replica => replica.ReadAsync(slot, offset, length, contiguousTail, CancellationToken.None),
                Read,
                DisposePayload),
            DisposePayload,
            cancellationToken);
    }

    public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
    {
        var call = nodes.LedgerTrace.TakeCall();
        if (seal)
        {
            audit.RequestCreate(ledger);
        }

        return Call(
            seal ? "seal" : "tail read",
            () => Serve(
                call,
                seal ? LedgerOperation.Seal : LedgerOperation.TailRead,
                slot: null,
                contiguousTail,
                replica => replica.ReadTailAsync(contiguousTail, seal, CancellationToken.None),
                answer =>
                {
                    if (!answer.TryGetValue(out LedgerReplicaTail tail))
                    {
                        return (answer.Name, null);
                    }

                    if (seal)
                    {
                        audit.AcknowledgeCreate(ledger);
                    }

                    audit.AcknowledgeSealAnswer(ledger, tail.IsSealed, seal ? "seal" : "tail read");
                    return (LedgerTrace.TailAnswer, tail);
                },
                discard: null),
            discard: null,
            cancellationToken);
    }

    /// <summary>
    /// Records a violation unless every exchange with the replica has settled: its owner disposes it only once none is in
    /// flight (ledger-replicas.md §6.3).
    /// </summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (_inFlight.Count > 0)
        {
            nodes.DisposedInFlight(string.Create(
                CultureInfo.InvariantCulture,
                $"the replica of ledger {ledger} on {node.Address} was disposed with exchanges in flight: {string.Join(", ", _inFlight.OrderBy(static exchange => exchange.Key).Select(static exchange => exchange.Value))}"));
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Copies a DATA payload's bytes into the request, rejecting an empty one as the replica would (ledger-replicas.md §3.2).
    /// </summary>
    private static byte[] Serialize(ArcBuffer payload) =>
        payload.Length == 0
            ? throw new ArgumentException("A batch is non-empty.", nameof(payload))
            : payload.ToArray();

    private static (string Answer, LedgerReplicaTail? Tail) Read(ReplicaRead result) => (result.Case.ToString(), null);

    private static void DisposePayload(ReplicaRead result) => result.Release();

    private static string Describe(string operation, Slot slot) =>
        string.Create(CultureInfo.InvariantCulture, $"{operation} {slot.Value}");

    /// <summary>
    /// Serves one delivery of request <paramref name="call"/> on the node, logging the answer its running store gives,
    /// or its failure, in the ledger trace before the answer leaves the node. A contiguous-tail read first takes the tail
    /// the replica held when it arrived, which its answer must not fall below (LR-I6).
    /// </summary>
    private ValueTask<T> Serve<T>(
        long? call,
        LedgerOperation operation,
        Slot? slot,
        Slot contiguousTail,
        Func<ILedgerReplica, ValueTask<T>> request,
        Func<T, (string Answer, LedgerReplicaTail? Tail)> acknowledged,
        Action<T>? discard)
    {
        var trace = nodes.LedgerTrace;
        var floor = operation is LedgerOperation.TailRead or LedgerOperation.Seal ? trace.Floor(node, ledger) : null;
        return node.ServeReplicaAsync(
            ledger,
            options,
            nodes.ObservesServing ? ObservedAsync : request,
            result =>
            {
                var (answer, tail) = acknowledged(result);
                trace.Served(client, node, ledger, operation, slot?.Value, contiguousTail, answer, tail, floor, call);
            },
            discard,
            failure => trace.Served(client, node, ledger, operation, slot?.Value, contiguousTail, LedgerTrace.Describe(failure), tail: null, floor, call));

        async ValueTask<T> ObservedAsync(ILedgerReplica replica)
        {
            var serving = new ReplicaServing(node, client, ledger, operation, slot?.Value);
            nodes.BeginServing(serving);
            T answer;
            try
            {
                answer = await request(replica);
            }
            finally
            {
                serving.Complete();
            }

            if (serving.Release is { } release)
            {
                await release;
            }

            return answer;
        }
    }

    private async ValueTask<T> Call<T>(string operation, Func<ValueTask<T>> serve, Action<T>? discard, CancellationToken cancellationToken)
    {
        var exchange = Start(operation);
        try
        {
            return await nodes.Environment.Rpc.CallAsync(nodes.Client, node.Node, serve, nodes.Deadline, discard, cancellationToken);
        }
        finally
        {
            _ = _inFlight.Remove(exchange);
        }
    }

    private int Start(string operation)
    {
        var exchange = _exchanges++;
        _inFlight.Add(exchange, operation);
        return exchange;
    }
}
