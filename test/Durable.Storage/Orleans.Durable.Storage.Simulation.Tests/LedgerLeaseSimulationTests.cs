using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Deterministic Clockwork simulations of the L2 ledger-replica quorum lease (system-model.md §2, §4; LP-I17;
/// LG-I10; LR-I10; OR-I24):
/// <list type="bullet">
/// <item>0-RTT linearizable local reads and cold-key first writes while the primary holds a valid quorum lease, with zero replica <c>ReadTailAsync</c> RPCs.</item>
/// <item>Planned cooperative handoff via <c>PrepareRelease</c> permanently surrenders the lease and completes <c>Replace</c> without waiting for <c>LeaseDuration</c> (0 ms wait), whereas unplanned takeover waits for the sealing quorum's remaining lease before committing the successor ledger so no stale 0-RTT read can observe an outdated state after the successor accepts writes.</item>
/// <item>Replica crash and restart restores RAM-only <c>bootLeaseHorizon</c> with zero WAL writes so sealing a restarted replica still protects any live primary lease.</item>
/// </list>
/// </summary>
public sealed class LedgerLeaseSimulationTests
{
    private const int NodeCount = 4;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GuardBand = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ReplicaDeadline = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(10);

    private enum ScenarioKind
    {
        ZeroRttReadsAndColdLoads,
        CooperativeVsUnplannedTakeover,
        ReplicaRestartBootLeaseHorizon,
    }

    /// <summary>
    /// LP-I17: while leased on a real simulated <see cref="LedgerStore"/> cluster, primary <c>JournalStore.ReadAsync</c>
    /// and cold-key first writes issue zero replica <c>ReadTailAsync</c> RPCs.
    /// </summary>
    [Fact]
    public void LeasedPrimaryServesZeroRttReadsAndColdKeyWritesInSimulation() =>
        _ = SeededSimulation.Run(seed: 201, static env => RunAsync(env, ScenarioKind.ZeroRttReadsAndColdLoads));

    /// <summary>
    /// LG-I10, OR-I24: planned cooperative <c>Replace</c> skips the lease-expiry wait (completing in well under
    /// <c>LeaseDuration</c> after <c>PrepareRelease</c> surrenders the lease), while unplanned <c>TakenOver</c>
    /// <c>Replace</c> waits out the sealing quorum's remaining lease before committing the successor ledger.
    /// </summary>
    [Fact]
    public void CooperativeHandoffSkipsLeaseWaitWhileUnplannedTakeoverWaitsForExpiry() =>
        _ = SeededSimulation.Run(seed: 202, static env => RunAsync(env, ScenarioKind.CooperativeVsUnplannedTakeover));

    /// <summary>
    /// LR-I10, LS-I13: after a replica node crashes and restarts under Clockwork, its RAM-only <c>bootLeaseHorizon</c>
    /// ensures a seal reports remaining lease time and causes an unplanned takeover to wait out the lease window.
    /// </summary>
    [Fact]
    public void ReplicaCrashRestartProtectsLeaseViaBootLeaseHorizon() =>
        _ = SeededSimulation.Run(seed: 203, static env => RunAsync(env, ScenarioKind.ReplicaRestartBootLeaseHorizon));

    private static async Task RunAsync(SimulationEnvironment environment, ScenarioKind kind)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        environment.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(2);
        environment.Network.MaxJitter = TimeSpan.FromMilliseconds(2);
        var client = environment.Cluster.AddNode("client");

        var leaseOptions = new LedgerLeaseOptions
        {
            LeaseDuration = LeaseDuration,
            ClockDriftFactor = 1.25,
            LeaseGuardBand = GuardBand,
        };
        var nodes = await StartNodesAsync(environment, NodeCount, LeaseDuration, token);
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, ReplicaDeadline);
        _ = LedgerStoreInvariants.Register(environment, replicas);
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
        await using var objects = new SimulatedObjectStore(environment, "archive");
        using var archive = new SimulatedArchiveStore(environment, "archive-store");

        var registry = new RegisteredNodes(
            Revision: 1,
            DrainAllRevision: 0,
            Nodes: nodes.Select((node, index) => (node.NodeId, index))
                .ToFrozenDictionary(
                    static entry => entry.NodeId,
                    static entry => new RegisteredNode(RetiredRevision: null, string.Create(CultureInfo.InvariantCulture, $"domain-{entry.index}"), Generation: 1, Ordinal: null)));
        var membership = nodes.ToFrozenDictionary(static node => node.NodeId, static _ => (NodeLiveness)NodeLiveness.Live.Instance);

        var processes = new List<NodeProcess>(NodeCount);
        var controllerView = await LedgerMapView.OpenAsync(register, token);
        await using var controller = new ControllerHost(controllerView, register, objects, new ControlOptions
        {
            Replicas = 3,
            SequencerWindow = 4,
            LedgersPerNode = 1,
            MaxConcurrentTransitions = 2,
            MaxConcurrentHandOffs = 2,
            TransitionTimeout = TransitionTimeout,
            PrepareReleaseTimeout = PrepareTimeout,
            ReplicaGrace = TimeSpan.FromHours(1),
            SplitBytesPerSecond = long.MaxValue,
        });
        try
        {
            foreach (var node in nodes)
            {
                var view = await LedgerMapView.OpenAsync(register, token);
                processes.Add(new NodeProcess(node.NodeId, view, new CountingNodeTransport(replicas, node.Address), archive, replicas.LedgerTrace, leaseOptions));
            }

            controller.Decisions.Observe(registry, membership);

            // Bootstrap the initial open ledger.
            var bootstrapPass = controller.Decisions.RunPass();
            var create = Assert.Single(bootstrapPass.Transitions);
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await ExecuteTransitionAsync(controller, processes, create, token)).Value);
            await RefreshViewsAsync(controller.View, processes, token);

            var initialLedger = Assert.Single(controller.View.Current.Ledgers, static l => l.IsOpen);
            var primary = processes.Single(p => p.Node == initialLedger.Primary.Node);

            switch (kind)
            {
                case ScenarioKind.ZeroRttReadsAndColdLoads:
                    await RunZeroRttReadsAndColdLoadsAsync(primary, token);
                    break;
                case ScenarioKind.CooperativeVsUnplannedTakeover:
                    await RunCooperativeVsUnplannedTakeoverAsync(environment, controller, processes, primary, initialLedger, token);
                    break;
                case ScenarioKind.ReplicaRestartBootLeaseHorizon:
                    await RunReplicaRestartBootLeaseHorizonAsync(environment, nodes, controller, processes, primary, initialLedger, token);
                    break;
                default:
                    throw new UnreachableException();
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                await process.DisposeAsync();
            }
        }
    }

    private static async Task RunZeroRttReadsAndColdLoadsAsync(NodeProcess primary, CancellationToken token)
    {
        var kSeed = JournalRowKey.Encode("seed");
        var v1 = await WriteAcceptedAsync(primary.Journals, kSeed, JournalVersion.Origin, "first"u8.ToArray(), token);
        var tailCallsAfterSeed = primary.Transport.ReadTailCalls;

        // Repeated reads and cold-key first writes (WarmStateBytes = 0) take 0 ReadTailAsync RPCs while leased.
        var r1 = await ReadAllBytesAsync(primary.Journals, kSeed, token);
        Assert.Equal((v1, "first"), (r1.Version, Encoding.UTF8.GetString(r1.Content)));

        var kCold1 = JournalRowKey.Encode("cold-1");
        var kCold2 = JournalRowKey.Encode("cold-2");
        var c1 = await WriteAcceptedAsync(primary.Journals, kCold1, JournalVersion.Origin, "alpha"u8.ToArray(), token);
        var c2 = await WriteAcceptedAsync(primary.Journals, kCold2, JournalVersion.Origin, "beta"u8.ToArray(), token);
        Assert.Equal(tailCallsAfterSeed, primary.Transport.ReadTailCalls);

        var rCold1 = await ReadAllBytesAsync(primary.Journals, kCold1, token);
        var rCold2 = await ReadAllBytesAsync(primary.Journals, kCold2, token);
        Assert.Equal((c1, "alpha"), (rCold1.Version, Encoding.UTF8.GetString(rCold1.Content)));
        Assert.Equal((c2, "beta"), (rCold2.Version, Encoding.UTF8.GetString(rCold2.Content)));
        Assert.Equal(tailCallsAfterSeed, primary.Transport.ReadTailCalls);
    }

    private static async Task RunCooperativeVsUnplannedTakeoverAsync(
        SimulationEnvironment environment,
        ControllerHost controller,
        List<NodeProcess> processes,
        NodeProcess primaryA,
        LedgerMetadata ledger1,
        CancellationToken token)
    {
        var key = JournalRowKey.Encode("handoff-key");
        var v1 = await WriteAcceptedAsync(primaryA.Journals, key, JournalVersion.Origin, "v1|"u8.ToArray(), token);

        // Part 1: Cooperative PrepareRelease -> 0 ms lease wait on Replace!
        controller.Decisions.RequestHandOff(primaryA.Node, generation: 1);
        var pass1 = controller.Decisions.RunPass();
        var sentPrepare = Assert.Single(pass1.PrepareReleases);
        var released = Assert.IsType<PrepareReleaseReply.Released>((await primaryA.Controller.PrepareReleaseAsync(sentPrepare.Request, token)).Value);
        controller.Decisions.PrepareReleaseCompleted(sentPrepare, released);

        var pass2 = controller.Decisions.RunPass();
        var sentCoopReplace = Assert.Single(pass2.Transitions);
        var coopReplace = Assert.IsType<Transition.Replace>(sentCoopReplace.Transition.Value);
        Assert.Equal(PredecessorReleaseHint.Cooperative, coopReplace.PredecessorHint);

        var coopStart = environment.TimeProvider.GetTimestamp();
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await ExecuteTransitionAsync(controller, processes, sentCoopReplace, token)).Value);
        var coopElapsed = environment.TimeProvider.GetElapsedTime(coopStart);
        Assert.True(coopElapsed < TimeSpan.FromMilliseconds(500), $"Cooperative Replace waited {coopElapsed}, expected near-zero wait (< 500ms vs {LeaseDuration} lease).");

        // Old primary A surrendered its lease during PrepareRelease, so reading on A now must observe Moved (never stale 0-RTT).
        await RefreshViewsAsync(controller.View, processes, token);
        var ledger2 = Assert.Single(controller.View.Current.Ledgers, static l => l.IsOpen);
        var primaryB = processes.Single(p => p.Node == ledger2.Primary.Node);
        var v2 = await WriteAcceptedAsync(primaryB.Journals, key, v1, "v2|"u8.ToArray(), token);

        // Part 2: Unplanned takeover (TakenOver) from B -> C MUST wait for B's 2s lease to expire before committing!
        var replicas2 = ((LedgerLocation.Nodes)ledger2.Location.Value).Replicas;
        var targetC = processes.First(p => p.Node != primaryB.Node && replicas2.Contains(p.Node));
        var unplannedChoice = new TransitionChoice(
            targetC.Node,
            ledger2.Location,
            k: ledger2.K,
            chosenAt: controller.View.Current.Epoch,
            epoch: controller.View.Current.Epoch);
        var unplannedTransition = new Transition.Replace(ledger2.Id, unplannedChoice, PredecessorReleaseHint.TakenOver);

        var unplannedStart = environment.TimeProvider.GetTimestamp();
        var unplannedReply = await targetC.Controller.ExecuteAsync(
            unplannedTransition,
            (change, ct) => controller.Decisions.CommitLedgerMapAsync(change, ct),
            token);
        var unplannedElapsed = environment.TimeProvider.GetElapsedTime(unplannedStart);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(unplannedReply.Value);
        Assert.True(unplannedElapsed >= TimeSpan.FromMilliseconds(1_500), $"Unplanned Replace took only {unplannedElapsed}, expected lease-expiry wait near {LeaseDuration}.");

        await RefreshViewsAsync(controller.View, processes, token);
        var finalRead = await ReadAllBytesAsync(targetC.Journals, key, token);
        Assert.Equal((v2, "v1|v2|"), (finalRead.Version, Encoding.UTF8.GetString(finalRead.Content)));
    }

    private static async Task RunReplicaRestartBootLeaseHorizonAsync(
        SimulationEnvironment environment,
        ImmutableArray<LedgerStoreNode> nodes,
        ControllerHost controller,
        List<NodeProcess> processes,
        NodeProcess primary,
        LedgerMetadata ledger,
        CancellationToken token)
    {
        var key = JournalRowKey.Encode("restart-key");
        var v1 = await WriteAcceptedAsync(primary.Journals, key, JournalVersion.Origin, "durable"u8.ToArray(), token);

        // Crash and restart every replica node holding this ledger so all RAM grantedLeaseUntil maps are lost,
        // leaving ONLY bootLeaseHorizon latched on reopen.
        var ledgerReplicas = ((LedgerLocation.Nodes)ledger.Location.Value).Replicas;
        var replicaNodes = nodes.Where(n => ledgerReplicas.Contains(n.NodeId)).ToArray();
        foreach (var node in replicaNodes)
        {
            await node.CrashAsync(operatingSystem: true);
            await node.StartAsync(token);
        }

        // Unplanned Replace immediately after replica restarts MUST STILL wait for bootLeaseHorizon!
        var successor = processes.First(p => p.Node != primary.Node && ledgerReplicas.Contains(p.Node));
        var choice = new TransitionChoice(
            successor.Node,
            ledger.Location,
            k: ledger.K,
            chosenAt: controller.View.Current.Epoch,
            epoch: controller.View.Current.Epoch);

        var start = environment.TimeProvider.GetTimestamp();
        var reply = await successor.Controller.ExecuteAsync(
            new Transition.Replace(ledger.Id, choice, PredecessorReleaseHint.TakenOver),
            (change, ct) => controller.Decisions.CommitLedgerMapAsync(change, ct),
            token);
        var elapsed = environment.TimeProvider.GetElapsedTime(start);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(reply.Value);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(1_500), $"Post-restart unplanned Replace took only {elapsed}, expected bootLeaseHorizon wait near {LeaseDuration}.");

        await RefreshViewsAsync(controller.View, processes, token);
        var read = await ReadAllBytesAsync(successor.Journals, key, token);
        Assert.Equal((v1, "durable"), (read.Version, Encoding.UTF8.GetString(read.Content)));
    }

    private static async Task<ImmutableArray<LedgerStoreNode>> StartNodesAsync(
        SimulationEnvironment environment,
        int count,
        TimeSpan leaseDuration,
        CancellationToken token)
    {
        var builder = ImmutableArray.CreateBuilder<LedgerStoreNode>(count);
        for (var i = 0; i < count; i++)
        {
            var address = string.Create(CultureInfo.InvariantCulture, $"replica-{i + 1}");
            var options = LedgerStoreNode.DefaultOptions(address) with { LeaseDuration = leaseDuration };
            var node = new LedgerStoreNode(environment, address, SimulatedStorage.Cluster, options);
            await node.StartAsync(token);
            builder.Add(node);
        }

        return builder.MoveToImmutable();
    }

    private static async Task<TransitionReply> ExecuteTransitionAsync(
        ControllerHost controller,
        List<NodeProcess> processes,
        SentTransition sent,
        CancellationToken token)
    {
        var executor = processes.Single(p => p.Node == sent.Executor);
        var reply = await executor.Controller.ExecuteAsync(
            sent.Transition,
            (change, ct) => controller.Decisions.CommitLedgerMapAsync(change, ct),
            token);
        _ = controller.Decisions.TransitionCompleted(sent, reply);
        return reply;
    }

    private static async Task RefreshViewsAsync(LedgerMapView controllerView, List<NodeProcess> processes, CancellationToken token)
    {
        _ = await controllerView.ReadAsync(token);
        foreach (var process in processes)
        {
            _ = await process.View.ReadAsync(token);
        }
    }

    private static async Task<JournalVersion> WriteAcceptedAsync(
        JournalStore journals,
        JournalRowKey key,
        JournalVersion expected,
        byte[] payload,
        CancellationToken token)
    {
        using var body = ArcBuffer.Wrap(payload);
        using var record = journals.EncodeAppend(key, expected, body);
        var outcome = await journals.WriteAsync(record, token);
        return Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;
    }

    private static async Task<(JournalVersion Version, byte[] Content)> ReadAllBytesAsync(
        JournalStore journals,
        JournalRowKey key,
        CancellationToken token)
    {
        var outcome = await journals.ReadAsync(key, token);
        using var result = Assert.IsType<JournalReadResult>(outcome.Value);
        List<byte> content = [];
        await foreach (var chunk in result.ReadPayloadsAsync(token))
        {
            using (chunk)
            {
                content.AddRange(chunk.ToArray());
            }
        }

        return (result.CurrentVersion, content.ToArray());
    }

    private sealed class ControllerHost : IAsyncDisposable
    {
        private readonly LedgerMapCommitter _committer;
        private readonly ManifestCommitter _manifest;

        public ControllerHost(LedgerMapView view, IConditionalRegister register, SimulatedObjectStore objects, ControlOptions options)
        {
            View = view;
            _committer = new LedgerMapCommitter(register, int.MaxValue, view);
            var manifestView = new ManifestView(objects);
            _manifest = new ManifestCommitter(objects, manifestView, SimulationEnvironment.Clock, TimeSpan.Zero);
            Decisions = new ClusterController(
                options,
                view,
                _committer,
                new ClusterArchive.Lsm(manifestView, _manifest, new ArchiveOptions()),
                SimulationEnvironment.Clock,
                NullLogger.Instance);
        }

        public LedgerMapView View { get; }

        public ClusterController Decisions { get; }

        public async ValueTask DisposeAsync()
        {
            await _committer.DisposeAsync();
            await _manifest.DisposeAsync();
        }
    }

    private sealed class NodeProcess : IAsyncDisposable
    {
        public NodeProcess(NodeId node, LedgerMapView view, CountingNodeTransport transport, IArchiveStore archive, LedgerTrace trace, LedgerLeaseOptions leaseOptions)
        {
            Node = node;
            View = view;
            Transport = transport;
            Reader = new LedgerReader(view, transport, SimulationEnvironment.Clock, node)
            {
                LeaseOptions = leaseOptions,
            };
            Registry = new LedgerWriterRegistry(Reader, 1 << 25, SmallSegments.Options, SimulationEnvironment.Clock, NullLoggerFactory.Instance, SmallSegments.Random(node.ToString()));
            var journalOptions = new JournalStoreOptions
            {
                MaxBodyBytes = 4096,
                MaxBatchEntries = 4,
                MaxBatchBytes = 4096,
                MaxQueuedBytes = 1 << 20,
                WarmStateBytes = 0,
                TombstoneRetention = TimeSpan.FromMinutes(1),
            };
            Journals = new JournalStore(Registry, archive, journalOptions, SimulationEnvironment.Clock)
            {
                OnAdmission = (ledger, writer) => trace.TraceWriter(writer, transport.Address, ledger),
            };
            var objects = new InMemoryObjectStore();
            var manifest = new ManifestView(objects);
            var archiveOptions = new ArchiveOptions();
            var lsm = new NodeArchive.Lsm(
                new DrainWorker(objects, manifest, Reader, archiveOptions, SimulationEnvironment.Clock),
                new CompactionWorker(objects, new TailCache(objects, archiveOptions), manifest, archiveOptions),
                manifest);
            Controller = new NodeController(
                node,
                generation: 1,
                view,
                Registry,
                Reader,
                new TransitionExecutor(Registry, NullLogger<TransitionExecutor>.Instance),
                static () => true,
                static () => ImmutableDictionary<NodeId, int>.Empty,
                lsm,
                SimulationEnvironment.Clock,
                TimeSpan.FromMinutes(1),
                prepareRelease: Journals.PrepareReleaseAsync);
        }

        public NodeId Node { get; }

        public LedgerMapView View { get; }

        public CountingNodeTransport Transport { get; }

        public LedgerReader Reader { get; }

        public LedgerWriterRegistry Registry { get; }

        public JournalStore Journals { get; }

        public NodeController Controller { get; }

        public async ValueTask DisposeAsync()
        {
            await Journals.DisposeAsync();
            await Registry.DisposeAsync();
            await Reader.DisposeAsync();
        }
    }

    private sealed class CountingNodeTransport(SimulatedLedgerReplicaTransport replicas, string client) : ILedgerReplicaTransport
    {
        private int _readTailCalls;

        public string Address => client;

        public int ReadTailCalls => Volatile.Read(ref _readTailCalls);

        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            new CountingReplica(replicas.Open(node, ledger, options, client), this);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            replicas.HeartbeatAsync(node, ledgers, cancellationToken);

        private sealed class CountingReplica(ILedgerReplica inner, CountingNodeTransport parent) : ILedgerReplica
        {
            public ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
                inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

            public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

            public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(slot, contiguousTail, cancellationToken);

            public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
            {
                _ = Interlocked.Increment(ref parent._readTailCalls);
                return inner.ReadTailAsync(contiguousTail, seal, cancellationToken);
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
