using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Deterministic Clockwork simulations of Ledgers and Journals over a mixed cluster holding both node-backed quorum
/// ledgers (<see cref="LedgerLocation.Nodes"/>) and object-store ledgers (<see cref="LedgerLocation.ObjectStore"/>),
/// including a diskless node with no local <see cref="LedgerStoreNode"/>, rolling ledgers between locations in both
/// directions, taking over object-store ledgers across diskful and diskless primaries, draining closed ledgers of both
/// locations into the archive store, and sweeping dropped object-store ledgers with <see cref="ObjectLedgerSweeper"/>
/// (orleans.md §5.14, §9; LG-I1 – LG-I10; OL-I4 – OL-I9; JS-I1 – JS-I4; HI-I4).
/// </summary>
public sealed class MixedObjectLedgerSimulationTests
{
    private const int FirstSeed = 1;
    private const int SeedCount = 1;
    private const int LedgersReplaySeed = 3;
    private const int JournalsReplaySeed = 4;
    private const int DiskfulNodeCount = 3;
    private const int Window = 4;
    private const int MaxAppends = 4;
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan Retention = TimeSpan.FromMilliseconds(50);

    /// <summary>Gets the seed corpus.</summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    /// <summary>
    /// Ledgers over a mixed cluster (3 diskful nodes + 1 diskless node) keep every map invariant, proposal durability,
    /// per-key tag monotonicity across <c>Nodes &lt;-&gt; ObjectStore</c> rolls, and object-ledger sweep reclamation
    /// under network, conditional-register, and object-store faults.
    /// </summary>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void MixedLocationLedgersKeepInvariantsAcrossRollsAndSweep(int seed) =>
        _ = SeededSimulation.Run(seed, static environment => SimulateLedgersAsync(environment, fullWorkload: true));

    /// <summary>The mixed-location Ledgers simulation replays deterministically from its seed and artifact.</summary>
    [Fact]
    public void MixedLocationLedgersReplayDeterministically()
    {
        var run = SeededSimulation.RunAndReplay(LedgersReplaySeed, static environment => SimulateLedgersAsync(environment, fullWorkload: false));

        Assert.Contains(run.Trace, static entry => entry.Contains("mixed ledgers verified", StringComparison.Ordinal));
    }

    /// <summary>
    /// Journals over a mixed cluster (3 diskful nodes + 1 diskless node) linearize against <see cref="JournalModel"/>
    /// and preserve every token chain and final fold across concurrent <c>Nodes &lt;-&gt; ObjectStore</c> rolls,
    /// diskless takeovers, archive drains of both locations, and object-ledger sweeps under faults.
    /// </summary>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void MixedLocationJournalsLinearizeAcrossRollsAndDrains(int seed) =>
        _ = SeededSimulation.Run(seed, static environment => SimulateJournalsAsync(environment, fullWorkload: true));

    /// <summary>The mixed-location Journals simulation replays deterministically from its seed and artifact.</summary>
    [Fact]
    public void MixedLocationJournalsReplayDeterministically()
    {
        var run = SeededSimulation.RunAndReplay(JournalsReplaySeed, static environment => SimulateJournalsAsync(environment, fullWorkload: false));

        Assert.Contains(run.Trace, static entry => entry.Contains("mixed journals verified", StringComparison.Ordinal));
    }

