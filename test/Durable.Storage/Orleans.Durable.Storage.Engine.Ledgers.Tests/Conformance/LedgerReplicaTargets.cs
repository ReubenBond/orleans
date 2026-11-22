using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Conformance;

/// <summary>
/// A replica under conformance test, the harness actions it supports, and how it classifies a failure the contract does
/// not define.
/// </summary>
internal abstract class LedgerReplicaTarget : IAsyncDisposable
{
    private readonly LedgerReplicaOverlaps _overlaps = new();

    /// <summary>
    /// Gets the IDs of the spec's ledgers, by index (<see cref="LedgerReplicaSpec.LedgerCount"/>).
    /// </summary>
    internal static LedgerId[] Ledgers { get; } = [LedgerReplicaTestData.Ledger, new(LedgerReplicaTestData.Ledger.Value + 1)];

    /// <summary>
    /// Returns the replica of ledger <paramref name="ledger"/>, by index, which the spec's operations on it run against.
    /// </summary>
    internal abstract ILedgerReplica Replica(int ledger);

    /// <summary>
    /// Runs <paramref name="request"/> through <see cref="ExecuteAsync"/>, adding the calls which overlapped it to its
    /// outcome.
    /// </summary>
    internal virtual Task<LedgerReplicaOutcome> RunAsync(object request) => _overlaps.RunAsync(request, () => ExecuteAsync(request));

    /// <summary>
    /// Runs <paramref name="request"/> against its ledger's replica, or against the node when it is a node action, and
    /// classifies its outcome.
    /// </summary>
    internal virtual Task<LedgerReplicaOutcome> ExecuteAsync(object request) => request switch
    {
        LedgerReplicaDropReplica drop => LedgerReplicaExecutor.ExecuteNodeAsync(() => Node.DropReplicaAsync(Ledgers[drop.Ledger]), Classify),
        LedgerReplicaListReplicas => LedgerReplicaExecutor.ExecuteListingAsync(ListReplicasAsync, Ledgers, Classify),
        LedgerReplicaRemove => LedgerReplicaExecutor.ExecuteNodeAsync(RemoveAsync, Classify),
        _ => LedgerReplicaExecutor.ExecuteAsync(Replica(LedgerReplicaSpec.LedgerOf(request)), request, Classify),
    };

    public abstract ValueTask DisposeAsync();

    /// <summary>
    /// Gets the handle through which the replicas' node lists and drops, the same suite over every provider (ledger-replicas.md
    /// §2.3).
    /// </summary>
    protected virtual ILedgerStore Node => throw new NotSupportedException();

    /// <summary>
    /// Lists the replicas: by default, the node's.
    /// </summary>
    protected virtual ValueTask<IReadOnlyList<LedgerId>> ListReplicasAsync() => Node.ListReplicasAsync();

    /// <summary>
    /// Removes the ledger: drops it at every node.
    /// </summary>
    protected virtual ValueTask RemoveAsync() => throw new NotSupportedException();

    /// <summary>
    /// Classifies a failure the contract does not define. By default none is expected.
    /// </summary>
    /// <returns>The outcome, or <see langword="null"/> when the failure is unexpected.</returns>
    protected virtual LedgerReplicaOutcome? Classify(Exception exception) => null;
}

/// <summary>
/// The memory provider's replicas of the spec's ledgers on one node, whose handle lists and drops them (ledger-replicas.md
/// §9.1).
/// </summary>
internal sealed class MemoryTarget : LedgerReplicaTarget
{
    private readonly InMemoryLedgerReplicaTransport _nodes = new();
    private readonly ILedgerReplica[] _replicas;

    /// <summary>
    /// Opens the replicas, each of which <paramref name="wrap"/> wraps when it is set.
    /// </summary>
    internal MemoryTarget(LedgerReplicaOptions window, Func<ILedgerReplica, ILedgerReplica>? wrap = null)
    {
        _replicas = [.. Ledgers.Select(ledger => _nodes.Open(LedgerReplicaTestData.Node, ledger, window)).Select(replica => wrap is null ? replica : wrap(replica))];
        Node = _nodes.Node(LedgerReplicaTestData.Node);
    }

    protected override ILedgerStore Node { get; }

    internal override ILedgerReplica Replica(int ledger) => _replicas[ledger];

