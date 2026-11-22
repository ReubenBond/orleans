using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Deterministic Clockwork simulation of whole-cluster drain-all before an instance-releasing stop on instance-local NVMe
/// (orleans.md §5.10, §5.14, §8, §9; row 4.8 B; OR-I2, OR-I4, OR-I5, OR-I12, OR-I19, LG-I11, JS-I1 – JS-I4):
/// <list type="bullet">
/// <item>Backs both <c>LedgerMap</c> and the node registry with fault-injected <see cref="SimulatedConditionalRegister"/> instances enforcing CAS, <c>OrdinalOccupied</c>, durable <c>DrainAllRevision</c>, and atomic <c>CompleteDrainAll</c>.</item>
/// <item>Exercises full whole-cluster drain-all under faults, phase-gated diskful node crash/restart, OR-I12 concurrent duplicate controller activation, controller failover without losing durable <c>DrainAllRevision</c>, stop &amp; empty-root restart at ordinals <c>0, 1, 2</c>, automatic rollback to <c>Nodes</c>, non-vacuous OR-I4 stale-controller rejection, OR-I5 placement invariant checks, and full history linearizability.</item>
/// <item>Exercises mid-Phase-1 cancel -&gt; automatic rollback to <c>Nodes</c> -&gt; resume -&gt; empty-root restart, and mixed diskful + diskless replacement where live reads/writes continue uninterrupted on the surviving diskless silo while diskful nodes are wiped and replaced.</item>
/// </list>
/// </summary>
public sealed class DrainAllSimulationTests
{
    private const int FirstSeed = 1;
    private const int SeedCount = 1;
    private const int ReplaySeed = 2;
    private const int CancelResumeSeed = 3;
    private const int MixedClusterSeed = 4;
    private const int DiskfulNodeCount = 3;
    private const int Window = 4;
    private const int MaxAppends = 4;
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan Retention = TimeSpan.FromMilliseconds(50);

    private enum Mode
    {
        FullRestart,
        ReplayRestart,
        CancelAndResume,
        MixedDisklessReplacement,
    }

    /// <summary>Gets the seed corpus.</summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    /// <summary>
    /// Whole-cluster drain-all under faults, phase-gated node crash/restart, OR-I12 duplicate controller activation,
    /// durable failover, stop &amp; empty-root restart at the same StatefulSet ordinals, and rollback to <c>Nodes</c>
    /// ledgers preserves linearizability and every map, placement (OR-I5), and retirement-gate (OR-I4) invariant.
    /// </summary>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void WholeClusterDrainAllSurvivesFaultsFailoverAndEmptyRootRestart(int seed) =>
        _ = SeededSimulation.Run(seed, static environment => SimulateAsync(environment, Mode.FullRestart));

    /// <summary>The drain-all simulation replays deterministically from its seed and artifact.</summary>
    [Fact]
    public void WholeClusterDrainAllReplaysDeterministically()
    {
        var run = SeededSimulation.RunAndReplay(ReplaySeed, static environment => SimulateAsync(environment, Mode.ReplayRestart));

        Assert.Contains(run.Trace, static entry => entry.Contains("drain-all verified", StringComparison.Ordinal));
    }

    /// <summary>
    /// Cancelling drain-all mid-Phase-1 clears durable <c>DrainAllRevision</c>, automatically rolls open ledgers back
    /// onto <c>Nodes</c>, and resumes cleanly to empty-root stop and restart without losing any acknowledged entry.
    /// </summary>
    [Fact]
    public void MidPhase1CancelRollsBackToNodesAndResumesCleanly() =>
        _ = SeededSimulation.Run(CancelResumeSeed, static environment => SimulateAsync(environment, Mode.CancelAndResume));

    /// <summary>
    /// In a mixed cluster (3 diskful + 1 diskless) under <see cref="LedgerPlacement.Nodes"/>, drain-all moves open
    /// ledgers onto the diskless silo in Phase 1, retires only the diskful nodes in Phase 2, keeps serving live reads and
    /// writes on the diskless silo while diskful nodes stop and wipe, and automatically rolls back onto fresh diskful
    /// nodes once they restart at ordinals <c>0, 1, 2</c>.
    /// </summary>
    [Fact]
    public void MixedDiskfulAndDisklessClusterReplacesDiskfulWithoutDowntime() =>
        _ = SeededSimulation.Run(MixedClusterSeed, static environment => SimulateAsync(environment, Mode.MixedDisklessReplacement));