    private static async Task SimulateLedgersAsync(SimulationEnvironment environment, bool fullWorkload)
    {
        using var cancellation = new CancellationTokenSource();
        var network = environment.Network;
        network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        network.MaxJitter = TimeSpan.FromMilliseconds(3);
        network.MessageDropRate = 0.03;
        environment.Rpc.DuplicationRate = 0.03;
        var client = environment.Cluster.AddNode("client");
        var diskful = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, DiskfulNodeCount, cancellation.Token);
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, diskful, Deadline);
        _ = LedgerStoreInvariants.Register(environment, replicas);
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map")
        {
            UnknownOutcomeRate = 0.1,
            AppliedMismatchRate = 0.1,
            ReadFailureRate = 0.05,
        };
        await using var objects = new SimulatedObjectStore(environment, "object-ledgers")
        {
            UnknownOutcomeRate = 0.1,
            RetriedAnswerRate = 0.1,
            ReadFailureRate = 0.05,
        };
        var objectLedgers = ObjectLedgerStore.Over(objects, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.PerSlot, CacheBytes = 64 * 1024 }, SimulationEnvironment.Clock);
        _ = await RetryAsync(() => objectLedgers.StoreRecordAsync(cancellation.Token).AsTask(), cancellation.Token);
        await using var committer = new LedgerMapCommitter(register.Writer(0), int.MaxValue);
        await using var dropCommitter = new LedgerMapCommitter(register.Writer(1), int.MaxValue);
        await new LedgersScenario(environment, replicas, register, objects, objectLedgers, diskful, [committer, dropCommitter], fullWorkload, cancellation.Token).RunAsync();
    }

    private static async Task SimulateJournalsAsync(SimulationEnvironment environment, bool fullWorkload)
    {
        using var cancellation = new CancellationTokenSource();
        var network = environment.Network;
        network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        network.MaxJitter = TimeSpan.FromMilliseconds(3);
        network.MessageDropRate = 0.03;
        environment.Rpc.DuplicationRate = 0.03;
        var client = environment.Cluster.AddNode("client");
        var diskful = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, DiskfulNodeCount, cancellation.Token);
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, diskful, Deadline);
        _ = LedgerStoreInvariants.Register(environment, replicas);
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map")
        {
            UnknownOutcomeRate = 0.1,
            AppliedMismatchRate = 0.1,
            ReadFailureRate = 0.05,
        };
        await using var objects = new SimulatedObjectStore(environment, "object-ledgers")
        {
            UnknownOutcomeRate = 0.1,
            RetriedAnswerRate = 0.1,
            ReadFailureRate = 0.05,
        };
        var objectLedgers = ObjectLedgerStore.Over(objects, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.PerSlot, CacheBytes = 64 * 1024 }, SimulationEnvironment.Clock);
        _ = await RetryAsync(() => objectLedgers.StoreRecordAsync(cancellation.Token).AsTask(), cancellation.Token);
        await using var committer = new LedgerMapCommitter(register.Writer(0), int.MaxValue);
        await using var drainCommitter = new LedgerMapCommitter(register.Writer(1), int.MaxValue);
        using var archive = new SimulatedArchiveStore(environment, "journals", maxAppends: MaxAppends, replaceRequestAppends: MaxAppends - 1) { StaleRate = 0.1 };
        await new JournalsScenario(environment, replicas, register, objects, objectLedgers, diskful, [committer, drainCommitter], archive, fullWorkload, cancellation.Token).RunAsync();
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
            catch (Exception failure) when (failure is IOException or TimeoutException or NodeDownException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
            }
        }
    }

    private static async Task<LedgerMapCommitOutcome> RetryTransitionAsync(
        TransitionExecutor executor,
        Transition transition,
        LedgerMapCommitter committer,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await executor.ExecuteAsync(transition, (change, token) => committer.CommitAsync(change, token), cancellationToken);
            }
            catch (Exception failure) when (failure is IOException or TimeoutException or NodeDownException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
            }
        }
    }

    private static int CompareRanges(LedgerMetadata left, LedgerMetadata right)
    {
        var prefixComparison = left.Range.Prefix.SequenceCompareTo(right.Range.Prefix);
        return prefixComparison != 0 ? prefixComparison : left.Id.CompareTo(right.Id);
    }

    private static LedgerMetadata[] OpenByRange(LedgerMapValue map)
    {
        LedgerMetadata[] open = [.. map.Ledgers.Where(static ledger => ledger.IsOpen)];
        Array.Sort(open, CompareRanges);
        return open;
    }

    private static LedgerMetadata[] OpenObjectByRange(LedgerMapValue map)
    {
        LedgerMetadata[] open = [.. map.Ledgers.Where(static ledger => ledger.IsOpen && ledger.Location.Value is LedgerLocation.ObjectStore)];
        Array.Sort(open, CompareRanges);
        return open;
    }

    private static bool HasBothLocationsOpen(LedgerMapValue map)
    {
        var hasNodes = false;
        var hasObject = false;
        foreach (var ledger in map.Ledgers)
        {
            if (!ledger.IsOpen)
            {
                continue;
            }

            if (ledger.Location.Value is LedgerLocation.Nodes)
            {
                hasNodes = true;
            }
            else if (ledger.Location.Value is LedgerLocation.ObjectStore)
            {
                hasObject = true;
            }
        }

        return hasNodes && hasObject;
    }

    private static async Task<List<string>> ListObjectNamesAsync(IObjectStore store, string prefix, CancellationToken cancellationToken)
    {
        List<string> names = [];
        await foreach (var entry in store.ListAsync(prefix, cancellationToken: cancellationToken))
        {
            names.Add(entry.Name.Value);
        }

        return names;
    }

    private static async Task<List<LedgerId>> FindLedgersWithLiveSlotsAsync(IObjectStore store, IEnumerable<LedgerId> ids, CancellationToken cancellationToken)
    {
        List<LedgerId> withSlots = [];
        foreach (var id in ids)
        {
            var names = await ListObjectNamesAsync(store, ObjectLedgerNames.Ledger(id), cancellationToken);
            foreach (var name in names)
            {
                if (ObjectLedgerNames.ParseSlot(id, new ObjectName(name)) is not null)
                {
                    var properties = await store.GetPropertiesAsync(new ObjectName(name), cancellationToken);
                    if (properties is not null && properties.Length > 0)
                    {
                        withSlots.Add(id);
                        break;
                    }
                }
            }
        }

        return withSlots;
    }

    private static async Task AssertSweptCleanlyAsync(IObjectStore store, IReadOnlyList<LedgerId> ledgersWithLiveSlots, CancellationToken cancellationToken)
    {
        Assert.NotEmpty(ledgersWithLiveSlots);
        foreach (var id in ledgersWithLiveSlots)
        {
            var names = await ListObjectNamesAsync(store, ObjectLedgerNames.Ledger(id), cancellationToken);
            Assert.Contains(ObjectLedgerNames.Dropped(id).Value, names, StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (ObjectLedgerNames.ParseSlot(id, new ObjectName(name)) is not null)
                {
                    var properties = await store.GetPropertiesAsync(new ObjectName(name), cancellationToken);
                    Assert.True(properties is null || properties.Length == 0, Invariant($"Sweeper left live slot object {name} ({properties?.Length} bytes)."));
                }
            }
        }
    }

    private sealed class ClientTransport(SimulatedLedgerReplicaTransport replicas, string client) : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) => replicas.Open(node, ledger, options, client);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            replicas.HeartbeatAsync(node, ledgers, cancellationToken);
    }

    /// <summary>
    /// Mixed-location Ledgers simulation: 3 diskful nodes plus 1 diskless node, rolling ledgers between
    /// <see cref="LedgerLocation.Nodes"/> and <see cref="LedgerLocation.ObjectStore"/>, exercising concurrent proposals,
    /// splits, merges, diskless primary takeovers, drops, and <see cref="ObjectLedgerSweeper"/>.
    /// </summary>
    private sealed class LedgersScenario(
        SimulationEnvironment environment,
        SimulatedLedgerReplicaTransport replicas,
        SimulatedConditionalRegister register,
        SimulatedObjectStore objects,
        ObjectLedgerStore objectLedgers,
        LedgerStoreNode[] diskful,
        LedgerMapCommitter[] committers,
        bool fullWorkload,
        CancellationToken cancellationToken)
    {
        private sealed class Process(string name, NodeId node, bool diskless, LedgerMapView view, ILedgerReplicaTransport transport, ObjectLedgerStore objectStore) : IAsyncDisposable
        {
            private bool _disposed;

            public string Name { get; } = name;

            public NodeId Node { get; } = node;

            public bool Diskless { get; } = diskless;

            public bool Stopped { get; private set; }

            public LedgerMapView View { get; } = view;

            public LedgerReader Reader { get; } = new(view, transport, SimulationEnvironment.Clock, node) { Objects = objectStore };

            public LedgerWriterRegistry Registry { get; } = new(new LedgerReader(view, transport, SimulationEnvironment.Clock, node) { Objects = objectStore }, 1 << 25, SmallSegments.Options, SimulationEnvironment.Clock, NullLoggerFactory.Instance, SmallSegments.Random(name));

            public TransitionExecutor Executor => field ??= new TransitionExecutor(Registry, NullLogger<TransitionExecutor>.Instance);

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
                await Registry.DisposeAsync();
                await Reader.DisposeAsync();
            }
        }

        private readonly record struct AnsweredProposal(LedgerId Ledger, byte Key, long CalledAt, long AnsweredAt, byte[] Body, ProposalOutcome Outcome, Slot? Slot);

        private readonly SimulationRandom _random = environment.Random;
        private readonly List<Process> _processes = [];
        private readonly List<AnsweredProposal> _answered = [];
        private readonly HashSet<LedgerId> _traced = [];
        private readonly HashSet<LedgerId> _sweptObjectLedgers = [];
        private LedgerMapValue _previous = LedgerMapValue.Empty;
        private int _checked;
        private long _steps;
        private bool _running = true;
        private bool _sawMixed;
        private bool _rolledNodesToObject;
        private bool _rolledObjectToNodes;
        private bool _disklessServedObjectLedger;

        public async Task RunAsync()
        {
            environment.Invariants.Add("mixed-ledger-map", CheckMap);
            try
            {
                for (var index = 0; index < diskful.Length; index++)
                {
                    var view = await RetryAsync(() => LedgerMapView.OpenAsync(register, cancellationToken), cancellationToken);
                    _processes.Add(new Process($"diskful-{index}", diskful[index].NodeId, diskless: false, view, new ClientTransport(replicas, $"diskful-{index}"), objectLedgers));
                }

                // A 4th node with no LedgerStoreNode: diskless, serving only object-store ledgers.
                var disklessNode = new NodeId(new Guid(0xD15C_0001, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1));
                var disklessView = await RetryAsync(() => LedgerMapView.OpenAsync(register, cancellationToken), cancellationToken);
                _processes.Add(new Process("diskless-0", disklessNode, diskless: true, disklessView, new ClientTransport(replicas, "diskless-0"), objectLedgers));

                // Bootstrap on Nodes with all 3 diskful replicas.
                var initialReplicas = diskful.Select(static node => node.NodeId).ToImmutableArray();
                _ = await ApplyTransitionAsync(_processes[0], new Transition.Bootstrap(new TransitionChoice(_processes[0].Node, new LedgerLocation.Nodes(initialReplicas), Window, chosenAt: 0, epoch: 0)));

                var proposing = SimulationTasks.WhenAll(_processes.Select(ProposeLoopAsync));
                var following = SimulationTasks.WhenAll(_processes.Select(FollowLoopAsync));

                await RunTransitionsAsync();

                // Heal faults before quiescence, final verification, drops, and sweep.
                Heal();
                _running = false;
                await proposing;
                await following;

                await RefreshAllViewsAsync();
                await VerifyAllLedgersAsync();
                await DropAndSweepAllClosedAsync();

                Assert.True(_sawMixed, "The simulation must hold Nodes and ObjectStore open ledgers concurrently.");
                Assert.True(_rolledNodesToObject, "The simulation must roll a ledger from Nodes to ObjectStore.");
                Assert.True(_rolledObjectToNodes, "The simulation must roll a ledger from ObjectStore to Nodes.");
                Assert.True(_disklessServedObjectLedger, "The diskless node must serve an open object-store ledger.");
                Assert.NotEmpty(_sweptObjectLedgers);
                environment.Trace.Record("mixed ledgers verified");
            }
            finally
            {
                foreach (var process in _processes)
                {
                    await process.DisposeAsync();
                }
            }
        }

        private string? CheckMap()
        {
            for (; _checked < register.AppliedWrites.Count; _checked++)
            {
                if (LedgerMapValue.Decode(register.AppliedWrites[_checked].AsSpan()).Value is not LedgerMapValue value)
                {
                    return Invariant($"register applied a non-map value at write {_checked}");
                }

                if (value.Epoch != _previous.Epoch + 1)
                {
                    return Invariant($"epoch {value.Epoch} follows epoch {_previous.Epoch}");
                }

                if (LedgerMapReference.Violation(_previous, value) is { } violation)
                {
                    return violation;
                }

                replicas.LedgerTrace.RecordCloses(_previous, value, node => diskful.Single(candidate => candidate.NodeId == node).Address);

                if (HasBothLocationsOpen(value))
                {
                    _sawMixed = true;
                }

                foreach (var opened in value.Ledgers.Where(ledger => ledger.IsOpen && _previous.Get(ledger.Id) is null))
                {
                    if (opened.Location.Value is LedgerLocation.Nodes nodesLocation && nodesLocation.Replicas.Any(replica => _processes.Single(process => process.Node == replica).Diskless))
                    {
                        return Invariant($"diskless node placed as replica of node-backed ledger {opened.Id}");
                    }

                    foreach (var closed in value.Ledgers.Where(ledger => !ledger.IsOpen && _previous.Get(ledger.Id)?.IsOpen == true && ledger.Range.Overlaps(opened.Range)))
                    {
                        if (closed.Location.Value is LedgerLocation.Nodes && opened.Location.Value is LedgerLocation.ObjectStore)
                        {
                            _rolledNodesToObject = true;
                        }
                        else if (closed.Location.Value is LedgerLocation.ObjectStore && opened.Location.Value is LedgerLocation.Nodes)
                        {
                            _rolledObjectToNodes = true;
                        }
                    }
                }

                _previous = value;
            }

            return null;
        }

        private async Task RunTransitionsAsync()
        {
            var diskless = _processes.Single(static process => process.Diskless);
            var diskfulProcesses = _processes.Where(static process => !process.Diskless).ToArray();
            var allDiskful = diskfulProcesses.Select(static process => process.Node).ToImmutableArray();

            // 1. Split Root (Nodes) into left & right on Nodes.
            var map = await RetryAsync(() => _processes[0].View.ReadAsync(cancellationToken), cancellationToken);
            var root = map.Ledgers.Single(static ledger => ledger.IsOpen);
            _ = await ApplyTransitionAsync(diskfulProcesses[0], new Transition.Split(root.Id, new TransitionChoice(diskfulProcesses[0].Node, new LedgerLocation.Nodes(allDiskful), Window, chosenAt: 0, epoch: map.Epoch)));

            await Task.Delay(TimeSpan.FromMilliseconds(25), SimulationEnvironment.Clock, cancellationToken);

            // 2. Roll left half from Nodes -> ObjectStore onto the diskless node -> mixed map (ObjectStore + Nodes open simultaneously!).
            map = await RetryAsync(() => diskless.View.ReadAsync(cancellationToken), cancellationToken);
            var open = OpenByRange(map);
            Assert.Equal(2, open.Length);
            _ = await ApplyTransitionAsync(diskless, new Transition.Replace(open[0].Id, new TransitionChoice(diskless.Node, LedgerLocation.ObjectStore.Instance, Window, chosenAt: 0, epoch: map.Epoch)));
            await RefreshAllViewsAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(35), SimulationEnvironment.Clock, cancellationToken);

            if (fullWorkload)
            {
                // 3. Stop/crash the diskless primary while writes are in flight, then take over its ObjectStore ledger
                // onto diskful[1] while the other half stays on Nodes.
                map = await RetryAsync(() => diskfulProcesses[1].View.ReadAsync(cancellationToken), cancellationToken);
                var objLedger = map.Ledgers.Single(static ledger => ledger.IsOpen && ledger.Location.Value is LedgerLocation.ObjectStore);
                var inFlight = ProposeOnceAsync(diskless, objLedger);
                await diskless.StopAsync();
                await inFlight;

                _ = await ApplyTransitionAsync(diskfulProcesses[1], new Transition.Replace(objLedger.Id, new TransitionChoice(diskfulProcesses[1].Node, LedgerLocation.ObjectStore.Instance, Window, chosenAt: 0, epoch: map.Epoch)));
                await Task.Delay(TimeSpan.FromMilliseconds(25), SimulationEnvironment.Clock, cancellationToken);

                // 4. Restart a fresh diskless node (fresh NodeId) and split the ObjectStore ledger onto it.
                var restartedNode = new NodeId(new Guid(0xD15C_0001, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3));
                var restartedView = await RetryAsync(() => LedgerMapView.OpenAsync(register, cancellationToken), cancellationToken);
                var restartedDiskless = new Process("diskless-1", restartedNode, diskless: true, restartedView, new ClientTransport(replicas, "diskless-1"), objectLedgers);
                _processes.Add(restartedDiskless);

                map = await RetryAsync(() => restartedDiskless.View.ReadAsync(cancellationToken), cancellationToken);
                objLedger = map.Ledgers.Single(static ledger => ledger.IsOpen && ledger.Location.Value is LedgerLocation.ObjectStore);
                _ = await ApplyTransitionAsync(restartedDiskless, new Transition.Split(objLedger.Id, new TransitionChoice(restartedDiskless.Node, LedgerLocation.ObjectStore.Instance, Window, chosenAt: 0, epoch: map.Epoch)));
                await Task.Delay(TimeSpan.FromMilliseconds(25), SimulationEnvironment.Clock, cancellationToken);

                // 5. Merge the two ObjectStore siblings back onto diskful[2] as a single Nodes ledger (ObjectStore -> Nodes roll!).
                map = await RetryAsync(() => diskfulProcesses[2].View.ReadAsync(cancellationToken), cancellationToken);
                var objSiblings = OpenObjectByRange(map);
                Assert.Equal(2, objSiblings.Length);
                ImmutableArray<NodeId> rotatedReplicas = [diskfulProcesses[2].Node, diskfulProcesses[0].Node, diskfulProcesses[1].Node];
                _ = await ApplyTransitionAsync(diskfulProcesses[2], new Transition.Merge(objSiblings[0].Id, objSiblings[1].Id, new TransitionChoice(diskfulProcesses[2].Node, new LedgerLocation.Nodes(rotatedReplicas), Window, chosenAt: 0, epoch: map.Epoch)));
                await Task.Delay(TimeSpan.FromMilliseconds(25), SimulationEnvironment.Clock, cancellationToken);
            }
            else
            {
                // Smaller replay path: stop the diskless primary with a write in flight, then roll back to Nodes.
                map = await RetryAsync(() => diskfulProcesses[1].View.ReadAsync(cancellationToken), cancellationToken);
                var objLedger = map.Ledgers.Single(static ledger => ledger.IsOpen && ledger.Location.Value is LedgerLocation.ObjectStore);
                var inFlight = ProposeOnceAsync(diskless, objLedger);
                await diskless.StopAsync();
                await inFlight;

                ImmutableArray<NodeId> rotatedReplicas = [diskfulProcesses[1].Node, diskfulProcesses[0].Node, diskfulProcesses[2].Node];
                _ = await ApplyTransitionAsync(diskfulProcesses[1], new Transition.Replace(objLedger.Id, new TransitionChoice(diskfulProcesses[1].Node, new LedgerLocation.Nodes(rotatedReplicas), Window, chosenAt: 0, epoch: map.Epoch)));
                await Task.Delay(TimeSpan.FromMilliseconds(20), SimulationEnvironment.Clock, cancellationToken);
            }
        }

        private async Task ProposeOnceAsync(Process process, LedgerMetadata ledger)
        {
            var writer = process.Registry.TryGet(ledger.Id);
            if (writer is null)
            {
                return;
            }

            if (process.Diskless && ledger.Location.Value is LedgerLocation.ObjectStore)
            {
                _disklessServedObjectLedger = true;
            }

            var key = KeyIn(ledger.Range);
            var body = Encoding.UTF8.GetBytes(Invariant($"{process.Name}:{++_steps}"));
            var calledAt = ++_steps;
            ProposalOutcome outcome;
            var versions = new List<RowVersion>(1);
            try
            {
                using var writerBuffer = new ArcBufferWriter();
                writerBuffer.Write(body);
                using var slice = writerBuffer.PeekSlice(writerBuffer.Length);
                using var row = new Row([key], RowKind.Append, slice);
                outcome = await writer.ProposeAsync([row], versions, cancellationToken);
            }
            catch (ObjectDisposedException) when (process.Stopped)
            {
                return;
            }

            var answeredAt = ++_steps;
            Slot? slot = outcome.TryGetValue(out ProposalOutcome.Durable durable) ? durable.Slot : null;
            _answered.Add(new AnsweredProposal(ledger.Id, key, calledAt, answeredAt, body, outcome, slot));
        }

        private async Task<LedgerMapCommitOutcome> ApplyTransitionAsync(Process executor, Transition transition)
        {
            var outcome = await RetryTransitionAsync(executor.Executor, transition, committers[0], cancellationToken);
            Assert.IsType<LedgerMapCommitOutcome.Applied>(outcome.Value);
            return outcome;
        }

        private async Task FollowLoopAsync(Process process)
        {
            while (_running && !process.Stopped)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), SimulationEnvironment.Clock, cancellationToken);
                if (process.Stopped)
                {
                    return;
                }

                try
                {
                    _ = await process.View.ReadAsync(cancellationToken);
                }
                catch (Exception failure) when (failure is IOException or TimeoutException or ObjectDisposedException)
                {
                }
            }
        }

        private async Task ProposeLoopAsync(Process process)
        {
            var target = fullWorkload ? 12 : 4;
            var count = 0;
            while (_running && !process.Stopped && count < target)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(2, 12)), SimulationEnvironment.Clock, cancellationToken);
                if (process.Stopped)
                {
                    return;
                }

                var map = process.View.Current;
                var mine = map.Ledgers.Where(ledger => ledger.IsOpen && ledger.Primary.Node == process.Node).ToArray();
                if (mine.Length == 0)
                {
                    continue;
                }

                var ledger = _random.Choose(mine);
                var writer = process.Registry.TryGet(ledger.Id);
                if (writer is null)
                {
                    continue;
                }

                if (ledger.Location.Value is LedgerLocation.Nodes && _traced.Add(ledger.Id))
                {
                    replicas.LedgerTrace.TraceWriter(writer, process.Name, ledger.Id);
                }

                if (process.Diskless && ledger.Location.Value is LedgerLocation.ObjectStore)
                {
                    _disklessServedObjectLedger = true;
                }

                var key = KeyIn(ledger.Range);
                var body = Encoding.UTF8.GetBytes(Invariant($"{process.Name}:{++_steps}"));
                var calledAt = ++_steps;
                ProposalOutcome outcome;
                var versions = new List<RowVersion>(1);
                try
                {
                    using var writerBuffer = new ArcBufferWriter();
                    writerBuffer.Write(body);
                    using var slice = writerBuffer.PeekSlice(writerBuffer.Length);
                    using var row = new Row([key], RowKind.Append, slice);
                    outcome = await writer.ProposeAsync([row], versions, cancellationToken);
                }
                catch (ObjectDisposedException) when (process.Stopped)
                {
                    return;
                }

                var answeredAt = ++_steps;
                Slot? slot = outcome.TryGetValue(out ProposalOutcome.Durable durable) ? durable.Slot : null;
                _answered.Add(new AnsweredProposal(ledger.Id, key, calledAt, answeredAt, body, outcome, slot));
                count++;
            }
        }

        private byte KeyIn(KeyRange range)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var candidate = (byte)_random.Next(0, 256);
                if (range.Contains([candidate]))
                {
                    return candidate;
                }
            }

            for (var candidate = 0; candidate < 256; candidate++)
            {
                if (range.Contains([(byte)candidate]))
                {
                    return (byte)candidate;
                }
            }

            throw new InvalidOperationException($"No single-byte key lies in {range}.");
        }

        private void Heal()
        {
            environment.Network.MessageDropRate = 0;
            environment.Rpc.DuplicationRate = 0;
            register.UnknownOutcomeRate = 0;
            register.AppliedMismatchRate = 0;
            register.ReadFailureRate = 0;
            objects.UnknownOutcomeRate = 0;
            objects.RetriedAnswerRate = 0;
            objects.ReadFailureRate = 0;
        }

        private async Task RefreshAllViewsAsync()
        {
            foreach (var process in _processes)
            {
                if (!process.Stopped)
                {
                    _ = await RetryAsync(() => process.View.ReadAsync(cancellationToken), cancellationToken);
                }
            }
        }

        private async Task VerifyAllLedgersAsync()
        {
            var map = await _processes[0].View.ReadAsync(cancellationToken);
            var reader = _processes[0].Reader;
            Dictionary<LedgerId, Dictionary<Slot, byte[]>> decided = [];

            foreach (var ledger in map.Ledgers)
            {
                Slot end;
                if (ledger.Status.Value is LedgerStatus.Closed closed)
                {
                    end = closed.T;
                }
                else
                {
                    var tail = Assert.IsType<LedgerTail>((await reader.ReadTailAsync(ledger, cancellationToken)).Value);
                    end = tail.ContiguousTail;
                }

                Dictionary<Slot, byte[]> slots = [];
                using var view = new LedgerSegmentView(ledger.Id);
                var from = Slot.Zero;
                while (from < end)
                {
                    await foreach (var item in reader.ScanAsync(ledger, from, end, view, cancellationToken: cancellationToken))
                    {
                        Assert.True(item.Read.TryGetValue(out LedgerRead.Data data), Invariant($"Expected Data at s{ledger.Id}[{from}], got {item.Read}."));
                        using (data)
                        {
                            foreach (var row in item.Rows)
                            {
                                slots[data.Slot] = row.Body.ToArray();
                                break;
                            }
                        }

                        from = data.Slot + 1;
                    }
                }

                decided[ledger.Id] = slots;
            }

            // LG-I5: every durable proposal matches its decided slot's exact row body.
            foreach (var item in _answered.Where(static entry => entry.Slot is not null))
            {
                Assert.True(decided.TryGetValue(item.Ledger, out var slots), Invariant($"s{item.Ledger} missing from decided map."));
                Assert.True(slots.TryGetValue(item.Slot!.Value, out var actual), Invariant($"s{item.Ledger}[{item.Slot}] missing from decided ledger."));
                Assert.Equal(item.Body, actual);
            }

            // LG-I6: for every key, a durable proposal answered before a later durable proposal was called holds a strictly lower tag.
            var durable = _answered.Where(static entry => entry.Slot is not null).ToArray();
            foreach (var earlier in durable)
            {
                var earlierTag = new Tag(earlier.Ledger, earlier.Slot!.Value, 0);
                foreach (var later in durable)
                {
                    if (earlier.Key == later.Key && earlier.AnsweredAt < later.CalledAt)
                    {
                        var laterTag = new Tag(later.Ledger, later.Slot!.Value, 0);
                        Assert.True(earlierTag < laterTag, Invariant($"LG-I6 violated for key {earlier.Key}: earlier {earlierTag} !< later {laterTag}."));
                    }
                }
            }
        }

        private async Task DropAndSweepAllClosedAsync()
        {
            while (true)
            {
                var map = await _processes[0].View.ReadAsync(cancellationToken);
                LedgerMetadata[] droppable = [.. map.Ledgers.Where(ledger => !ledger.IsOpen && !map.Ledgers.Any(lower => lower.Id < ledger.Id && lower.Range.Overlaps(ledger.Range)))];
                if (droppable.Length == 0)
                {
                    break;
                }

                foreach (var ledger in droppable)
                {
                    var outcome = await committers[1].CommitAsync(new LedgerMapChange.Drop(ledger.Id), cancellationToken);
                    Assert.IsType<LedgerMapCommitOutcome.Applied>(outcome.Value);
                    if (ledger.Location.Value is LedgerLocation.ObjectStore)
                    {
                        _ = _sweptObjectLedgers.Add(ledger.Id);
                    }
                }
            }

            var finalMap = await _processes[0].View.ReadAsync(cancellationToken);
            var ledgersWithLiveSlots = await FindLedgersWithLiveSlotsAsync(objects, _sweptObjectLedgers, cancellationToken);
            var sweeper = new ObjectLedgerSweeper(objectLedgers);
            _ = await sweeper.SweepAsync(finalMap, cancellationToken);

            // OL-I4, OL-I5, OL-I8: non-vacuously verify that batch objects existed before sweep and every swept
            // object-store ledger now has its dropped marker and zero live batch slot objects.
            await AssertSweptCleanlyAsync(objects, ledgersWithLiveSlots, cancellationToken);
        }
    }

    /// <summary>
    /// Mixed-location Journals simulation: 3 diskful nodes plus 1 diskless node, each running a <see cref="JournalStore"/>
    /// over a shared <see cref="SimulatedArchiveStore"/> and mixed <see cref="LedgerLocation.Nodes"/> /
    /// <see cref="LedgerLocation.ObjectStore"/> ledgers, checking Porcupine linearizability (<see cref="JournalModel"/>),
    /// token chains (<see cref="TokenChainChecker"/>), and final folds across rolls and drains.
    /// </summary>
    private sealed class JournalsScenario(
        SimulationEnvironment environment,
        SimulatedLedgerReplicaTransport replicas,
        SimulatedConditionalRegister register,
        SimulatedObjectStore objects,
        ObjectLedgerStore objectLedgers,
        LedgerStoreNode[] diskful,
        LedgerMapCommitter[] committers,
        SimulatedArchiveStore archive,
        bool fullWorkload,
        CancellationToken cancellationToken)
    {
        private sealed record ReferenceRow(string Journal, Tag Tag, RowKind Kind, RowVersion Version, RowToken Token, LedgerSegmentId Segment, byte[] Body)
        {
            public ChainRow Chain => new(Tag, Segment, Kind, Body.Length, Kind == RowKind.Delete ? Body : ReadOnlyMemory<byte>.Empty, Token, Version, Held: false);
        }

        private sealed class Process : IAsyncDisposable
        {
            private bool _disposed;

            public Process(string name, NodeId node, bool diskless, LedgerMapView view, ILedgerReplicaTransport transport, ObjectLedgerStore objectStore, IArchiveStore store, LedgerTrace trace)
            {
                Name = name;
                Node = node;
                Diskless = diskless;
                View = view;
                Reader = new LedgerReader(view, transport, SimulationEnvironment.Clock, node) { Objects = objectStore };
                Registry = new LedgerWriterRegistry(Reader, 1 << 25, SmallSegments.Options, SimulationEnvironment.Clock, NullLoggerFactory.Instance, SmallSegments.Random(name));
                Executor = new TransitionExecutor(Registry, NullLogger<TransitionExecutor>.Instance);
                var options = new JournalStoreOptions
                {
                    MaxBodyBytes = 64,
                    MaxBatchEntries = 4,
                    MaxBatchBytes = 256,
                    MaxQueuedBytes = 1 << 20,
                    WarmStateBytes = 2048,
                    TombstoneRetention = Retention,
                };
                Journals = new JournalStore(Registry, store, options, SimulationEnvironment.Clock)
                {
                    OnAdmission = (ledger, writer) => trace.TraceWriter(writer, name, ledger),
                };
            }

            public string Name { get; }

            public NodeId Node { get; }

            public bool Diskless { get; }

            public bool Stopped { get; private set; }

            public LedgerMapView View { get; }

            public LedgerReader Reader { get; }

            public LedgerWriterRegistry Registry { get; }

            public TransitionExecutor Executor { get; }

            public JournalStore Journals { get; }

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

        private static readonly string[] JournalNames = ["alpha", "beta", "gamma", "delta"];

        private readonly SimulationRandom _random = environment.Random;
        private readonly List<Process> _processes = [];
        private readonly HistoryRecorder<JournalInput, JournalOutput> _history = new(environment.TimeProvider);
        private readonly Dictionary<string, string> _journalsByKey = JournalNames.ToDictionary(static name => Convert.ToHexString(JournalRowKey.Encode(name).Span), static name => name, StringComparer.Ordinal);
        private readonly Dictionary<LedgerId, ReferenceRow[]> _decided = [];
        private readonly HashSet<LedgerId> _dropped = [];
        private readonly HashSet<LedgerId> _drainedNodes = [];
        private readonly HashSet<LedgerId> _drainedObjects = [];
        private LedgerMapValue _previous = LedgerMapValue.Empty;
        private int _checked;
        private bool _running = true;
        private bool _faulting = true;
        private bool _sawMixed;
        private bool _rolledNodesToObject;
        private bool _rolledObjectToNodes;

        public async Task RunAsync()
        {
            environment.Invariants.Add("mixed-journal-map", CheckMap);
            try
            {
                for (var index = 0; index < diskful.Length; index++)
                {
                    var view = await RetryAsync(() => LedgerMapView.OpenAsync(register, cancellationToken), cancellationToken);
                    _processes.Add(new Process($"diskful-{index}", diskful[index].NodeId, diskless: false, view, new ClientTransport(replicas, $"diskful-{index}"), objectLedgers, archive, replicas.LedgerTrace));
                }

                var disklessNode = new NodeId(new Guid(0xD15C_0002, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2));
                var disklessView = await RetryAsync(() => LedgerMapView.OpenAsync(register, cancellationToken), cancellationToken);
                _processes.Add(new Process("diskless-0", disklessNode, diskless: true, disklessView, new ClientTransport(replicas, "diskless-0"), objectLedgers, archive, replicas.LedgerTrace));

                var initialReplicas = diskful.Select(static node => node.NodeId).ToImmutableArray();
                _ = await ApplyTransitionAsync(_processes[0], new Transition.Bootstrap(new TransitionChoice(_processes[0].Node, new LedgerLocation.Nodes(initialReplicas), Window, chosenAt: 0, epoch: 0)));
                await RefreshAllViewsAsync();

                var following = SimulationTasks.WhenAll(_processes.Select(FollowLoopAsync));
                var draining = DrainLoopAsync();
                var writers = SimulationTasks.WhenAll(Enumerable.Range(0, JournalNames.Length).Select(WriteLoopAsync));
                var readers = SimulationTasks.WhenAll(Enumerable.Range(JournalNames.Length, 2).Select(ReadLoopAsync));

                await RunTransitionsAsync();
                await writers;
                await readers;

                Heal();
                _running = false;
                await draining;
                await following;

                await RefreshAllViewsAsync();
                var finalMap = await _processes[0].View.ReadAsync(cancellationToken);
                var ledgersWithLiveSlots = await FindLedgersWithLiveSlotsAsync(objects, _drainedObjects, cancellationToken);
                var sweeper = new ObjectLedgerSweeper(objectLedgers);
                _ = await sweeper.SweepAsync(finalMap, cancellationToken);
                await AssertSweptCleanlyAsync(objects, ledgersWithLiveSlots, cancellationToken);

                await CaptureRemainingOpenLedgersAsync(finalMap);
                await VerifyFinalStateAsync();
                LinearizabilityAssert.Linearizable(JournalModel.Instance, _history.History(), cancellationToken: cancellationToken);

                Assert.True(_sawMixed, "The simulation must hold Nodes and ObjectStore open ledgers concurrently.");
                Assert.True(_rolledNodesToObject, "The simulation must roll a ledger from Nodes to ObjectStore.");
                Assert.True(_rolledObjectToNodes, "The simulation must roll a ledger from ObjectStore to Nodes.");
                Assert.NotEmpty(_drainedNodes);
                Assert.NotEmpty(_drainedObjects);
                environment.Trace.Record("mixed journals verified");
            }
            finally
            {
                foreach (var process in _processes)
                {
                    await process.DisposeAsync();
                }
            }
        }

        private string? CheckMap()
        {
            for (; _checked < register.AppliedWrites.Count; _checked++)
            {
                if (LedgerMapValue.Decode(register.AppliedWrites[_checked].AsSpan()).Value is not LedgerMapValue value)
                {
                    return Invariant($"register applied a non-map value at write {_checked}");
                }

                if (value.Epoch != _previous.Epoch + 1)
                {
                    return Invariant($"epoch {value.Epoch} follows epoch {_previous.Epoch}");
                }

                if (LedgerMapReference.Violation(_previous, value) is { } violation)
                {
                    return violation;
                }

                replicas.LedgerTrace.RecordCloses(_previous, value, node => diskful.Single(candidate => candidate.NodeId == node).Address);

                if (HasBothLocationsOpen(value))
                {
                    _sawMixed = true;
                }

                foreach (var opened in value.Ledgers.Where(ledger => ledger.IsOpen && _previous.Get(ledger.Id) is null))
                {
                    if (opened.Location.Value is LedgerLocation.Nodes nodesLocation && nodesLocation.Replicas.Any(replica => _processes.Single(process => process.Node == replica).Diskless))
                    {
                        return Invariant($"diskless node placed as replica of node-backed ledger {opened.Id}");
                    }

                    foreach (var closed in value.Ledgers.Where(ledger => !ledger.IsOpen && _previous.Get(ledger.Id)?.IsOpen == true && ledger.Range.Overlaps(opened.Range)))
                    {
                        if (closed.Location.Value is LedgerLocation.Nodes && opened.Location.Value is LedgerLocation.ObjectStore)
                        {
                            _rolledNodesToObject = true;
                        }
                        else if (closed.Location.Value is LedgerLocation.ObjectStore && opened.Location.Value is LedgerLocation.Nodes)
                        {
                            _rolledObjectToNodes = true;
                        }
                    }
                }

                _previous = value;
            }

            return null;
        }

        private async Task RunTransitionsAsync()
        {
            var diskless = _processes.Single(static process => process.Diskless);
            var diskfulProcesses = _processes.Where(static process => !process.Diskless).ToArray();
            var allDiskful = diskfulProcesses.Select(static process => process.Node).ToImmutableArray();

            // 1. Split Root on Nodes so we can place one half in ObjectStore and keep the other on Nodes.
            var map = await RetryAsync(() => diskfulProcesses[0].View.ReadAsync(cancellationToken), cancellationToken);
            var root = map.Ledgers.Single(static ledger => ledger.IsOpen);
            _ = await ApplyTransitionAsync(diskfulProcesses[0], new Transition.Split(root.Id, new TransitionChoice(diskfulProcesses[0].Node, new LedgerLocation.Nodes(allDiskful), Window, chosenAt: 0, epoch: map.Epoch)));
            await Task.Delay(TimeSpan.FromMilliseconds(30), SimulationEnvironment.Clock, cancellationToken);

            // 2. Roll left half Nodes -> ObjectStore onto the diskless node.
            map = await RetryAsync(() => diskless.View.ReadAsync(cancellationToken), cancellationToken);
            var open = OpenByRange(map);
            _ = await ApplyTransitionAsync(diskless, new Transition.Replace(open[0].Id, new TransitionChoice(diskless.Node, LedgerLocation.ObjectStore.Instance, Window, chosenAt: 0, epoch: map.Epoch)));
            await Task.Delay(TimeSpan.FromMilliseconds(35), SimulationEnvironment.Clock, cancellationToken);

            if (fullWorkload)
            {
                // 3. Stop/crash the diskless primary while journal writers have writes in flight, then take over
                // its ObjectStore ledger onto diskful[1] while the right half stays on Nodes.
                await diskless.StopAsync();

                map = await RetryAsync(() => diskfulProcesses[1].View.ReadAsync(cancellationToken), cancellationToken);
                var objLedger = map.Ledgers.Single(static ledger => ledger.IsOpen && ledger.Location.Value is LedgerLocation.ObjectStore);
                _ = await ApplyTransitionAsync(diskfulProcesses[1], new Transition.Replace(objLedger.Id, new TransitionChoice(diskfulProcesses[1].Node, LedgerLocation.ObjectStore.Instance, Window, chosenAt: 0, epoch: map.Epoch)));
                await Task.Delay(TimeSpan.FromMilliseconds(35), SimulationEnvironment.Clock, cancellationToken);

                // 4. Restart a fresh diskless node (fresh NodeId), roll right half Nodes -> ObjectStore onto it,
                // and roll left half ObjectStore -> Nodes onto diskful[2].
                var restartedNode = new NodeId(new Guid(0xD15C_0002, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4));
                var restartedView = await RetryAsync(() => LedgerMapView.OpenAsync(register, cancellationToken), cancellationToken);
                var restartedDiskless = new Process("diskless-1", restartedNode, diskless: true, restartedView, new ClientTransport(replicas, "diskless-1"), objectLedgers, archive, replicas.LedgerTrace);
                _processes.Add(restartedDiskless);

                map = await RetryAsync(() => restartedDiskless.View.ReadAsync(cancellationToken), cancellationToken);
                var nodesLedger = map.Ledgers.Single(static ledger => ledger.IsOpen && ledger.Location.Value is LedgerLocation.Nodes);
                _ = await ApplyTransitionAsync(restartedDiskless, new Transition.Replace(nodesLedger.Id, new TransitionChoice(restartedDiskless.Node, LedgerLocation.ObjectStore.Instance, Window, chosenAt: 0, epoch: map.Epoch)));

                map = await RetryAsync(() => diskfulProcesses[2].View.ReadAsync(cancellationToken), cancellationToken);
                objLedger = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Location.Value is LedgerLocation.ObjectStore && ledger.Primary.Node != restartedDiskless.Node);
                ImmutableArray<NodeId> rotated = [diskfulProcesses[2].Node, diskfulProcesses[0].Node, diskfulProcesses[1].Node];
                _ = await ApplyTransitionAsync(diskfulProcesses[2], new Transition.Replace(objLedger.Id, new TransitionChoice(diskfulProcesses[2].Node, new LedgerLocation.Nodes(rotated), Window, chosenAt: 0, epoch: map.Epoch)));
                await Task.Delay(TimeSpan.FromMilliseconds(35), SimulationEnvironment.Clock, cancellationToken);

                // 5. Merge the mixed pair back into a single Nodes ledger (exercises mixed-location Merge -> Nodes).
                map = await RetryAsync(() => diskfulProcesses[0].View.ReadAsync(cancellationToken), cancellationToken);
                open = OpenByRange(map);
                _ = await ApplyTransitionAsync(diskfulProcesses[0], new Transition.Merge(open[0].Id, open[1].Id, new TransitionChoice(diskfulProcesses[0].Node, new LedgerLocation.Nodes(allDiskful), Window, chosenAt: 0, epoch: map.Epoch)));
            }
            else
            {
                // Smaller replay path: stop the diskless primary while writes are in flight, then roll back to Nodes.
                await diskless.StopAsync();

                map = await RetryAsync(() => diskfulProcesses[1].View.ReadAsync(cancellationToken), cancellationToken);
                var objLedger = map.Ledgers.Single(static ledger => ledger.IsOpen && ledger.Location.Value is LedgerLocation.ObjectStore);
                ImmutableArray<NodeId> rotated = [diskfulProcesses[1].Node, diskfulProcesses[0].Node, diskfulProcesses[2].Node];
                _ = await ApplyTransitionAsync(diskfulProcesses[1], new Transition.Replace(objLedger.Id, new TransitionChoice(diskfulProcesses[1].Node, new LedgerLocation.Nodes(rotated), Window, chosenAt: 0, epoch: map.Epoch)));
            }
        }

        private async Task<LedgerMapCommitOutcome> ApplyTransitionAsync(Process executor, Transition transition)
        {
            var outcome = await RetryTransitionAsync(executor.Executor, transition, committers[0], cancellationToken);
            Assert.IsType<LedgerMapCommitOutcome.Applied>(outcome.Value);
            return outcome;
        }

        private async Task FollowLoopAsync(Process process)
        {
            while (_running && !process.Stopped)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), SimulationEnvironment.Clock, cancellationToken);
                if (process.Stopped)
                {
                    return;
                }

                try
                {
                    _ = await process.View.ReadAsync(cancellationToken);
                }
                catch (Exception failure) when (failure is IOException or TimeoutException or ObjectDisposedException)
                {
                }
            }
        }

        private async Task WriteLoopAsync(int client)
        {
            var journal = JournalNames[client % JournalNames.Length];
            var key = JournalRowKey.Encode(journal);
            var version = JournalVersion.Origin;
            var writes = fullWorkload ? 8 : 3;

            for (var index = 0; index < writes; index++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(5, 18)), SimulationEnvironment.Clock, cancellationToken);
                var kind = index > 0 && index % 4 == 0
                    ? JournalEntryKind.Replace
                    : index > 0 && index % 6 == 0
                        ? JournalEntryKind.Delete
                        : JournalEntryKind.Append;
                byte[] payload = kind == JournalEntryKind.Delete
                    ? []
                    : Encoding.UTF8.GetBytes(Invariant($"{journal}-{index}|"));

                var input = kind switch
                {
                    JournalEntryKind.Append => JournalInput.Append(journal, version.ToString(), payload),
                    JournalEntryKind.Replace => JournalInput.Replace(journal, version.ToString(), payload),
                    JournalEntryKind.Delete => JournalInput.Delete(journal, version.ToString()),
                    JournalEntryKind.Expired => throw new InvalidOperationException(),
                    _ => throw new InvalidOperationException(),
                };

                var invocation = _history.Invoke(client, input);
                var (outcome, deposed) = await ExecuteWriteAsync(key, kind, version, payload);
                switch (outcome.Value)
                {
                    case JournalWriteOutcome.Accepted accepted:
                        _history.Complete(invocation, JournalOutput.Accepted(accepted.Version.ToString()));
                        version = accepted.Version;
                        break;
                    case JournalWriteOutcome.Rejected rejected:
                        _history.Complete(invocation, JournalOutput.Rejected(rejected.CurrentVersion.ToString(), deposed));
                        version = rejected.CurrentVersion;
                        break;
                    case JournalWriteOutcome.ReplaceRequired:
                        {
                            _history.Complete(invocation, JournalOutput.ReplaceRequiredBy(deposed));
                            var snapshot = Encoding.UTF8.GetBytes(Invariant($"snap-{journal}-{index}|"));
                            var replaceInvocation = _history.Invoke(client, JournalInput.Replace(journal, version.ToString(), snapshot));
                            var (replaceOutcome, replaceDeposed) = await ExecuteWriteAsync(key, JournalEntryKind.Replace, version, snapshot);
                            switch (replaceOutcome.Value)
                            {
                                case JournalWriteOutcome.Accepted replaced:
                                    _history.Complete(replaceInvocation, JournalOutput.Accepted(replaced.Version.ToString()));
                                    version = replaced.Version;
                                    break;
                                case JournalWriteOutcome.Rejected replaceRejected:
                                    _history.Complete(replaceInvocation, JournalOutput.Rejected(replaceRejected.CurrentVersion.ToString(), replaceDeposed));
                                    version = replaceRejected.CurrentVersion;
                                    break;
                                default:
                                    _history.CompleteUnbounded(replaceInvocation, JournalOutput.Ambiguous);
                                    version = (await ReadOnceAsync(client + 100, journal)).Version;
                                    break;
                            }

                            break;
                        }

                    default:
                        _history.CompleteUnbounded(invocation, JournalOutput.Ambiguous);
                        version = (await ReadOnceAsync(client + 100, journal)).Version;
                        break;
                }
            }
        }

        private async Task<(JournalWriteOutcome Outcome, bool Deposed)> ExecuteWriteAsync(JournalRowKey key, JournalEntryKind kind, JournalVersion expected, byte[] payload)
        {
            var encoder = _processes[0].Journals;
            using var body = payload.Length == 0 ? default : ArcBuffer.Wrap(payload);
            using var record = kind switch
            {
                JournalEntryKind.Append => encoder.EncodeAppend(key, expected, body),
                JournalEntryKind.Replace => encoder.EncodeReplace(key, expected, body),
                JournalEntryKind.Delete => encoder.EncodeDelete(key, expected),
                JournalEntryKind.Expired => throw new InvalidOperationException(),
                _ => throw new InvalidOperationException(),
            };

            var target = OwnerOf(key);
            while (true)
            {
                if (target.Stopped)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    target = await RefreshOwnerAsync(key);
                    continue;
                }

                JournalWriteOutcome outcome;
                try
                {
                    outcome = await target.Journals.WriteAsync(record, cancellationToken);
                }
                catch (ObjectDisposedException) when (target.Stopped)
                {
                    return (JournalWriteOutcome.Ambiguous.Instance, IsDeposed(target, key));
                }
                catch (Exception failure) when (_faulting && IsUnavailable(failure))
                {
                    return (JournalWriteOutcome.Ambiguous.Instance, false);
                }

                switch (outcome.Value)
                {
                    case JournalWriteOutcome.Accepted:
                    case JournalWriteOutcome.Rejected:
                    case JournalWriteOutcome.ReplaceRequired:
                    case JournalWriteOutcome.Ambiguous:
                        return (outcome, IsDeposed(target, key));
                    case JournalMoved moved:
                        target = await NextOwnerAsync(key, moved.Owner);
                        continue;
                    case JournalWriteOutcome.Fenced:
                        await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                        target = await RefreshOwnerAsync(key);
                        continue;
                    default:
                        throw new InvalidOperationException($"Unexpected write outcome: {outcome}.");
                }
            }
        }

        private async Task ReadLoopAsync(int client)
        {
            var reads = fullWorkload ? 8 : 3;
            for (var index = 0; index < reads; index++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(8, 22)), SimulationEnvironment.Clock, cancellationToken);
                _ = await ReadOnceAsync(client, _random.Choose(JournalNames));
            }
        }

        private async Task<(JournalVersion Version, bool Exists, byte[] Content)> ReadOnceAsync(int client, string journal)
        {
            var key = JournalRowKey.Encode(journal);
            var invocation = _history.Invoke(client, JournalInput.Read(journal));
            var target = OwnerOf(key);
            while (true)
            {
                if (target.Stopped)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    target = await RefreshOwnerAsync(key);
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
                    target = await RefreshOwnerAsync(key);
                    continue;
                }
                catch (Exception failure) when (_faulting && IsUnavailable(failure))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    target = await RefreshOwnerAsync(key);
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
                    target = await RefreshOwnerAsync(key);
                    continue;
                }
                catch (Exception failure) when (_faulting && IsUnavailable(failure))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    target = await RefreshOwnerAsync(key);
                    continue;
                }

                var bytes = content.ToArray();
                _history.Complete(invocation, JournalOutput.Read(result.Exists, result.CurrentVersion.ToString(), bytes));
                return (result.CurrentVersion, result.Exists, bytes);
            }
        }

        private async Task DrainLoopAsync()
        {
            var drainer = _processes[0];
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), SimulationEnvironment.Clock, cancellationToken);
                LedgerMapValue map;
                try
                {
                    map = await drainer.View.ReadAsync(cancellationToken);
                }
                catch (Exception failure) when (_faulting && IsUnavailable(failure))
                {
                    continue;
                }

                LedgerMetadata[] droppable = [.. map.Ledgers.Where(ledger => !ledger.IsOpen && !map.Ledgers.Any(lower => lower.Id < ledger.Id && lower.Range.Overlaps(ledger.Range)))];
                if (droppable.Length == 0 && !_running)
                {
                    return;
                }

                foreach (var ledger in droppable)
                {
                    var end = ((LedgerStatus.Closed)ledger.Status.Value).T;
                    var segments = await ScanAsync(drainer, ledger, end);
                    try
                    {
                        _decided[ledger.Id] = [.. segments.SelectMany(RowsOf)];
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

                    await DropLedgerAsync(ledger.Id);
                    _ = _dropped.Add(ledger.Id);
                    if (ledger.Location.Value is LedgerLocation.Nodes)
                    {
                        _ = _drainedNodes.Add(ledger.Id);
                    }
                    else
                    {
                        _ = _drainedObjects.Add(ledger.Id);
                    }
                }
            }
        }

        private async Task DropLedgerAsync(LedgerId ledger)
        {
            while (true)
            {
                try
                {
                    var outcome = await committers[1].CommitAsync(new LedgerMapChange.Drop(ledger), cancellationToken);
                    Assert.IsType<LedgerMapCommitOutcome.Applied>(outcome.Value);
                    return;
                }
                catch (Exception failure) when (_faulting && failure is IOException or TimeoutException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                }
            }
        }

        private async Task CaptureRemainingOpenLedgersAsync(LedgerMapValue map)
        {
            var drainer = _processes[0];
            foreach (var ledger in map.Ledgers)
            {
                if (_decided.ContainsKey(ledger.Id))
                {
                    continue;
                }

                Slot end;
                if (ledger.Status.Value is LedgerStatus.Closed closed)
                {
                    end = closed.T;
                }
                else
                {
                    var tail = Assert.IsType<LedgerTail>((await drainer.Reader.ReadTailAsync(ledger, cancellationToken)).Value);
                    end = tail.ContiguousTail;
                }

                var segments = await ScanAsync(drainer, ledger, end);
                try
                {
                    _decided[ledger.Id] = [.. segments.SelectMany(RowsOf)];
                }
                finally
                {
                    foreach (var segment in segments)
                    {
                        KeyedRow.Release(segment);
                    }
                }
            }
        }

        private async Task VerifyFinalStateAsync()
        {
            foreach (var process in _processes)
            {
                if (!process.Stopped)
                {
                    Assert.Equal(0, process.Journals.WaitingEntries);
                }
            }

            foreach (var journal in JournalNames)
            {
                var rows = _decided.Values
                    .SelectMany(static list => list)
                    .Where(row => string.Equals(row.Journal, journal, StringComparison.Ordinal))
                    .OrderBy(static row => row.Tag)
                    .ToArray();

                // HI-I4: verify token chain across every decided row (Nodes + ObjectStore + Archive).
                var violation = TokenChainChecker.Check(archive, default, [.. rows.Select(static row => row.Chain)], dropped: _dropped);
                Assert.Null(violation);

                // Compute expected fold of all decided rows and compare to a final owner read.
                var expectedExists = false;
                List<byte> expectedContent = [];
                foreach (var row in rows)
                {
                    switch (row.Kind)
                    {
                        case RowKind.Append:
                            expectedExists = true;
                            expectedContent.AddRange(row.Body);
                            break;
                        case RowKind.Replace:
                            expectedExists = true;
                            expectedContent.Clear();
                            expectedContent.AddRange(row.Body);
                            break;
                        case RowKind.Delete:
                            expectedExists = false;
                            expectedContent.Clear();
                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                }

                var (_, actualExists, actualContent) = await ReadOnceAsync(client: 200, journal);
                Assert.Equal(expectedExists, actualExists);
                Assert.Equal(expectedContent.ToArray(), actualContent);
            }
        }

        private async Task<List<List<KeyedRow>>> ScanAsync(Process process, LedgerMetadata ledger, Slot end)
        {
            var segments = new List<List<KeyedRow>>();
            using var view = new LedgerSegmentView(ledger.Id);
            using var bodies = new ArcBufferWriter();
            LedgerSegmentId? current = null;
            var from = Slot.Zero;
            try
            {
                while (from < end)
                {
                    try
                    {
                        await foreach (var item in process.Reader.ScanAsync(ledger, from, end, view, cancellationToken: cancellationToken))
                        {
                            var read = item.Read;
                            if (!read.TryGetValue(out LedgerRead.Data data))
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
                    catch (Exception failure) when (_faulting && IsUnavailable(failure))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(5), SimulationEnvironment.Clock, cancellationToken);
                    }
                }
            }
            catch
            {
                foreach (var segment in segments)
                {
                    KeyedRow.Release(segment);
                }

                throw;
            }

            return segments;
        }

        private IEnumerable<ReferenceRow> RowsOf(List<KeyedRow> segment)
        {
            foreach (var row in segment)
            {
                var journal = _journalsByKey[Convert.ToHexString(row.Key)];
                yield return new ReferenceRow(journal, row.Row.Tag, row.Row.Kind, row.Row.Version, row.Row.Token, row.Row.Segment, row.Row.Body.ToArray());
            }
        }

        private Process OwnerOf(JournalRowKey key)
        {
            foreach (var process in _processes)
            {
                if (!process.Stopped && process.View.Current.FindOpen(key.Span) is { } open)
                {
                    return _processes.Single(candidate => candidate.Node == open.Primary.Node);
                }
            }

            return _processes[0];
        }

        private async Task<Process> RefreshOwnerAsync(JournalRowKey key)
        {
            var map = await RetryAsync(() => _processes[0].View.ReadAsync(cancellationToken), cancellationToken);
            var open = map.FindOpen(key.Span);
            return open is { } ledger ? _processes.Single(candidate => candidate.Node == ledger.Primary.Node) : _processes[0];
        }

        private async Task<Process> NextOwnerAsync(JournalRowKey key, NodeId? hint)
        {
            if (hint is { } node)
            {
                var target = _processes.FirstOrDefault(candidate => candidate.Node == node);
                if (target is not null && !target.Stopped)
                {
                    try
                    {
                        _ = await target.View.ReadAsync(cancellationToken);
                    }
                    catch (Exception failure) when (_faulting && IsUnavailable(failure) || target.Stopped && failure is ObjectDisposedException)
                    {
                    }

                    return target;
                }
            }

            return await RefreshOwnerAsync(key);
        }

        private bool IsDeposed(Process target, JournalRowKey key) =>
            _previous.FindOpen(key.Span) is not { } open || open.Primary.Node != target.Node;

        private static bool IsUnavailable(Exception failure) =>
            failure is IOException or TimeoutException or NodeDownException or JournalStoreUnavailableException or AggregateException;

        private void Heal()
        {
            _faulting = false;
            environment.Network.MessageDropRate = 0;
            environment.Rpc.DuplicationRate = 0;
            register.UnknownOutcomeRate = 0;
            register.AppliedMismatchRate = 0;
            register.ReadFailureRate = 0;
            objects.UnknownOutcomeRate = 0;
            objects.RetriedAnswerRate = 0;
            objects.ReadFailureRate = 0;
        }

        private async Task RefreshAllViewsAsync()
        {
            foreach (var process in _processes)
            {
                if (!process.Stopped)
                {
                    _ = await RetryAsync(() => process.View.ReadAsync(cancellationToken), cancellationToken);
                }
            }
        }
    }
}