    public override async ValueTask DisposeAsync()
    {
        foreach (var replica in _replicas)
        {
            await replica.DisposeAsync();
        }

        _nodes.Dispose();
    }
}

/// <summary>
/// The LedgerStore replicas of the spec's ledgers over a simulated node root, which the spec's restart crashes and
/// reopens; the store is its node's handle, which lists and drops them (ledger-replicas.md §9.2).
/// </summary>
/// <remarks>
/// A restart waits for the operations in flight and holds back new ones until the store reopens, so it is one atomic
/// step among them, as the spec has it: every durable answer survives it, and it rebuilds each retained contiguous tail
/// (LR-I6). A call counts as overlapping only from when it passes that gate, so a call held back behind a restart
/// overlaps neither the restart nor the calls the restart waited for, and the restart overlaps none.
/// </remarks>
internal sealed class LedgerStoreTarget : LedgerReplicaTarget
{
    private readonly SimulatedNodeRoot _root = LedgerStoreReplicaTests.Simulated();
    private readonly LedgerReplicaOptions _window;
    private readonly SemaphoreSlim _entry = new(1, 1);
    private LedgerStore? _store;
    private ILedgerReplica[] _replicas = [];
    private int _running;

    private LedgerStoreTarget(LedgerReplicaOptions window)
    {
        _window = window;
    }

    protected override ILedgerStore Node => _store!;

    internal override ILedgerReplica Replica(int ledger) => _replicas[ledger];

    /// <summary>
    /// Opens a store which holds no replica: each replica's first write, recovery write, or seal creates its replica (LS-I1).
    /// </summary>
    internal static async Task<LedgerStoreTarget> CreateAsync(LedgerReplicaOptions window)
    {
        var target = new LedgerStoreTarget(window);
        await target.OpenAsync(create: true);
        return target;
    }

    internal override async Task<LedgerReplicaOutcome> RunAsync(object request)
    {
        await _entry.WaitAsync();
        if (request is LedgerReplicaRestart)
        {
            try
            {
                await LedgerStoreTestExtensions.WaitUntilAsync(() => Volatile.Read(ref _running) == 0, "the operations in flight complete");
                return await base.RunAsync(request);
            }
            finally
            {
                _ = _entry.Release();
            }
        }

        _ = Interlocked.Increment(ref _running);
        _ = _entry.Release();
        try
        {
            return await base.RunAsync(request);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _running);
        }
    }

    internal override Task<LedgerReplicaOutcome> ExecuteAsync(object request) =>
        request is LedgerReplicaRestart restart
            ? LedgerReplicaExecutor.ExecuteNodeAsync(() => new ValueTask(RestartAsync(restart.OperatingSystem)), Classify)
            : base.ExecuteAsync(request);

    public override async ValueTask DisposeAsync()
    {
        await ReleaseReplicasAsync();
        await _store!.DisposeAsync();
        _entry.Dispose();
    }

    /// <summary>
    /// Crashes the store and reopens it from what the crash left: every acknowledged operation survives, its creation,
    /// seal, and drop included (LS-I1, LS-I2, LS-I11).
    /// </summary>
    private async Task RestartAsync(bool operatingSystem)
    {
        await ReleaseReplicasAsync();
        await _root.CrashAsync(_store!, operatingSystem);
        await OpenAsync(create: false);
    }

    private async Task OpenAsync(bool create)
    {
        _store = await _root.OpenAsync(create);
        _replicas = [.. Ledgers.Select(ledger => _store.OpenReplica(ledger, _window))];
    }

    private async ValueTask ReleaseReplicasAsync()
    {
        foreach (var replica in _replicas)
        {
            await replica.DisposeAsync();
        }
    }
}