    private static async Task SimulateAsync(SimulationEnvironment environment, Mode mode)
    {
        using var cancellation = new CancellationTokenSource();
        var network = environment.Network;
        network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        network.MaxJitter = TimeSpan.FromMilliseconds(3);
        network.MessageDropRate = 0.03;
        environment.Rpc.DuplicationRate = 0.03;

        var client = environment.Cluster.AddNode("client");
        var totalNodes = mode == Mode.MixedDisklessReplacement ? DiskfulNodeCount * 2 + 1 : DiskfulNodeCount * 2;
        var allNodes = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, totalNodes, cancellation.Token);
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, allNodes, Deadline);
        _ = LedgerStoreInvariants.Register(environment, replicas);

        await using var mapRegister = new SimulatedConditionalRegister(environment, "ledger-map")
        {
            UnknownOutcomeRate = 0.08,
            AppliedMismatchRate = 0.08,
            ReadFailureRate = 0.04,
        };
        await using var registryRegister = new SimulatedConditionalRegister(environment, "node-registry")
        {
            UnknownOutcomeRate = 0.08,
            AppliedMismatchRate = 0.08,
            ReadFailureRate = 0.04,
        };
        await using var objects = new SimulatedObjectStore(environment, "object-ledgers")
        {
            UnknownOutcomeRate = 0.08,
            RetriedAnswerRate = 0.08,
            ReadFailureRate = 0.04,
        };
        var objectLedgers = ObjectLedgerStore.Over(
            objects,
            new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.PerSlot, CacheBytes = 64 * 1024 },
            SimulationEnvironment.Clock);
        _ = await RetryAsync(() => objectLedgers.StoreRecordAsync(cancellation.Token).AsTask(), cancellation.Token);
        using var archive = new SimulatedArchiveStore(environment, "journals", maxAppends: MaxAppends, replaceRequestAppends: MaxAppends - 1)
        {
            StaleRate = 0.08,
        };

        await new Scenario(environment, replicas, mapRegister, registryRegister, objects, objectLedgers, archive, allNodes, mode, cancellation.Token).RunAsync();
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task<T> RetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (Exception failure) when (IsUnavailable(failure))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
            }
        }
    }

    private static bool IsUnavailable(Exception failure) =>
        failure is IOException or TimeoutException or NodeDownException;

    private sealed class ClientTransport(SimulatedLedgerReplicaTransport replicas, string client) : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            replicas.Open(node, ledger, options, client);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            replicas.HeartbeatAsync(node, ledgers, cancellationToken);
    }

    /// <summary>
    /// Conditional-register-backed node registry enforcing CAS, <c>OrdinalOccupied</c>, durable <c>DrainAllRevision</c>,
    /// and atomic <c>CompleteDrainAll</c> (matching <c>NodeRegistryCommitter</c> semantics over a real
    /// <see cref="SimulatedConditionalRegister"/>).
    /// </summary>
    private sealed class SimulatedRegistryStore(SimulatedConditionalRegister register)
    {
        private readonly Dictionary<ulong, RegisteredNodes> _history = new() { [0] = RegisteredNodes.Empty };

        public RegisteredNodes Latest { get; private set; } = RegisteredNodes.Empty;

        public bool TryGetRevision(ulong revision, out RegisteredNodes value) => _history.TryGetValue(revision, out value!);

        public async Task<RegisteredNodes> ReadAsync(CancellationToken cancellationToken)
        {
            using var entry = await register.ReadAsync(cancellationToken);
            var value = entry is null ? RegisteredNodes.Empty : Decode(entry.Value.ToArray());
            Record(value);
            return value;
        }

        public Task<RegisteredNodes> RegisterAsync(NodeId node, int? ordinal, bool diskless, CancellationToken cancellationToken) =>
            CommitAsync(
                current =>
                {
                    if (current.Nodes.ContainsKey(node))
                    {
                        return (false, current);
                    }

                    if (ordinal is { } requested && current.Nodes.Values.Any(entry => !entry.IsRetired && entry.Ordinal == requested))
                    {
                        throw new InvalidOperationException(Invariant($"OrdinalOccupied: ordinal {requested} is already held by an active node."));
                    }

                    var updated = current.Nodes.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);
                    updated[node] = new RegisteredNode(RetiredRevision: null, FailureDomain: null, Generation: 1, Ordinal: ordinal, Diskless: diskless);
                    return (true, new RegisteredNodes(current.Revision + 1, updated.ToFrozenDictionary(), HasObjectLedgerStore: true, current.DrainAllRevision));
                },
                cancellationToken);

        public async Task<ulong> StartDrainAllAsync(CancellationToken cancellationToken)
        {
            var value = await CommitAsync(
                current => !current.HasObjectLedgerStore || current.DrainAllRevision > 0
                    ? (false, current)
                    : (true, current with { Revision = current.Revision + 1, DrainAllRevision = current.Revision + 1 }),
                cancellationToken);
            if (!value.HasObjectLedgerStore)
            {
                throw new InvalidOperationException("Drain-all requires an object-ledger store recorded in CLNR (ObjectLedgersNotConfigured).");
            }

            return value.DrainAllRevision;
        }

        public async Task<bool> CancelDrainAllAsync(CancellationToken cancellationToken)
        {
            var cancelled = false;
            _ = await CommitAsync(
                current =>
                {
                    if (current.DrainAllRevision == 0)
                    {
                        return (false, current);
                    }

                    cancelled = true;
                    return (true, current with { Revision = current.Revision + 1, DrainAllRevision = 0 });
                },
                cancellationToken);
            return cancelled;
        }

        public async Task<bool> CompleteDrainAllAsync(ulong expectedDrainAllRevision, CancellationToken cancellationToken)
        {
            var completed = false;
            _ = await CommitAsync(
                current =>
                {
                    if (current.DrainAllRevision != expectedDrainAllRevision)
                    {
                        return (false, current);
                    }

                    var retiredAt = current.Revision + 1;
                    var updated = current.Nodes.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);
                    foreach (var (node, info) in updated.ToArray())
                    {
                        if (!info.IsRetired && !info.Diskless)
                        {
                            updated[node] = info with { RetiredRevision = retiredAt };
                        }
                    }

                    completed = true;
                    return (true, new RegisteredNodes(retiredAt, updated.ToFrozenDictionary(), current.HasObjectLedgerStore, DrainAllRevision: 0));
                },
                cancellationToken);
            return completed;
        }

        public Task<RegisteredNodes> RetireNodeAsync(NodeId node, CancellationToken cancellationToken) =>
            CommitAsync(
                current =>
                {
                    if (!current.Nodes.TryGetValue(node, out var existing) || existing.IsRetired)
                    {
                        return (false, current);
                    }

                    var retiredAt = current.Revision + 1;
                    var updated = current.Nodes.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);
                    updated[node] = existing with { RetiredRevision = retiredAt };
                    return (true, new RegisteredNodes(retiredAt, updated.ToFrozenDictionary(), current.HasObjectLedgerStore, current.DrainAllRevision));
                },
                cancellationToken);

        private async Task<RegisteredNodes> CommitAsync(Func<RegisteredNodes, (bool Write, RegisteredNodes Next)> transform, CancellationToken cancellationToken)
        {
            while (true)
            {
                MetaStoreVersion? version;
                RegisteredNodes current;
                try
                {
                    using var entry = await register.ReadAsync(cancellationToken);
                    version = entry?.Version;
                    current = entry is null ? RegisteredNodes.Empty : Decode(entry.Value.ToArray());
                    Record(current);
                }
                catch (Exception failure) when (IsUnavailable(failure))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    continue;
                }

                var (shouldWrite, next) = transform(current);
                if (!shouldWrite)
                {
                    return current;
                }

                var bytes = Encode(next);
                try
                {
                    var outcome = await register.WriteAsync(version, new ReadOnlySequence<byte>(bytes), cancellationToken);
                    var applied = outcome switch
                    {
                        RegisterWriteOutcome.Applied => true,
                        RegisterWriteOutcome.Mismatch => false,
                        null => throw new UnreachableException(),
                    };
                    if (applied)
                    {
                        Record(next);
                        return next;
                    }
                }
                catch (Exception failure) when (IsUnavailable(failure))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                }
            }
        }

        private void Record(RegisteredNodes value)
        {
            _history[value.Revision] = value;
            if (value.Revision >= Latest.Revision)
            {
                Latest = value;
            }
        }

        private static byte[] Encode(RegisteredNodes value)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(value.Revision);
            writer.Write(value.HasObjectLedgerStore);
            writer.Write(value.DrainAllRevision);
            writer.Write(value.Nodes.Count);
            foreach (var (node, info) in value.Nodes)
            {
                writer.Write(node.Value.ToByteArray());
                writer.Write(info.RetiredRevision.HasValue);
                writer.Write(info.RetiredRevision ?? 0ul);
                writer.Write(info.Generation);
                writer.Write(info.Ordinal.HasValue);
                writer.Write(info.Ordinal ?? 0);
                writer.Write(info.Diskless);
            }

            writer.Flush();
            return stream.ToArray();
        }

        private static RegisteredNodes Decode(byte[] data)
        {
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var revision = reader.ReadUInt64();
            var hasObjectLedgerStore = reader.ReadBoolean();
            var drainAllRevision = reader.ReadUInt64();
            var count = reader.ReadInt32();
            var nodes = new Dictionary<NodeId, RegisteredNode>(count);
            for (var index = 0; index < count; index++)
            {
                var id = new NodeId(new Guid(reader.ReadBytes(16)));
                var hasRetired = reader.ReadBoolean();
                var retiredRev = reader.ReadUInt64();
                var generation = reader.ReadInt32();
                var hasOrdinal = reader.ReadBoolean();
                var ordinal = reader.ReadInt32();
                var diskless = reader.ReadBoolean();
                nodes[id] = new RegisteredNode(hasRetired ? retiredRev : null, FailureDomain: null, generation, hasOrdinal ? ordinal : null, diskless);
            }

            return new RegisteredNodes(revision, nodes.ToFrozenDictionary(), hasObjectLedgerStore, drainAllRevision);
        }
    }

    private sealed class ControllerHost : IAsyncDisposable
    {
        private readonly LedgerMapCommitter _committer;

        public ControllerHost(
            ControlOptions options,
            LedgerMapView view,
            SimulatedConditionalRegister register,
            int writerId,
            bool adoptObjectLedgers)
        {
            WriterId = writerId;
            View = view;
            _committer = new LedgerMapCommitter(register.Writer(writerId), int.MaxValue, view);
            Controller = new ClusterController(
                options,
                view,
                _committer,
                ClusterArchive.Logs.Instance,
                SimulationEnvironment.Clock,
                NullLogger.Instance);
            if (adoptObjectLedgers)
            {
                Controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
            }
        }

        public int WriterId { get; }

        public ClusterController Controller { get; }

        public LedgerMapView View { get; }

        public ValueTask DisposeAsync() => _committer.DisposeAsync();
    }

    private sealed class NodeProcess : IAsyncDisposable
    {
        private bool _disposed;
        private long _reportSequence;

        public NodeProcess(
            string name,
            LedgerStoreNode storeNode,
            int? ordinal,
            bool diskless,
            LedgerMapView view,
            ILedgerReplicaTransport transport,
            ObjectLedgerStore objectStore,
            IArchiveStore archive,
            LedgerTrace trace)
        {
            Name = name;
            StoreNode = storeNode;
            Ordinal = ordinal;
            Diskless = diskless;
            View = view;
            Reader = new LedgerReader(view, transport, SimulationEnvironment.Clock, storeNode.NodeId) { Objects = objectStore };
            Registry = new LedgerWriterRegistry(Reader, 1 << 25, SmallSegments.Options, SimulationEnvironment.Clock, NullLoggerFactory.Instance, SmallSegments.Random(name));
            Executor = new TransitionExecutor(Registry, NullLogger<TransitionExecutor>.Instance);
            var options = new JournalStoreOptions
            {
                MaxBodyBytes = 4096,
                MaxBatchEntries = 4,
                MaxBatchBytes = 4096,
                MaxQueuedBytes = 1 << 20,
                WarmStateBytes = 8192,
                TombstoneRetention = Retention,
            };
            Journals = new JournalStore(Registry, archive, options, SimulationEnvironment.Clock)
            {
                OnAdmission = (ledger, writer) => trace.TraceWriter(writer, name, ledger),
            };
        }

        public string Name { get; }

        public LedgerStoreNode StoreNode { get; }

        public NodeId Node => StoreNode.NodeId;

        public int? Ordinal { get; }

        public bool Diskless { get; }

        public bool Stopped { get; private set; }

        public LedgerMapView View { get; }

        public LedgerReader Reader { get; }

        public LedgerWriterRegistry Registry { get; }

        public TransitionExecutor Executor { get; }

        public JournalStore Journals { get; }

        private readonly Dictionary<LedgerId, Slot> _releasedLedgers = [];

        public NodeReport CreateReport()
        {
            ImmutableDictionary<LedgerId, Slot>? released = null;
            if (_releasedLedgers.Count > 0)
            {
                var map = View.Current;
                foreach (var ledger in _releasedLedgers.Keys.ToArray())
                {
                    if (map.Get(ledger) is not { IsOpen: true } open || open.Primary.Node != Node)
                    {
                        _ = _releasedLedgers.Remove(ledger);
                    }
                }

                if (_releasedLedgers.Count > 0)
                {
                    released = _releasedLedgers.ToImmutableDictionary();
                }
            }

            return NodeReport.Idle(Node, generation: 1, ++_reportSequence) with
            {
                ArchiveGatePassed = true,
                Draining = true,
                ObjectLedgersReady = true,
                ReleasedLedgers = released,
            };
        }

        public async Task<PrepareReleaseReply> PrepareReleaseAsync(PrepareReleaseRequest request, CancellationToken cancellationToken)
        {
            if (Stopped)
            {
                return PrepareReleaseReply.Declined.Instance;
            }

            if (_releasedLedgers.TryGetValue(request.Ledger, out var tail))
            {
                return new PrepareReleaseReply.Released(tail);
            }

            var outcome = await Journals.PrepareReleaseAsync(request.Ledger, request.Range, request.Deadline, cancellationToken);
            if (outcome.Value is LedgerPrepareReleaseOutcome.Released released)
            {
                _releasedLedgers[request.Ledger] = released.ContiguousTail;
                return new PrepareReleaseReply.Released(released.ContiguousTail);
            }

            return outcome.Value switch
            {
                LedgerPrepareReleaseOutcome.Sealed => PrepareReleaseReply.Sealed.Instance,
                LedgerPrepareReleaseOutcome.TimedOut => PrepareReleaseReply.TimedOut.Instance,
                _ => PrepareReleaseReply.NotServed.Instance,
            };
        }

        public ValueTask StopAsync()
        {
            Stopped = true;
            return DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stopped = true;
            await Journals.DisposeAsync();
            await Registry.DisposeAsync();
            await Reader.DisposeAsync();
        }
    }

    private sealed class Scenario(
        SimulationEnvironment environment,
        SimulatedLedgerReplicaTransport replicas,
        SimulatedConditionalRegister mapRegister,
        SimulatedConditionalRegister registryRegister,
        SimulatedObjectStore objects,
        ObjectLedgerStore objectLedgers,
        SimulatedArchiveStore archive,
        LedgerStoreNode[] allNodes,
        Mode mode,
        CancellationToken cancellationToken)
    {
        private static readonly string[] JournalNames = ["nvme-a", "nvme-b", "nvme-c", "nvme-d"];

        private readonly SimulationRandom _random = environment.Random;
        private readonly SimulatedRegistryStore _registryStore = new(registryRegister);
        private readonly List<NodeProcess> _activeProcesses = [];
        private readonly HistoryRecorder<JournalInput, JournalOutput> _history = new(environment.TimeProvider);
        private readonly Dictionary<NodeId, NodeLiveness> _membership = [];
        private readonly HashSet<LedgerId> _drainedClosed = [];
        private readonly HashSet<LedgerId> _drainedNonEmptyNodesAfterStart = [];
        private readonly List<string> _violations = [];
        private LedgerMapValue _previousMap = LedgerMapValue.Empty;
        private int _checkedHistory;
        private int _nextWriterId;
        private ulong? _readyAtEpoch;
        private ulong? _drainAllStartedAtEpoch;
        private bool _faulting = true;
        private bool _controllerLoopRunning;
        private bool _duplicateControllerLoopRunning;
        private bool _drainLoopRunning;
        private bool _writeLoopRunning;
        private ControllerHost? _activeController;
        private ControllerHost? _duplicateController;

        public async Task RunAsync()
        {
            environment.Invariants.Add("drain-all-map", CheckMap);
            switch (mode)
            {
                case Mode.FullRestart:
                case Mode.ReplayRestart:
                    await RunRestartScenarioAsync(fullWorkload: mode == Mode.FullRestart);
                    break;
                case Mode.CancelAndResume:
                    await RunCancelAndResumeScenarioAsync();
                    break;
                case Mode.MixedDisklessReplacement:
                    await RunMixedDisklessReplacementScenarioAsync();
                    break;
                default:
                    throw new UnreachableException();
            }
        }

        private async Task RunRestartScenarioAsync(bool fullWorkload)
        {
            ControllerHost? staleController = null;
            try
            {
                // 1. Register 3 initial diskful nodes at ordinals 0, 1, 2 in CLNR and start their processes.
                for (var ordinal = 0; ordinal < DiskfulNodeCount; ordinal++)
                {
                    await AddNodeAsync($"initial-{ordinal}", allNodes[ordinal], ordinal, diskless: false);
                }

                var initialNodes = _activeProcesses.Select(static p => p.Node).ToHashSet();

                // 2. Start controller 1 (LedgerPlacement = Nodes) and a stale duplicate paused pre-drain-all.
                _activeController = await CreateControllerAsync(adoptObjectLedgers: true);
                staleController = await CreateControllerAsync(adoptObjectLedgers: true);

                _controllerLoopRunning = true;
                _drainLoopRunning = true;
                var controllerTask = ControllerLoopAsync(() => _activeController, () => _controllerLoopRunning);
                var drainTask = DrainClosedLedgersLoopAsync();

                // Bootstrap on Nodes ledgers across all 3 initial diskful nodes.
                await WaitForMapAsync(map =>
                    map.Ledgers.Count(static l => l.IsOpen) >= DiskfulNodeCount &&
                    map.Ledgers.Where(static l => l.IsOpen).All(static l => l.Location.Value is LedgerLocation.Nodes),
                    "initial bootstrap on Nodes ledgers");

                // Freeze stale controller's pre-drain-all view.
                _ = await RetryAsync(() => staleController.View.ReadAsync(cancellationToken), cancellationToken);
                var staleRegistry = await RetryAsync(() => _registryStore.ReadAsync(cancellationToken), cancellationToken);
                var staleRegistryRevision = staleRegistry.Revision;
                staleController.Controller.Observe(staleRegistry, _membership.ToFrozenDictionary());

                // 3. Run initial writes on Nodes ledgers and assert at least 1 accepted before drain-all starts.
                var preDrainAccepted = await WriteRoundAsync("pre-drain-", clientBase: 0, requireAllAccepted: false);
                Assert.True(preDrainAccepted >= 1, "Expected at least one Accepted write on Nodes ledgers before drain-all.");

                _writeLoopRunning = true;
                var concurrentWriters = SimulationTasks.WhenAll(Enumerable.Range(0, JournalNames.Length).Select(ConcurrentWriteLoopAsync));

                // 4. Start drain-all durably in CLNR (DrainAllRevision > 0).
                _drainAllStartedAtEpoch = _activeController.View.Current.Epoch;
                var drainAllRev = await _registryStore.StartDrainAllAsync(cancellationToken);
                Assert.True(drainAllRev > 0);
                environment.Trace.Record($"drain-all started at rev {drainAllRev}");

                // 5. Phase-gated mid-drain-all faults: wait until Phase 1 has started moving a range to ObjectStore.
                await WaitForMapAsync(
                    map => map.RetirementFloor >= drainAllRev || map.Ledgers.Any(static l => l.IsOpen && l.Location.Value is LedgerLocation.ObjectStore),
                    "Phase 1 to begin rolling or fencing");

                Task? duplicateTask = null;
                if (fullWorkload)
                {
                    // Launch an OR-I12 concurrent duplicate controller activation during drain-all.
                    _duplicateController = await CreateControllerAsync(adoptObjectLedgers: true);
                    _duplicateControllerLoopRunning = true;
                    duplicateTask = ControllerLoopAsync(() => _duplicateController, () => _duplicateControllerLoopRunning);

                    // Crash & stop diskful node 1 mid-drain-all, mark Dead in membership, let passes run, then restart it.
                    var bouncingIndex = 1;
                    var bouncingProcess = _activeProcesses[bouncingIndex];
                    var bouncingNode = bouncingProcess.StoreNode;
                    await bouncingProcess.StopAsync();
                    _membership[bouncingNode.NodeId] = new NodeLiveness.Dead(SimulationEnvironment.Clock.GetUtcNow());
                    await bouncingNode.CrashAsync(operatingSystem: false);

                    var bounceStart = _activeController.View.Current.Epoch;
                    await WaitForMapAsync(
                        map => map.Epoch > bounceStart || !map.Ledgers.Any(static l => l.IsOpen && l.Location.Value is LedgerLocation.Nodes),
                        "progress while node 1 is crashed mid-drain-all");

                    await bouncingNode.StartAsync(cancellationToken);
                    var restartedView = await RetryAsync(() => LedgerMapView.OpenAsync(mapRegister, cancellationToken), cancellationToken);
                    _activeProcesses[bouncingIndex] = new NodeProcess(
                        "initial-1-restarted",
                        bouncingNode,
                        ordinal: bouncingIndex,
                        diskless: false,
                        restartedView,
                        new ClientTransport(replicas, "initial-1-restarted"),
                        objectLedgers,
                        archive,
                        replicas.LedgerTrace);
                    _membership[bouncingNode.NodeId] = NodeLiveness.Live.Instance;
                }

                // Fail over controller 1 -> fresh controller 2: durable DrainAllRevision keeps IsDrainingAll == true
                // automatically without losing drain-all state or rolling back onto Nodes!
                var oldController = _activeController;
                var failedOver = await CreateControllerAsync(adoptObjectLedgers: true);
                var currentReg = await RetryAsync(() => _registryStore.ReadAsync(cancellationToken), cancellationToken);
                failedOver.Controller.Observe(currentReg, _membership.ToFrozenDictionary());
                if (currentReg.DrainAllRevision > 0)
                {
                    Assert.True(failedOver.Controller.IsDrainingAll);
                }

                _activeController = failedOver;
                await oldController.DisposeAsync();
                environment.Trace.Record("controller failed over durably during drain-all");

                // Stop concurrent writers, stop duplicate controller, heal faults so drain-all converges cleanly.
                _writeLoopRunning = false;
                await concurrentWriters;
                if (duplicateTask is not null)
                {
                    _duplicateControllerLoopRunning = false;
                    await duplicateTask;
                    if (_duplicateController is not null)
                    {
                        await _duplicateController.DisposeAsync();
                        _duplicateController = null;
                    }
                }

                Heal();

                // 6. Wait for durable readiness: Active == false (CompleteDrainAll reset DrainAllRevision to 0),
                //    0 Nodes ledgers, 0 active diskful nodes, floor covers every diskful retirement.
                var ready = await WaitForDrainAllReadyAsync();
                Assert.False(ready.Active);
                Assert.Empty(ready.NodeLedgers);
                Assert.Empty(ready.ActiveDiskfulNodes);
                Assert.True(ready.FloorCovers);
                Assert.True(ready.RegistryRevision > 0);
                _readyAtEpoch = ready.Epoch;
                environment.Trace.Record($"drain-all ready at epoch {ready.Epoch} floor {ready.Floor}");

                // Assert at least one non-empty closed Nodes ledger (T > 0) was drained and dropped after drain-all started.
                Assert.NotEmpty(_drainedNonEmptyNodesAfterStart);

                var drainedMap = await _activeController.View.ReadAsync(cancellationToken);
                Assert.DoesNotContain(drainedMap.Ledgers, static l => l.Location.Value is LedgerLocation.Nodes);
                Assert.NotEmpty(drainedMap.Ledgers.Where(static l => l.IsOpen));
                Assert.All(drainedMap.Ledgers.Where(static l => l.IsOpen), static l =>
                {
                    _ = Assert.IsType<LedgerLocation.ObjectStore>(l.Location.Value);
                });

                var drainedRegistry = await _registryStore.ReadAsync(cancellationToken);
                Assert.Equal(0ul, drainedRegistry.DrainAllRevision);
                foreach (var node in initialNodes)
                {
                    var registered = drainedRegistry.Nodes[node];
                    Assert.NotNull(registered.RetiredRevision);
                    Assert.True(drainedMap.RetirementFloor >= registered.RetiredRevision.Value);
                }

                // Non-vacuous OR-I4 / LG-I11 check: attempt to close a CURRENTLY OPEN ObjectStore ledger from
                // drainedMap with chosenAt = staleRegistryRevision (< drainedMap.RetirementFloor) to install a Nodes ledger.
                var liveOpen = drainedMap.Ledgers.First(static l => l.IsOpen);
                Assert.True(staleRegistryRevision < drainedMap.RetirementFloor);
                var staleInstall = new LedgerMapChange.Install(
                    [new LedgerClosing(liveOpen.Id, Slot.Zero)],
                    [new LedgerOpening(liveOpen.Range, new LedgerLocation.Nodes([.. initialNodes]), Window, new PrimaryClaim(_activeProcesses[0].Node, SequencerInstanceId.New()))],
                    staleRegistryRevision);
                var staleOutcome = await staleController.Controller.CommitLedgerMapAsync(staleInstall, cancellationToken);
                _ = Assert.IsType<LedgerMapCommitOutcome.Superseded>(staleOutcome.Value);

                // Write one round on the drained ObjectStore ledgers (healed -> every write must be Accepted).
                _ = await WriteRoundAsync("drained-obj-", clientBase: 10, requireAllAccepted: true);

                // 7. Stop loops and every initial node, and discard their local stores.
                _controllerLoopRunning = false;
                _drainLoopRunning = false;
                await controllerTask;
                await drainTask;

                await _activeController.DisposeAsync();
                _activeController = null;

                foreach (var process in _activeProcesses)
                {
                    await process.StopAsync();
                    _membership[process.Node] = new NodeLiveness.Dead(SimulationEnvironment.Clock.GetUtcNow());
                }

                _activeProcesses.Clear();
                for (var ordinal = 0; ordinal < DiskfulNodeCount; ordinal++)
                {
                    await allNodes[ordinal].CrashAsync(operatingSystem: false);
                }

                _readyAtEpoch = null;

                // 8. Register 3 fresh diskful nodes over empty roots at the exact same ordinals 0, 1, 2 (real CAS + OrdinalOccupied check).
                for (var ordinal = 0; ordinal < DiskfulNodeCount; ordinal++)
                {
                    await AddNodeAsync($"restarted-{ordinal}", allNodes[DiskfulNodeCount + ordinal], ordinal, diskless: false);
                }

                var restartedNodes = _activeProcesses.Select(static p => p.Node).ToHashSet();
                Assert.Empty(initialNodes.Intersect(restartedNodes));

                // 9. Start fresh controller 3 (DrainAllRevision == 0 -> IsDrainingAll == false) and restart loops.
                _activeController = await CreateControllerAsync(adoptObjectLedgers: true);
                Assert.False(_activeController.Controller.IsDrainingAll);

                _controllerLoopRunning = true;
                _drainLoopRunning = true;
                controllerTask = ControllerLoopAsync(() => _activeController, () => _controllerLoopRunning);
                drainTask = DrainClosedLedgersLoopAsync();

                await WaitForMapAsync(map =>
                    map.Ledgers.Any(static l => l.IsOpen) &&
                    map.Ledgers.Where(static l => l.IsOpen).All(l =>
                        l.Location.Value is LedgerLocation.Nodes &&
                        l.Replicas.Length == DiskfulNodeCount &&
                        l.Replicas.All(restartedNodes.Contains)),
                    "automatic rollback onto fresh Nodes ledgers after empty-root restart");

                await ReadAllJournalsAsync(clientBase: 20, requireNonEmpty: true);
                _ = await WriteRoundAsync("post-restart-", clientBase: 30, requireAllAccepted: true);
                await ReadAllJournalsAsync(clientBase: 40, requireNonEmpty: true);

                _controllerLoopRunning = false;
                _drainLoopRunning = false;
                await controllerTask;
                await drainTask;

                Assert.Empty(_violations);
                LinearizabilityAssert.Linearizable(JournalModel.Instance, _history.History(), cancellationToken: cancellationToken);
                Assert.NotEmpty(_drainedClosed);
                environment.Trace.Record("drain-all verified");
            }
            finally
            {
                await DisposeAllAsync(staleController);
            }
        }

        private async Task RunCancelAndResumeScenarioAsync()
        {
            try
            {
                for (var ordinal = 0; ordinal < DiskfulNodeCount; ordinal++)
                {
                    await AddNodeAsync($"initial-{ordinal}", allNodes[ordinal], ordinal, diskless: false);
                }

                var initialNodes = _activeProcesses.Select(static p => p.Node).ToHashSet();
                _activeController = await CreateControllerAsync(adoptObjectLedgers: true);
                _controllerLoopRunning = true;
                _drainLoopRunning = true;
                var controllerTask = ControllerLoopAsync(() => _activeController, () => _controllerLoopRunning);
                var drainTask = DrainClosedLedgersLoopAsync();

                await WaitForMapAsync(map =>
                    map.Ledgers.Count(static l => l.IsOpen) >= DiskfulNodeCount &&
                    map.Ledgers.Where(static l => l.IsOpen).All(static l => l.Location.Value is LedgerLocation.Nodes),
                    "initial bootstrap for cancel-resume");

                Heal();
                _ = await WriteRoundAsync("pre-cancel-", clientBase: 0, requireAllAccepted: true);

                // Pause closed-ledger draining briefly so Phase 1 stays in Phase 1 (closed Nodes ledgers remain) while we cancel!
                _drainLoopRunning = false;
                await drainTask;

                var drainRev = await _registryStore.StartDrainAllAsync(cancellationToken);
                Assert.True(drainRev > 0);
                await WaitForMapAsync(
                    map => map.Ledgers.Any(static l => l.IsOpen && l.Location.Value is LedgerLocation.ObjectStore),
                    "at least one ObjectStore ledger to open during Phase 1 before cancel");

                // Cancel mid-Phase-1: DrainAllRevision clears to 0 and NO diskful node was retired.
                Assert.True(await _registryStore.CancelDrainAllAsync(cancellationToken));
                Assert.False(await _registryStore.CancelDrainAllAsync(cancellationToken));

                _drainLoopRunning = true;
                drainTask = DrainClosedLedgersLoopAsync();

                // Controller automatically rolls every range back onto the original 3 diskful Nodes!
                await WaitForMapAsync(map =>
                    map.Ledgers.Any(static l => l.IsOpen) &&
                    map.Ledgers.Where(static l => l.IsOpen).All(l =>
                        l.Location.Value is LedgerLocation.Nodes &&
                        l.Replicas.All(initialNodes.Contains)),
                    "automatic rollback to Nodes after mid-Phase-1 cancel");

                var afterCancelReg = await _registryStore.ReadAsync(cancellationToken);
                Assert.Equal(0ul, afterCancelReg.DrainAllRevision);
                Assert.All(initialNodes, node => Assert.False(afterCancelReg.Nodes[node].IsRetired));

                _ = await WriteRoundAsync("after-cancel-", clientBase: 10, requireAllAccepted: true);

                // Resume drain-all, drain to readiness, stop/wipe, restart fresh nodes at 0, 1, 2, verify rollback.
                _drainAllStartedAtEpoch = _activeController.View.Current.Epoch;
                _ = await _registryStore.StartDrainAllAsync(cancellationToken);
                var ready = await WaitForDrainAllReadyAsync();
                Assert.True(ready.IsReady);
                Assert.False(ready.Active);
                Assert.NotEmpty(_drainedNonEmptyNodesAfterStart);

                _controllerLoopRunning = false;
                _drainLoopRunning = false;
                await controllerTask;
                await drainTask;
                await _activeController.DisposeAsync();
                _activeController = null;

                foreach (var process in _activeProcesses)
                {
                    await process.StopAsync();
                    _membership[process.Node] = new NodeLiveness.Dead(SimulationEnvironment.Clock.GetUtcNow());
                }

                _activeProcesses.Clear();
                for (var ordinal = 0; ordinal < DiskfulNodeCount; ordinal++)
                {
                    await allNodes[ordinal].CrashAsync(operatingSystem: false);
                    await AddNodeAsync($"restarted-{ordinal}", allNodes[DiskfulNodeCount + ordinal], ordinal, diskless: false);
                }

                var restartedNodes = _activeProcesses.Select(static p => p.Node).ToHashSet();
                _activeController = await CreateControllerAsync(adoptObjectLedgers: true);
                _controllerLoopRunning = true;
                _drainLoopRunning = true;
                controllerTask = ControllerLoopAsync(() => _activeController, () => _controllerLoopRunning);
                drainTask = DrainClosedLedgersLoopAsync();

                await WaitForMapAsync(map =>
                    map.Ledgers.Any(static l => l.IsOpen) &&
                    map.Ledgers.Where(static l => l.IsOpen).All(l =>
                        l.Location.Value is LedgerLocation.Nodes &&
                        l.Replicas.All(restartedNodes.Contains)),
                    "rollback to fresh Nodes after resumed drain-all");

                await ReadAllJournalsAsync(clientBase: 20, requireNonEmpty: true);
                _ = await WriteRoundAsync("post-resume-", clientBase: 30, requireAllAccepted: true);
                await ReadAllJournalsAsync(clientBase: 40, requireNonEmpty: true);

                _controllerLoopRunning = false;
                _drainLoopRunning = false;
                await controllerTask;
                await drainTask;

                Assert.Empty(_violations);
                LinearizabilityAssert.Linearizable(JournalModel.Instance, _history.History(), cancellationToken: cancellationToken);
                environment.Trace.Record("cancel-resume verified");
            }
            finally
            {
                await DisposeAllAsync(extra: null);
            }
        }

        private async Task RunMixedDisklessReplacementScenarioAsync()
        {
            try
            {
                // 3 diskful nodes (ordinals 0, 1, 2) + 1 diskless node (no ordinal, Diskless = true).
                for (var ordinal = 0; ordinal < DiskfulNodeCount; ordinal++)
                {
                    await AddNodeAsync($"diskful-{ordinal}", allNodes[ordinal], ordinal, diskless: false);
                }

                await AddNodeAsync("diskless-0", allNodes[DiskfulNodeCount * 2], ordinal: null, diskless: true);
                var initialDiskful = _activeProcesses.Where(static p => !p.Diskless).Select(static p => p.Node).ToHashSet();
                var disklessProcess = _activeProcesses.Single(static p => p.Diskless);

                _activeController = await CreateControllerAsync(adoptObjectLedgers: true);
                _controllerLoopRunning = true;
                _drainLoopRunning = true;
                var controllerTask = ControllerLoopAsync(() => _activeController, () => _controllerLoopRunning);
                var drainTask = DrainClosedLedgersLoopAsync();

                await WaitForMapAsync(map =>
                    map.Ledgers.Count(static l => l.IsOpen) >= DiskfulNodeCount &&
                    map.Ledgers.Where(static l => l.IsOpen).All(static l => l.Location.Value is LedgerLocation.Nodes),
                    "initial Nodes bootstrap in mixed cluster");

                Heal();
                _ = await WriteRoundAsync("mixed-pre-drain-", clientBase: 0, requireAllAccepted: true);

                // Start drain-all: Phase 1 prefers diskless-0 for ObjectStore rolls; Phase 2 retires only diskful nodes.
                _drainAllStartedAtEpoch = _activeController.View.Current.Epoch;
                _ = await _registryStore.StartDrainAllAsync(cancellationToken);
                var ready = await WaitForDrainAllReadyAsync();
                Assert.True(ready.IsReady);
                Assert.Empty(ready.ActiveDiskfulNodes);
                Assert.NotEmpty(_drainedNonEmptyNodesAfterStart);

                var regAfterPhase2 = await _registryStore.ReadAsync(cancellationToken);
                Assert.False(regAfterPhase2.Nodes[disklessProcess.Node].IsRetired);
                Assert.All(initialDiskful, node => Assert.True(regAfterPhase2.Nodes[node].IsRetired));

                // Wait until all open ObjectStore ledgers are served by diskless-0 before stopping the 3 diskful nodes.
                await WaitForMapAsync(map =>
                    map.Ledgers.Any(static l => l.IsOpen) &&
                    map.Ledgers.Where(static l => l.IsOpen).All(l =>
                        l.Location.Value is LedgerLocation.ObjectStore &&
                        l.Primary.Node == disklessProcess.Node),
                    "open ObjectStore ledgers to move onto surviving diskless node");

                // Stop & wipe all 3 diskful nodes while diskless-0 keeps serving uninterrupted!
                foreach (var process in _activeProcesses.Where(static p => !p.Diskless).ToArray())
                {
                    await process.StopAsync();
                    _membership[process.Node] = new NodeLiveness.Dead(SimulationEnvironment.Clock.GetUtcNow());
                    _ = _activeProcesses.Remove(process);
                }

                for (var ordinal = 0; ordinal < DiskfulNodeCount; ordinal++)
                {
                    await allNodes[ordinal].CrashAsync(operatingSystem: false);
                }

                // Live reads & writes continue uninterrupted with 0 diskful nodes!
                await ReadAllJournalsAsync(clientBase: 10, requireNonEmpty: true);
                _ = await WriteRoundAsync("diskless-only-", clientBase: 20, requireAllAccepted: true);
                await ReadAllJournalsAsync(clientBase: 30, requireNonEmpty: true);

                // Start 3 fresh diskful nodes over empty roots at ordinals 0, 1, 2.
                for (var ordinal = 0; ordinal < DiskfulNodeCount; ordinal++)
                {
                    await AddNodeAsync($"replacement-{ordinal}", allNodes[DiskfulNodeCount + ordinal], ordinal, diskless: false);
                }

                var freshDiskful = _activeProcesses.Where(static p => !p.Diskless).Select(static p => p.Node).ToHashSet();
                Assert.Empty(initialDiskful.Intersect(freshDiskful));

                // Controller automatically rolls open ObjectStore ledgers back onto the 3 fresh diskful nodes!
                await WaitForMapAsync(map =>
                    map.Ledgers.Any(static l => l.IsOpen) &&
                    map.Ledgers.Where(static l => l.IsOpen).All(l =>
                        l.Location.Value is LedgerLocation.Nodes &&
                        l.Replicas.Length == DiskfulNodeCount &&
                        l.Replicas.All(freshDiskful.Contains)),
                    "automatic rollback onto fresh diskful nodes in mixed cluster");

                _ = await WriteRoundAsync("mixed-post-replace-", clientBase: 40, requireAllAccepted: true);
                await ReadAllJournalsAsync(clientBase: 50, requireNonEmpty: true);

                _controllerLoopRunning = false;
                _drainLoopRunning = false;
                await controllerTask;
                await drainTask;

                Assert.Empty(_violations);
                LinearizabilityAssert.Linearizable(JournalModel.Instance, _history.History(), cancellationToken: cancellationToken);
                environment.Trace.Record("mixed-cluster replacement verified");
            }
            finally
            {
                await DisposeAllAsync(extra: null);
            }
        }

        private async Task DisposeAllAsync(ControllerHost? extra)
        {
            if (_activeController is not null)
            {
                await _activeController.DisposeAsync();
                _activeController = null;
            }

            if (_duplicateController is not null)
            {
                await _duplicateController.DisposeAsync();
                _duplicateController = null;
            }

            if (extra is not null)
            {
                await extra.DisposeAsync();
            }

            foreach (var process in _activeProcesses)
            {
                await process.DisposeAsync();
            }

            _activeProcesses.Clear();
        }

        private async Task AddNodeAsync(string name, LedgerStoreNode storeNode, int? ordinal, bool diskless)
        {
            _ = await _registryStore.RegisterAsync(storeNode.NodeId, ordinal, diskless, cancellationToken);
            _membership[storeNode.NodeId] = NodeLiveness.Live.Instance;

            var view = await RetryAsync(() => LedgerMapView.OpenAsync(mapRegister, cancellationToken), cancellationToken);
            var process = new NodeProcess(
                name,
                storeNode,
                ordinal,
                diskless,
                view,
                new ClientTransport(replicas, name),
                objectLedgers,
                archive,
                replicas.LedgerTrace);
            _activeProcesses.Add(process);
        }

        private async Task<ControllerHost> CreateControllerAsync(bool adoptObjectLedgers)
        {
            var options = new ControlOptions
            {
                LedgersPerNode = 1,
                SequencerWindow = Window,
                LedgerPlacement = LedgerPlacement.Nodes,
                ControlInterval = TimeSpan.FromMilliseconds(20),
                BalanceInterval = TimeSpan.FromMilliseconds(20),
                TransitionTimeout = TimeSpan.FromSeconds(4),
                PrepareReleaseTimeout = TimeSpan.FromMilliseconds(150),
                ClosedDrainTimeout = TimeSpan.FromMilliseconds(50),
                MaxConcurrentTransitions = 4,
                MaxConcurrentHandOffs = 4,
            };
            var view = await RetryAsync(() => LedgerMapView.OpenAsync(mapRegister, cancellationToken), cancellationToken);
            var host = new ControllerHost(options, view, mapRegister, ++_nextWriterId, adoptObjectLedgers);
            var reg = await RetryAsync(() => _registryStore.ReadAsync(cancellationToken), cancellationToken);
            host.Controller.Observe(reg, _membership.ToFrozenDictionary());
            return host;
        }

        private async Task ControllerLoopAsync(Func<ControllerHost?> getHost, Func<bool> isRunning)
        {
            var inFlight = new List<Task>();
            while (isRunning())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(15), SimulationEnvironment.Clock, cancellationToken);
                var host = getHost();
                if (host is null)
                {
                    continue;
                }

                RegisteredNodes reg;
                try
                {
                    _ = await host.View.ReadAsync(cancellationToken);
                    reg = await _registryStore.ReadAsync(cancellationToken);
                }
                catch (Exception failure) when (IsUnavailable(failure) || failure is ObjectDisposedException)
                {
                    continue;
                }

                host.Controller.Observe(reg, _membership.ToFrozenDictionary());

                foreach (var process in _activeProcesses.ToArray())
                {
                    if (process.Stopped)
                    {
                        continue;
                    }

                    try
                    {
                        _ = await process.View.ReadAsync(cancellationToken);
                    }
                    catch (Exception failure) when (IsUnavailable(failure) || failure is ObjectDisposedException)
                    {
                    }

                    _ = host.Controller.Report(process.CreateReport());
                }

                var pass = host.Controller.RunPass();

                if (pass.FloorRaise is { } floor)
                {
                    try
                    {
                        _ = await host.Controller.CommitLedgerMapAsync(new LedgerMapChange.RaiseFloor(floor), cancellationToken);
                    }
                    catch (Exception failure) when (IsUnavailable(failure) || failure is ObjectDisposedException)
                    {
                    }
                    finally
                    {
                        host.Controller.FloorRaiseEnded();
                    }
                }

                if (pass.CompleteDrainAllRevision is { } drainRev)
                {
                    try
                    {
                        _ = await _registryStore.CompleteDrainAllAsync(drainRev, cancellationToken);
                        var updated = await _registryStore.ReadAsync(cancellationToken);
                        host.Controller.Observe(updated, _membership.ToFrozenDictionary());
                    }
                    catch (Exception failure) when (IsUnavailable(failure) || failure is ObjectDisposedException)
                    {
                    }
                    finally
                    {
                        host.Controller.DrainAllCompletionEnded();
                    }
                }

                foreach (var retiring in pass.Retirements)
                {
                    try
                    {
                        var updated = await _registryStore.RetireNodeAsync(retiring, cancellationToken);
                        host.Controller.Observe(updated, _membership.ToFrozenDictionary());
                    }
                    catch (Exception failure) when (IsUnavailable(failure) || failure is ObjectDisposedException)
                    {
                    }
                    finally
                    {
                        host.Controller.RetirementSettled(retiring);
                    }
                }

                foreach (var prepare in pass.PrepareReleases)
                {
                    var capturedHost = host;
                    var capturedPrepare = prepare;
                    inFlight.Add(ExecutePrepareReleaseAsync(capturedHost, capturedPrepare));
                }

                foreach (var sent in pass.Transitions)
                {
                    CheckPlacement(host, sent);
                    var capturedHost = host;
                    var capturedSent = sent;
                    inFlight.Add(ExecuteTransitionAsync(capturedHost, capturedSent));
                }

                inFlight.RemoveAll(static task => task.IsCompleted);
            }

            if (inFlight.Count > 0)
            {
                await SimulationTasks.WhenAll(inFlight);
            }
        }

        private void CheckPlacement(ControllerHost host, SentTransition sent)
        {
            var choice = sent.Transition.Choice;
            if (!_registryStore.TryGetRevision(choice.ChosenAt, out var registry))
            {
                _violations.Add(Invariant($"OR-I5: controller {host.WriterId} sent {sent} at unknown registry revision {choice.ChosenAt}."));
                return;
            }

            if (registry.Find(sent.Executor) is not { IsRetired: false })
            {
                _violations.Add(Invariant($"OR-I5: controller {host.WriterId} placed retired/unregistered executor {sent.Executor} at revision {choice.ChosenAt}."));
            }

            _ = choice.Location switch
            {
                LedgerLocation.Nodes nodes => CheckNodes(nodes),
                LedgerLocation.ObjectStore => true,
                null => throw new UnreachableException(),
            };

            bool CheckNodes(LedgerLocation.Nodes nodes)
            {
                if (registry.DrainAllRevision > 0)
                {
                    _violations.Add(Invariant($"OR-I5: controller {host.WriterId} placed a Nodes ledger while DrainAllRevision={registry.DrainAllRevision} at revision {choice.ChosenAt}."));
                }

                foreach (var replica in nodes.Replicas)
                {
                    if (registry.Find(replica) is not { IsRetired: false, Diskless: false })
                    {
                        _violations.Add(Invariant($"OR-I5: controller {host.WriterId} placed non-active/diskless replica {replica} in Nodes ledger at revision {choice.ChosenAt}."));
                    }
                }

                return true;
            }
        }

        private async Task ExecutePrepareReleaseAsync(ControllerHost host, SentPrepareRelease sent)
        {
            var primary = _activeProcesses.FirstOrDefault(p => p.Node == sent.Node && !p.Stopped);
            if (primary is null)
            {
                host.Controller.PrepareReleaseCompleted(sent, reply: null);
                return;
            }

            PrepareReleaseReply? reply = null;
            try
            {
                reply = await primary.PrepareReleaseAsync(sent.Request, cancellationToken);
            }
            catch (Exception failure) when (IsUnavailable(failure) || failure is ObjectDisposedException)
            {
                reply = null;
            }

            host.Controller.PrepareReleaseCompleted(sent, reply);
        }

        private async Task ExecuteTransitionAsync(ControllerHost host, SentTransition sent)
        {
            var executor = _activeProcesses.FirstOrDefault(p => p.Node == sent.Executor && !p.Stopped);
            if (executor is null)
            {
                _ = host.Controller.TransitionCompleted(sent, reply: null);
                return;
            }

            TransitionReply? reply = null;
            try
            {
                var outcome = await executor.Executor.ExecuteAsync(
                    sent.Transition,
                    (change, token) => host.Controller.CommitLedgerMapAsync(change, token),
                    cancellationToken);
                reply = TransitionReply.Of(outcome);
            }
            catch (Exception failure) when (IsUnavailable(failure) || failure is ObjectDisposedException)
            {
                reply = null;
            }

            _ = host.Controller.TransitionCompleted(sent, reply);
        }

        private async Task DrainClosedLedgersLoopAsync()
        {
            while (_drainLoopRunning)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), SimulationEnvironment.Clock, cancellationToken);
                var host = _activeController;
                var readerProcess = _activeProcesses.FirstOrDefault(static p => !p.Stopped);
                if (host is null || readerProcess is null)
                {
                    continue;
                }

                LedgerMapValue map;
                try
                {
                    map = await host.View.ReadAsync(cancellationToken);
                }
                catch (Exception failure) when (IsUnavailable(failure) || failure is ObjectDisposedException)
                {
                    continue;
                }

                var droppable = map.Ledgers
                    .Where(ledger => !ledger.IsOpen && !map.Ledgers.Any(lower => lower.Id < ledger.Id && lower.Range.Overlaps(ledger.Range)))
                    .ToArray();
                foreach (var closed in droppable)
                {
                    if (!_drainLoopRunning)
                    {
                        return;
                    }

                    try
                    {
                        await DrainClosedLedgerToArchiveAsync(readerProcess.Reader, closed);
                        var outcome = await host.Controller.CommitLedgerMapAsync(new LedgerMapChange.Drop(closed.Id), cancellationToken);
                        if (outcome.Value is LedgerMapCommitOutcome.Applied)
                        {
                            _ = _drainedClosed.Add(closed.Id);
                            if (_drainAllStartedAtEpoch is not null &&
                                closed.Location.Value is LedgerLocation.Nodes &&
                                closed.Status.Value is LedgerStatus.Closed { T.Value: > 0 })
                            {
                                _ = _drainedNonEmptyNodesAfterStart.Add(closed.Id);
                            }
                        }
                    }
                    catch (Exception failure) when (IsUnavailable(failure) || failure is ObjectDisposedException)
                    {
                    }
                }
            }
        }

        private async Task DrainClosedLedgerToArchiveAsync(LedgerReader reader, LedgerMetadata closed)
        {
            if (closed.Status.Value is not LedgerStatus.Closed closedStatus)
            {
                return;
            }

            var end = closedStatus.T;
            if (end == Slot.Zero)
            {
                return;
            }

            var segments = new List<List<KeyedRow>>();
            using var view = new LedgerSegmentView(closed.Id);
            using var bodies = new ArcBufferWriter();
            LedgerSegmentId? current = null;
            var from = Slot.Zero;
            try
            {
                while (from < end)
                {
                    await foreach (var item in reader.ScanAsync(closed, from, end, view, cancellationToken: cancellationToken))
                    {
                        if (!item.Read.TryGetValue(out LedgerRead.Data data))
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                            break;
                        }

                        using (data)
                        {
                            if (current != view.Segment)
                            {
                                segments.Add([]);
                                current = view.Segment;
                            }

                            foreach (var row in item.Rows)
                            {
                                segments[^1].Add(new KeyedRow(row.Key.ToArray(), ArchiveRow.Of(row, view.Segment, bodies)));
                            }
                        }

                        from = data.Slot + 1;
                    }
                }

                foreach (var segment in segments)
                {
                    KeyedRow.DrainByKey(CollectionsMarshal.AsSpan(segment), (key, rows) => archive.Drain(key, rows));
                }
            }
            finally
            {
                foreach (var segment in segments)
                {
                    KeyedRow.Release(segment);
                }
            }
        }

        private async Task ConcurrentWriteLoopAsync(int client)
        {
            var journal = JournalNames[client % JournalNames.Length];
            var sequence = 0;
            while (_writeLoopRunning)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(10, 25)), SimulationEnvironment.Clock, cancellationToken);
                if (!_writeLoopRunning)
                {
                    return;
                }

                _ = await WriteOnceAsync(client, journal, $"concurrent-{sequence++}|");
            }
        }

        private async Task<int> WriteRoundAsync(string prefix, int clientBase, bool requireAllAccepted)
        {
            var accepted = 0;
            for (var index = 0; index < JournalNames.Length; index++)
            {
                var outcome = await WriteOnceAsync(clientBase + index, JournalNames[index], prefix + JournalNames[index] + "|");
                if (requireAllAccepted && outcome.Value is JournalWriteOutcome.Rejected)
                {
                    // A concurrent drain drop between the pre-write read and the write can legitimately rebase a cold
                    // journal's version (JS-I1); retry once from the rebased version.
                    outcome = await WriteOnceAsync(clientBase + index, JournalNames[index], prefix + JournalNames[index] + "|");
                }

                if (requireAllAccepted)
                {
                    _ = Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value);
                }

                if (outcome.Value is JournalWriteOutcome.Accepted)
                {
                    accepted++;
                }
            }

            return accepted;
        }

        private async Task<JournalWriteOutcome> WriteOnceAsync(int client, string journal, string chunk)
        {
            var key = JournalRowKey.Encode(journal);
            var (version, _, currentContent) = await ReadOnceAsync(client + 100, journal);
            var payload = Encoding.UTF8.GetBytes(chunk);
            var invocation = _history.Invoke(client, JournalInput.Append(journal, version.ToString(), payload));
            var outcome = await ExecuteWriteAsync(key, version, payload, replace: false);
            _ = outcome switch
            {
                JournalWriteOutcome.Accepted accepted => Complete(invocation, JournalOutput.Accepted(accepted.Version.ToString())),
                JournalWriteOutcome.Rejected rejected => Complete(invocation, JournalOutput.Rejected(rejected.CurrentVersion.ToString(), deposed: false)),
                JournalWriteOutcome.ReplaceRequired => Complete(invocation, JournalOutput.ReplaceRequiredBy(deposed: false)),
                JournalWriteOutcome.Ambiguous => CompleteUnbounded(invocation),
                JournalMoved or JournalWriteOutcome.Fenced or null => throw new UnreachableException(),
            };
            if (outcome.Value is not JournalWriteOutcome.ReplaceRequired)
            {
                return outcome;
            }

            // Append chain hit the journal's compaction threshold; compact with Replace carrying currentContent + payload.
            byte[] snapshot = [.. currentContent, .. payload];
            var replaceInvocation = _history.Invoke(client, JournalInput.Replace(journal, version.ToString(), snapshot));
            var replaceOutcome = await ExecuteWriteAsync(key, version, snapshot, replace: true);
            _ = replaceOutcome switch
            {
                JournalWriteOutcome.Accepted accepted => Complete(replaceInvocation, JournalOutput.Accepted(accepted.Version.ToString())),
                JournalWriteOutcome.Rejected rejected => Complete(replaceInvocation, JournalOutput.Rejected(rejected.CurrentVersion.ToString(), deposed: false)),
                JournalWriteOutcome.Ambiguous => CompleteUnbounded(replaceInvocation),
                JournalWriteOutcome.ReplaceRequired or JournalMoved or JournalWriteOutcome.Fenced or null => throw new UnreachableException(),
            };
            return replaceOutcome;

            bool Complete(HistoryRecorder<JournalInput, JournalOutput>.Invocation id, JournalOutput output)
            {
                _history.Complete(id, output);
                return true;
            }

            bool CompleteUnbounded(HistoryRecorder<JournalInput, JournalOutput>.Invocation id)
            {
                _history.CompleteUnbounded(id, JournalOutput.Ambiguous);
                return true;
            }
        }

        private async Task<JournalWriteOutcome> ExecuteWriteAsync(JournalRowKey key, JournalVersion expected, byte[] payload, bool replace)
        {
            var target = await ResolveOwnerAsync(key);
            using var body = ArcBuffer.Wrap(payload);
            using var record = replace
                ? target.Journals.EncodeReplace(key, expected, body)
                : target.Journals.EncodeAppend(key, expected, body);

            while (true)
            {
                if (target.Stopped)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    target = await ResolveOwnerAsync(key);
                    continue;
                }

                JournalWriteOutcome outcome;
                try
                {
                    outcome = await target.Journals.WriteAsync(record, cancellationToken);
                }
                catch (ObjectDisposedException) when (target.Stopped)
                {
                    return JournalWriteOutcome.Ambiguous.Instance;
                }
                catch (Exception failure) when (_faulting && IsUnavailable(failure))
                {
                    return JournalWriteOutcome.Ambiguous.Instance;
                }

                var action = outcome switch
                {
                    JournalWriteOutcome.Accepted or JournalWriteOutcome.Rejected or JournalWriteOutcome.ReplaceRequired or JournalWriteOutcome.Ambiguous => 0,
                    JournalMoved => 1,
                    JournalWriteOutcome.Fenced => 2,
                    null => throw new UnreachableException(),
                };
                if (action == 0)
                {
                    return outcome;
                }

                if (action == 1)
                {
                    target = await NextOwnerAsync(key, ((JournalMoved)outcome.Value).Owner);
                    continue;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                target = await ResolveOwnerAsync(key);
            }
        }

        private async Task ReadAllJournalsAsync(int clientBase, bool requireNonEmpty)
        {
            for (var index = 0; index < JournalNames.Length; index++)
            {
                var (_, exists, content) = await ReadOnceAsync(clientBase + index, JournalNames[index]);
                if (requireNonEmpty)
                {
                    Assert.True(exists, $"Expected journal {JournalNames[index]} to exist after restart.");
                    Assert.NotEmpty(content);
                }
            }
        }

        private async Task<(JournalVersion Version, bool Exists, byte[] Content)> ReadOnceAsync(int client, string journal)
        {
            var key = JournalRowKey.Encode(journal);
            var invocation = _history.Invoke(client, JournalInput.Read(journal));
            var target = await ResolveOwnerAsync(key);
            while (true)
            {
                if (target.Stopped)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    target = await ResolveOwnerAsync(key);
                    continue;
                }

                JournalReadOutcome outcome;
                try
                {
                    outcome = await target.Journals.ReadAsync(key, cancellationToken);
                }
                catch (ObjectDisposedException) when (target.Stopped)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    target = await ResolveOwnerAsync(key);
                    continue;
                }
                catch (Exception failure) when (_faulting && IsUnavailable(failure))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    target = await ResolveOwnerAsync(key);
                    continue;
                }

                if (outcome.Value is JournalMoved moved)
                {
                    target = await NextOwnerAsync(key, moved.Owner);
                    continue;
                }

                using var result = (JournalReadResult)outcome.Value;
                List<byte> content = [];
                try
                {
                    await foreach (var chunk in result.ReadPayloadsAsync(cancellationToken))
                    {
                        using (chunk)
                        {
                            content.AddRange(chunk.ToArray());
                        }
                    }
                }
                catch (ObjectDisposedException) when (target.Stopped)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    target = await ResolveOwnerAsync(key);
                    continue;
                }
                catch (Exception failure) when (_faulting && IsUnavailable(failure))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    target = await ResolveOwnerAsync(key);
                    continue;
                }

                var bytes = content.ToArray();
                _history.Complete(invocation, JournalOutput.Read(result.Exists, result.CurrentVersion.ToString(), bytes));
                return (result.CurrentVersion, result.Exists, bytes);
            }
        }

        private async Task<NodeProcess> ResolveOwnerAsync(JournalRowKey key)
        {
            while (true)
            {
                var running = _activeProcesses.Where(static p => !p.Stopped).ToArray();
                if (running.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    continue;
                }

                try
                {
                    var map = await running[0].View.ReadAsync(cancellationToken);
                    foreach (var ledger in map.Ledgers)
                    {
                        if (ledger.IsOpen && ledger.Range.Contains(key.Span) && running.FirstOrDefault(p => p.Node == ledger.Primary.Node) is { } owner)
                        {
                            return owner;
                        }
                    }
                }
                catch (Exception failure) when (_faulting && IsUnavailable(failure))
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
            }
        }

        private async Task<NodeProcess> NextOwnerAsync(JournalRowKey key, NodeId? suggested)
        {
            if (suggested is { } owner && _activeProcesses.FirstOrDefault(p => !p.Stopped && p.Node == owner) is { } found)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
            return await ResolveOwnerAsync(key);
        }

        private async Task WaitForMapAsync(Func<LedgerMapValue, bool> predicate, string what)
        {
            var deadline = SimulationEnvironment.Clock.GetUtcNow() + TimeSpan.FromSeconds(10);
            while (SimulationEnvironment.Clock.GetUtcNow() < deadline)
            {
                var host = _activeController;
                if (host is not null && predicate(host.View.Current))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), SimulationEnvironment.Clock, cancellationToken);
            }

            Assert.Fail($"Timed out waiting for {what}.");
        }

        private async Task<DrainAllStatus> WaitForDrainAllReadyAsync()
        {
            var deadline = SimulationEnvironment.Clock.GetUtcNow() + TimeSpan.FromSeconds(10);
            while (SimulationEnvironment.Clock.GetUtcNow() < deadline)
            {
                var host = _activeController;
                if (host is not null)
                {
                    var status = await host.Controller.DrainAllAsync(
                        async token => await _registryStore.ReadAsync(token),
                        cancellationToken);
                    if (status.IsReady)
                    {
                        return status;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), SimulationEnvironment.Clock, cancellationToken);
            }

            var last = _activeController?.Controller.DrainAll();
            Assert.Fail(Invariant($"Timed out waiting for drain-all readiness (last={last}, nodeLedgers=[{string.Join(",", last?.NodeLedgers ?? [])}], activeDiskful=[{string.Join(",", last?.ActiveDiskfulNodes ?? [])}])."));
            return default;
        }

        private void Heal()
        {
            _faulting = false;
            environment.Network.MessageDropRate = 0;
            environment.Rpc.DuplicationRate = 0;
            mapRegister.UnknownOutcomeRate = 0;
            mapRegister.AppliedMismatchRate = 0;
            mapRegister.ReadFailureRate = 0;
            registryRegister.UnknownOutcomeRate = 0;
            registryRegister.AppliedMismatchRate = 0;
            registryRegister.ReadFailureRate = 0;
            objects.UnknownOutcomeRate = 0;
            objects.RetriedAnswerRate = 0;
            objects.ReadFailureRate = 0;
            archive.StaleRate = 0;
        }

        private string? CheckMap()
        {
            if (_violations.Count > 0)
            {
                return _violations[0];
            }

            for (; _checkedHistory < mapRegister.AppliedWrites.Count; _checkedHistory++)
            {
                if (LedgerMapValue.Decode(mapRegister.AppliedWrites[_checkedHistory].AsSpan()).Value is not LedgerMapValue value)
                {
                    return Invariant($"register applied a non-map value at write {_checkedHistory}");
                }

                if (LedgerMapReference.Violation(_previousMap, value) is { } violation)
                {
                    return violation;
                }

                // OR-I5: no newly installed Nodes ledger may name a node retired at or before the map's RetirementFloor.
                var previousIds = _previousMap.Ledgers.Select(static l => l.Id).ToHashSet();
                var latestReg = _registryStore.Latest;
                foreach (var ledger in value.Ledgers)
                {
                    if (!previousIds.Contains(ledger.Id) && ledger.Location.Value is LedgerLocation.Nodes nodes)
                    {
                        foreach (var replica in nodes.Replicas)
                        {
                            if (latestReg.Find(replica) is { RetiredRevision: { } retiredRev } && retiredRev <= value.RetirementFloor)
                            {
                                return Invariant($"OR-I5: newly installed Nodes ledger {ledger.Id} at epoch {value.Epoch} names node {replica} retired at {retiredRev} <= floor {value.RetirementFloor}.");
                            }
                        }
                    }
                }

                if (_readyAtEpoch is { } readyEpoch && value.Epoch > readyEpoch && value.Ledgers.Any(static l => l.Location.Value is LedgerLocation.Nodes))
                {
                    return Invariant($"OR-I4: Nodes ledger reappeared at epoch {value.Epoch} after drain-all readiness at epoch {readyEpoch}.");
                }

                _previousMap = value;
            }

            return null;
        }
    }
}