/// <summary>
/// A <see cref="Ledger"/> of quorum <c>Q</c> over the memory replicas of <c>N</c> nodes, on a fake clock, into which
/// harness actions inject faults (ledger-protocol.md §8.3). The spec drives it as the composition through its writer's
/// writes, reads, seal checks, and recoveries; a <see cref="Ledger"/> sends no seal and no recovery write but its
/// recovery's, so the spec's caller sends it neither.
/// </summary>
/// <remarks>
/// <para>
/// A replica fails an operation before or after applying it, as a lost request or response does, holds it, as a slow
/// replica does, until a harness action releases it or the fake clock passes <see cref="HoldLimit"/>, or answers it
/// <c>Backpressure</c> without applying it, as an overloaded replica does. While a call
/// in which an injected fault fired is incomplete, the harness advances the clock, so a recovery's backoff before it
/// asks a replica again, and a held operation's hold limit, pass; a call in which none fired never sees the clock move.
/// </para>
/// <para>
/// A failure is unavailable, and an answer is <c>Backpressure</c>, only when every replica failure behind it is an
/// injected fault, and only when an injected fault fired during the call or another call overlapped it. Replicas which
/// all answer decide
/// every write, read, seal check, and recovery (ledger-protocol.md §8.3), so such a call which ran alone against them
/// succeeds. Removing the ledger drops it at every node, faults aside.
/// </para>
/// </remarks>
internal sealed class LedgerTarget : LedgerReplicaTarget, IConformanceEvents
{
    /// <summary>
    /// How long a replica holds an operation on its clock, unless a harness action releases it first.
    /// </summary>
    internal static readonly TimeSpan HoldLimit = FaultyLedgerReplica.HoldLimit;

    /// <summary>
    /// How far the harness advances the fake clock at each poll of a call in which an injected fault fired.
    /// </summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(1);

    private readonly FakeTimeProvider _time = new();
    private readonly ConcurrentQueue<string> _events = new();
    private readonly InMemoryLedgerReplicaTransport _nodes = new();
    private readonly NodeId[] _nodeIds;
    private readonly FaultyLedgerReplica[] _replicas;
#pragma warning disable CA2213 // The ledger owns and disposes its quorum; the target keeps it for its hedge counts.
    private readonly LedgerQuorum _quorum;
#pragma warning restore CA2213
    private readonly Ledger _ledger;
    private readonly LedgerAsReplica _ledgerAsReplica;
    private readonly Lock _lock = new();
    private readonly LedgerTargetCoverage? _coverage;
    private int _running;
    private int _overlaps;

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerTarget"/> class of quorum <paramref name="quorum"/> over
    /// <paramref name="replicaCount"/> replicas, which fail only on demand or, with <paramref name="chaos"/>, also at
    /// random from <paramref name="seed"/>; <paramref name="wrap"/> wraps each replica the composition sees; its process
    /// is on the node of replica <paramref name="preferred"/>, when set, which its reads below the tail ask first;
    /// <paramref name="mutant"/> builds a defect into its reads; and <paramref name="coverage"/> counts its hedges and
    /// fallbacks.
    /// </summary>
    internal LedgerTarget(
        LedgerReplicaOptions window,
        int replicaCount = 3,
        int quorum = 2,
        int seed = 0,
        bool chaos = false,
        Func<ILedgerReplica, ILedgerReplica>? wrap = null,
        int? preferred = null,
        LedgerReadMutant mutant = LedgerReadMutant.None,
        LedgerTargetCoverage? coverage = null)
    {
        _coverage = coverage;
        _nodeIds = [.. Enumerable.Range(0, replicaCount).Select(static _ => NodeId.New())];
        _replicas =
        [
            .. Enumerable.Range(0, replicaCount).Select(index => new FaultyLedgerReplica(
                index,
                _nodes.Open(_nodeIds[index], LedgerReplicaTestData.Ledger, window),
                _time,
                _events,
                chaos ? new Random(unchecked(seed * replicaCount + index)) : null)),
        ];
        wrap ??= static replica => replica;
        ILedgerReplica[] seen = [.. _replicas.Select(wrap)];
        _quorum = LedgerQuorum.ForNodes(
            LedgerReplicaTestData.Ledger,
            [.. _nodeIds],
            seen,
            quorum,
            window,
            _time,
            localNode: preferred is { } local ? _nodeIds[local] : null);
        // A map lists only odd replica sets (ledgers.md §4), and the front's location serves only its sender check against a
        // map, which this target never runs, so an even set is located by its first N - 1 nodes.
        _ledger = new Ledger(LedgerReplicaTestData.Ledger, _quorum, new LedgerLocation.Nodes([.. _nodeIds.Take(replicaCount - (1 - replicaCount % 2))]));
        _ledgerAsReplica = new LedgerAsReplica(_ledger, mutant switch
        {
            LedgerReadMutant.None => null,
            LedgerReadMutant.TrustsOneReplica => (slot, tail, cancellationToken) => seen[0].ReadAsync(slot, tail, cancellationToken),
            LedgerReadMutant.DecidesEverySlot => (slot, tail, cancellationToken) => ReadEachInTurnAsync(seen, slot, tail, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mutant)),
        });
    }

    private long FaultsFired => _replicas.Sum(static replica => replica.Fired);

    /// <summary>
    /// The <see cref="LedgerReadMutant.DecidesEverySlot"/> defect: reads every slot as decided, one replica at a time in
    /// the map's order, and fails rather than answering missing when none holds a batch.
    /// </summary>
    private static async ValueTask<ReplicaRead> ReadEachInTurnAsync(ILedgerReplica[] replicas, Slot slot, Slot contiguousTail, CancellationToken cancellationToken)
    {
        foreach (var replica in replicas)
        {
            var read = await replica.ReadAsync(slot, contiguousTail, cancellationToken);
            if (read.Case == ReplicaReadCase.Batch)
            {
                return read;
            }
        }

        throw new InvalidDataException("No replica holds the slot, which the read took as decided.");
    }

    /// <summary>
    /// Returns the composition, which serves one ledger, the spec's ledger 0.
    /// </summary>
    internal override ILedgerReplica Replica(int ledger) =>
        ledger == 0 ? _ledgerAsReplica : throw new ArgumentOutOfRangeException(nameof(ledger), ledger, "The composition serves one ledger.");

    internal override async Task<LedgerReplicaOutcome> ExecuteAsync(object request)
    {
        int overlaps;
        long fired;
        lock (_lock)
        {
            overlaps = _overlaps;
            if (++_running > 1)
            {
                _overlaps++;
            }

            fired = FaultsFired;
        }

        var held = _replicas.Select(static replica => replica.HeldFired).ToArray();
        var reads = _replicas.Select(static replica => replica.Reads).ToArray();
        var exhaustions = _quorum.ReadHedgeBudgetExhaustions;
        var started = _time.GetUtcNow();
        foreach (var replica in _replicas)
        {
            replica.AnswersSince = started;
        }

        LedgerReplicaOutcome outcome;
        bool unexplained;
        bool alone;
        try
        {
            var call = request is LedgerReplicaRecover recover
                ? LedgerReplicaExecutor.ExecuteRecoverAsync(() => _ledger.RecoverAsync(new Slot(recover.ContiguousTail)), Classify)
                : base.ExecuteAsync(request);
            while (!call.IsCompleted)
            {
                _ = await Task.WhenAny(call, Task.Delay(Poll, TimeProvider.System, CancellationToken.None));
                if (!call.IsCompleted && FaultsFired != fired)
                {
                    _time.Advance(Step);
                }
            }

            outcome = await call;
        }
        finally
        {
            lock (_lock)
            {
                alone = _overlaps == overlaps;
                unexplained = alone && FaultsFired == fired;
                _running--;
            }
        }

        if (alone && WaitedOutOneHold(request, fired, held, reads, exhaustions, started))
        {
            return outcome with
            {
                Kind = LedgerReplicaOutcomeKind.Unexpected,
                Failure = $"A read below the tail waited out one replica's hold rather than hedging it: {outcome}",
            };
        }

        return outcome.Kind is LedgerReplicaOutcomeKind.Unavailable or LedgerReplicaOutcomeKind.Backpressure && unexplained
            ? outcome with
            {
                Kind = LedgerReplicaOutcomeKind.Unexpected,
                Failure = $"{outcome.Kind} although it ran alone and no injected fault fired: {outcome.Failure}",
            }
            : outcome;
    }

    /// <summary>
    /// Returns whether <paramref name="request"/>, a read below the known contiguous tail which began at
    /// <paramref name="started"/> and during which the only faults fired were holds of one replica, decided later than
    /// one hedge delay per replica and one poll step while a replica stayed unasked and the hedge budget held: a read
    /// hedges a held replica once the hedge delay passes, and asks the next replica at once on an answer without a batch
    /// (ledger-protocol.md §8.4). It decides on the last answer a replica returned to a read asked since it began, so
    /// the clock's later steps, before the harness sees the call complete, do not count.
    /// </summary>
    private bool WaitedOutOneHold(object request, long fired, long[] held, long[] reads, long exhaustions, DateTimeOffset started)
    {
        var address = request switch
        {
            LedgerReplicaRead read => read.Address,
            LedgerReplicaReadRange range => range.Address,
            _ => long.MaxValue,
        };
        var heldNow = _replicas.Select((replica, index) => replica.HeldFired - held[index]).ToArray();
        return address < _ledger.KnownContiguousTail.Value
            && FaultsFired - fired == heldNow.Sum()
            && heldNow.Count(static count => count > 0) == 1
            && _replicas.Where((replica, index) => replica.Reads > reads[index]).Count() < _replicas.Length
            && _quorum.ReadHedgeBudgetExhaustions == exhaustions
            && Decided(started) - started > _replicas.Length * LedgerQuorum.ReadHedgeDelay + Step;
    }

    private DateTimeOffset Decided(DateTimeOffset started) =>
        _replicas.Select(static replica => replica.LastAnsweredAt).Where(at => at >= started).DefaultIfEmpty(started).Max();

    /// <summary>
    /// Injects <paramref name="fault"/> into the next operations of one replica.
    /// </summary>
    internal void Inject(ReplicaFault fault) => _replicas[fault.Replica].Inject(fault.Count, fault.Fault, fault.Operation);

    /// <summary>
    /// Releases every held operation and waits until each has applied.
    /// </summary>
    internal Task ReleaseAsync() => Task.WhenAll(_replicas.Select(static replica => replica.ReleaseAsync()));

    /// <summary>
    /// Clears every fault, so no more fire, and releases every held operation.
    /// </summary>
    internal Task HealAsync() => Task.WhenAll(_replicas.Select(static replica => replica.HealAsync()));

    public IReadOnlyList<string> TakeEvents()
    {
        var events = new List<string>();
        while (_events.TryDequeue(out var text))
        {
            events.Add(text);
        }

        return events;
    }

    public override async ValueTask DisposeAsync()
    {
        _coverage?.Record(_quorum.ReadHedges, _replicas.Sum(static replica => replica.MissingBelowTail));
        await HealAsync();
        await _ledgerAsReplica.DisposeAsync();
        await _ledger.DisposeAsync();
        _nodes.Dispose();
    }

    protected override async ValueTask RemoveAsync()
    {
        foreach (var node in _nodeIds)
        {
            await _nodes.Node(node).DropReplicaAsync(Ledgers[0]);
        }
    }

    /// <summary>
    /// Lists the ledgers any of the composition's nodes holds.
    /// </summary>
    protected override async ValueTask<IReadOnlyList<LedgerId>> ListReplicasAsync()
    {
        var listed = new SortedSet<LedgerId>();
        foreach (var node in _nodeIds)
        {
            listed.UnionWith(await _nodes.Node(node).ListReplicasAsync());
        }

        return [.. listed];
    }

    protected override LedgerReplicaOutcome? Classify(Exception exception) =>
        exception is AggregateException aggregate
        && aggregate.InnerExceptions.All(static inner => inner is InjectedFaultException)
            ? new(LedgerReplicaOutcomeKind.Unavailable, Failure: LedgerReplicaExecutor.Describe(exception))
            : null;
}

/// <summary>
/// What a run of <see cref="LedgerTarget"/>s reached: the hedges their reads below the tail sent, and the replicas which
/// answered such a read missing, past which the read fell back.
/// </summary>
internal sealed class LedgerTargetCoverage
{
    private long _hedges;
    private long _missingFallbacks;

    internal long Hedges => Interlocked.Read(ref _hedges);

    internal long MissingFallbacks => Interlocked.Read(ref _missingFallbacks);

    internal void Record(long hedges, long missingFallbacks)
    {
        _ = Interlocked.Add(ref _hedges, hedges);
        _ = Interlocked.Add(ref _missingFallbacks, missingFallbacks);
    }
}

/// <summary>
/// A defect a test builds into a <see cref="LedgerTarget"/>'s reads from the ledger's parts, to show that the composition's
/// spec fails it (LP-I10, ledger-protocol.md §8.3).
/// </summary>
internal enum LedgerReadMutant
{
    /// <summary>The ledger's own reads.</summary>
    None,

    /// <summary>A read answers its first replica's answer alone, a missing answer included.</summary>
    TrustsOneReplica,

    /// <summary>
    /// A read takes every slot as decided, at or above the known contiguous tail too, so it asks one replica at a time and
    /// fails, rather than answering missing, when no replica holds a batch.
    /// </summary>
    DecidesEverySlot,
}
