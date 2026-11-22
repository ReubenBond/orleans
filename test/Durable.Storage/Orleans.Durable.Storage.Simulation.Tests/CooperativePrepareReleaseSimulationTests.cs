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
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Deterministic Clockwork simulation of cooperative <c>PrepareRelease</c> before planned handoffs
/// (orleans.md §5.6, §5.7, OR-I24):
/// <list type="bullet">
/// <item>Clean cooperative drain flushes in-flight journal writes on outgoing primary <c>A</c>, stops admitting new writes, replies <c>Released(contiguousTail)</c>, and hands off with <see cref="PredecessorReleaseHint.Cooperative"/> so successor <c>B</c> serves every flushed write without avoidable <c>Moved</c> storms.</item>
/// <item>Timeout and RPC failure on <c>PrepareRelease</c> fall back to non-cooperative <see cref="PredecessorReleaseHint.TakenOver"/> without claiming cooperative handoff.</item>
/// <item>A concurrent seal on the ledger's replicas during <c>PrepareRelease</c> aborts draining immediately with <c>Sealed</c> (well before <c>PrepareReleaseTimeout</c>), records no released ledger, and falls back to <see cref="PredecessorReleaseHint.TakenOver"/>.</item>
/// <item>Across controller failover mid-handoff, outgoing primary <c>A</c> answers repeated <c>PrepareRelease</c> calls idempotently with the same <c>Released(contiguousTail)</c> and advertises the drained ledger in <see cref="NodeReport.ReleasedLedgers"/> until the ledger closes.</item>
/// </list>
/// </summary>
public sealed class CooperativePrepareReleaseSimulationTests
{
    private const int NodeCount = 4;
    private static readonly TimeSpan ReplicaDeadline = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromMilliseconds(300);

    private enum ScenarioKind
    {
        CleanCooperativeDrain,
        RpcFailureFallback,
        TimeoutFallback,
        ConcurrentSealImmediateAbort,
        ControllerFailoverIdempotency,
    }

    /// <summary>
    /// OR-I24: planned handoff sends <c>PrepareRelease</c> to outgoing primary <c>A</c>; in-flight journal writes
    /// flush cleanly before <c>A</c> stops admitting and answers <c>Released(contiguousTail)</c>; the controller then
    /// sends <c>Replace</c> with <see cref="PredecessorReleaseHint.Cooperative"/> to <c>B</c>, which serves the
    /// flushed journal contents and subsequent appends linearly.
    /// </summary>
    [Fact]
    public void CleanCooperativeDrainFlushesInFlightWritesAndHandsOffCooperatively() =>
        _ = SeededSimulation.Run(seed: 101, static env => RunAsync(env, ScenarioKind.CleanCooperativeDrain));

    /// <summary>
    /// OR-I24: when <c>PrepareRelease</c> fails (unreachable node / lost reply), the controller falls back cleanly to
    /// <c>Replace</c> with <see cref="PredecessorReleaseHint.TakenOver"/> without claiming cooperative handoff.
    /// </summary>
    [Fact]
    public void PrepareReleaseRpcFailureFallsBackToNonCooperativeTakenOver() =>
        _ = SeededSimulation.Run(seed: 102, static env => RunAsync(env, ScenarioKind.RpcFailureFallback));

    /// <summary>
    /// OR-I24: when <c>PrepareRelease</c> does not answer before <c>PrepareReleaseTimeout</c>, the controller expires
    /// the prepare-release and falls back cleanly to <c>Replace</c> with <see cref="PredecessorReleaseHint.TakenOver"/>.
    /// </summary>
    [Fact]
    public void PrepareReleaseTimeoutFallsBackToNonCooperativeTakenOver() =>
        _ = SeededSimulation.Run(seed: 103, static env => RunAsync(env, ScenarioKind.TimeoutFallback));

    /// <summary>
    /// OR-I24: when another node seals the ledger while outgoing primary <c>A</c> is draining in-flight writes under
    /// <c>PrepareRelease</c>, <c>A</c> aborts draining immediately with <c>Sealed</c> (without waiting for the deadline),
    /// does not record the ledger as released, and the controller falls back to <see cref="PredecessorReleaseHint.TakenOver"/>.
    /// </summary>
    [Fact]
    public void ConcurrentSealDuringPrepareReleaseAbortsImmediatelyAndFallsBack() =>
        _ = SeededSimulation.Run(seed: 104, static env => RunAsync(env, ScenarioKind.ConcurrentSealImmediateAbort));

    /// <summary>
    /// OR-I24: when a controller fails over after outgoing primary <c>A</c> drained a ledger, <c>A</c> answers repeated
    /// <c>PrepareRelease</c> calls idempotently with the same <c>Released(contiguousTail)</c> and advertises the ledger
    /// in <see cref="NodeReport.ReleasedLedgers"/> so a new controller activation completes cooperative handoff and
    /// <c>A</c> prunes the released entry once the ledger closes.
    /// </summary>
    [Fact]
    public void ControllerFailoverMidHandoffObservesReleasedReportAndAnswersPrepareReleaseIdempotently() =>
        _ = SeededSimulation.Run(seed: 105, static env => RunAsync(env, ScenarioKind.ControllerFailoverIdempotency));

    private static async Task RunAsync(SimulationEnvironment environment, ScenarioKind kind)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        environment.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(2);
        environment.Network.MaxJitter = TimeSpan.FromMilliseconds(2);
        var client = environment.Cluster.AddNode("client");
        var nodes = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, NodeCount, token);
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
        var controllers = new List<ControllerHost>();
        try
        {
            foreach (var node in nodes)
            {
                var view = await LedgerMapView.OpenAsync(register, token);
                processes.Add(new NodeProcess(node.NodeId, view, new NodeTransport(replicas, node.Address), archive, replicas.LedgerTrace));
            }

            var options = new ControlOptions
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
            };

            var controller1 = await CreateControllerAsync(register, objects, options, registry, membership, token);
            controllers.Add(controller1);

            // Bootstrap the single initial open ledger [empty, empty) onto primary A.
            var bootstrapPass = controller1.Decisions.RunPass();
            var create = Assert.Single(bootstrapPass.Transitions);
            Assert.Equal(TransitionPurpose.Bootstrap, create.Purpose);
            var createReply = await ExecuteTransitionAsync(controller1, processes, create, token);
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(createReply.Value);

            await RefreshViewsAsync(controller1.View, processes, token);
            var initialLedger = Assert.Single(controller1.View.Current.Ledgers, static l => l.IsOpen);
            var primaryProcess = processes.Single(p => p.Node == initialLedger.Primary.Node);
            var journalKey = JournalRowKey.Encode("cooperative-journal");

            switch (kind)
            {
                case ScenarioKind.CleanCooperativeDrain:
                    await RunCleanCooperativeDrainAsync(environment, controller1, processes, primaryProcess, initialLedger, journalKey, token);
                    break;
                case ScenarioKind.RpcFailureFallback:
                    await RunRpcFailureFallbackAsync(controller1, processes, primaryProcess, initialLedger, journalKey, token);
                    break;
                case ScenarioKind.TimeoutFallback:
                    await RunTimeoutFallbackAsync(environment, controller1, processes, primaryProcess, initialLedger, journalKey, token);
                    break;
                case ScenarioKind.ConcurrentSealImmediateAbort:
                    await RunConcurrentSealImmediateAbortAsync(environment, replicas, nodes, controller1, processes, primaryProcess, initialLedger, journalKey, token);
                    break;
                case ScenarioKind.ControllerFailoverIdempotency:
                    await RunControllerFailoverIdempotencyAsync(register, objects, options, registry, membership, controller1, controllers, processes, primaryProcess, initialLedger, journalKey, token);
                    break;
                default:
                    throw new UnreachableException();
            }
        }
        finally
        {
            foreach (var controller in controllers)
            {
                await controller.DisposeAsync();
            }

            foreach (var process in processes)
            {
                await process.DisposeAsync();
            }
        }
    }

    private static async Task RunCleanCooperativeDrainAsync(
        SimulationEnvironment environment,
        ControllerHost controller,
        List<NodeProcess> processes,
        NodeProcess primary,
        LedgerMetadata ledger,
        JournalRowKey key,
        CancellationToken token)
    {
        // Seed one durable write so the journal exists and the writer is live on primary A.
        var v1 = await WriteAcceptedAsync(primary.Journals, key, JournalVersion.Origin, "first|"u8.ToArray(), token);

        // Increase network latency briefly so the second write is genuinely in flight when PrepareRelease begins,
        // while staying well within ReplicaDeadline (40ms) so every RPC succeeds cleanly.
        environment.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(12);
        using var inFlightBody = ArcBuffer.Wrap("second-in-flight|"u8.ToArray());
        using var inFlightRecord = primary.Journals.EncodeAppend(key, v1, inFlightBody);
        var inFlightWrite = primary.Journals.WriteAsync(inFlightRecord, token);

        // Request planned handoff off primary A: controller must emit PrepareRelease first, with no Replace yet.
        controller.Decisions.RequestHandOff(primary.Node, generation: 1);
        var pass1 = controller.Decisions.RunPass();
        Assert.Empty(pass1.Transitions);
        var sentPrepare = Assert.Single(pass1.PrepareReleases);
        Assert.Equal(primary.Node, sentPrepare.Node);
        Assert.Equal(ledger.Id, sentPrepare.Request.Ledger);
        Assert.Equal(ledger.Range, sentPrepare.Request.Range);
        Assert.Equal(TransitionPurpose.Departure, sentPrepare.Purpose);

        // Run PrepareRelease concurrently with the in-flight write; both complete cleanly without Moved/Fenced errors.
        var prepareTask = primary.Controller.PrepareReleaseAsync(sentPrepare.Request, token);
        await SimulationTasks.WhenAll([inFlightWrite, prepareTask]);

        var v2 = Assert.IsType<JournalWriteOutcome.Accepted>((await inFlightWrite).Value).Version;
        var released = Assert.IsType<PrepareReleaseReply.Released>((await prepareTask).Value);
        Assert.True(released.ContiguousTail >= new Slot(2), $"Expected both writes flushed, got tail {released.ContiguousTail}.");

        // Primary A stops admitting new writes immediately after release and advertises the released ledger in NodeReport.
        using var afterBody = ArcBuffer.Wrap("after-release|"u8.ToArray());
        using var afterRecord = primary.Journals.EncodeAppend(key, v2, afterBody);
        var afterOutcome = await primary.Journals.WriteAsync(afterRecord, token);
        _ = Assert.IsType<JournalMoved>(afterOutcome.Value);

        var report = primary.CreateReport();
        Assert.NotNull(report.ReleasedLedgers);
        Assert.Equal(released.ContiguousTail, report.ReleasedLedgers[ledger.Id]);

        // Restore low latency, report Released to the controller, and verify Replace carries Cooperative hint.
        environment.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(2);
        controller.Decisions.PrepareReleaseCompleted(sentPrepare, released);
        var pass2 = controller.Decisions.RunPass();
        Assert.Empty(pass2.PrepareReleases);
        var sentReplace = Assert.Single(pass2.Transitions);
        Assert.Equal(TransitionPurpose.Departure, sentReplace.Purpose);
        var replace = Assert.IsType<Transition.Replace>(sentReplace.Transition.Value);
        Assert.Equal(ledger.Id, replace.Ledger);
        Assert.Equal(PredecessorReleaseHint.Cooperative, replace.PredecessorHint);
        Assert.NotEqual(primary.Node, sentReplace.Executor);

        var replaceReply = await ExecuteTransitionAsync(controller, processes, sentReplace, token);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(replaceReply.Value);
        await RefreshViewsAsync(controller.View, processes, token);

        // Successor B serves both pre-handoff writes cleanly and accepts the next write.
        var successor = processes.Single(p => p.Node == sentReplace.Executor);
        var readBefore = await ReadAllBytesAsync(successor.Journals, key, token);
        Assert.Equal("first|second-in-flight|", Encoding.UTF8.GetString(readBefore.Content));
        Assert.Equal(v2, readBefore.Version);

        _ = await WriteAcceptedAsync(successor.Journals, key, v2, "third-on-successor|"u8.ToArray(), token);
        var readAfter = await ReadAllBytesAsync(successor.Journals, key, token);
        Assert.Equal("first|second-in-flight|third-on-successor|", Encoding.UTF8.GetString(readAfter.Content));
    }

    private static async Task RunRpcFailureFallbackAsync(
        ControllerHost controller,
        List<NodeProcess> processes,
        NodeProcess primary,
        LedgerMetadata ledger,
        JournalRowKey key,
        CancellationToken token)
    {
        var v1 = await WriteAcceptedAsync(primary.Journals, key, JournalVersion.Origin, "before-rpc-fail|"u8.ToArray(), token);

        controller.Decisions.RequestHandOff(primary.Node, generation: 1);
        var pass1 = controller.Decisions.RunPass();
        var sentPrepare = Assert.Single(pass1.PrepareReleases);
        Assert.Empty(pass1.Transitions);

        // Simulate RPC failure / lost reply by reporting null.
        controller.Decisions.PrepareReleaseCompleted(sentPrepare, reply: null);
        var pass2 = controller.Decisions.RunPass();
        Assert.Empty(pass2.PrepareReleases);
        var sentReplace = Assert.Single(pass2.Transitions);
        var replace = Assert.IsType<Transition.Replace>(sentReplace.Transition.Value);
        Assert.Equal(ledger.Id, replace.Ledger);
        Assert.Equal(PredecessorReleaseHint.TakenOver, replace.PredecessorHint);

        var replaceReply = await ExecuteTransitionAsync(controller, processes, sentReplace, token);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(replaceReply.Value);
        await RefreshViewsAsync(controller.View, processes, token);

        var successor = processes.Single(p => p.Node == sentReplace.Executor);
        var read = await ReadAllBytesAsync(successor.Journals, key, token);
        Assert.Equal("before-rpc-fail|", Encoding.UTF8.GetString(read.Content));
        Assert.Equal(v1, read.Version);
    }

    private static async Task RunTimeoutFallbackAsync(
        SimulationEnvironment environment,
        ControllerHost controller,
        List<NodeProcess> processes,
        NodeProcess primary,
        LedgerMetadata ledger,
        JournalRowKey key,
        CancellationToken token)
    {
        var v1 = await WriteAcceptedAsync(primary.Journals, key, JournalVersion.Origin, "before-timeout|"u8.ToArray(), token);

        controller.Decisions.RequestHandOff(primary.Node, generation: 1);
        var pass1 = controller.Decisions.RunPass();
        var sentPrepare = Assert.Single(pass1.PrepareReleases);
        Assert.Empty(pass1.Transitions);

        // Do not deliver PrepareRelease: advance virtual clock past PrepareReleaseTimeout.
        await Task.Delay(PrepareTimeout + TimeSpan.FromMilliseconds(20), SimulationEnvironment.Clock, token);

        var pass2 = controller.Decisions.RunPass();
        Assert.Empty(pass2.PrepareReleases);
        var sentReplace = Assert.Single(pass2.Transitions);
        var replace = Assert.IsType<Transition.Replace>(sentReplace.Transition.Value);
        Assert.Equal(ledger.Id, replace.Ledger);
        Assert.Equal(PredecessorReleaseHint.TakenOver, replace.PredecessorHint);

        var replaceReply = await ExecuteTransitionAsync(controller, processes, sentReplace, token);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(replaceReply.Value);
        await RefreshViewsAsync(controller.View, processes, token);

        var successor = processes.Single(p => p.Node == sentReplace.Executor);
        var read = await ReadAllBytesAsync(successor.Journals, key, token);
        Assert.Equal("before-timeout|", Encoding.UTF8.GetString(read.Content));
        Assert.Equal(v1, read.Version);
        _ = environment;
    }

    private static async Task RunConcurrentSealImmediateAbortAsync(
        SimulationEnvironment environment,
        SimulatedLedgerReplicaTransport replicas,
        LedgerStoreNode[] nodes,
        ControllerHost controller,
        List<NodeProcess> processes,
        NodeProcess primary,
        LedgerMetadata ledger,
        JournalRowKey key,
        CancellationToken token)
    {
        var v1 = await WriteAcceptedAsync(primary.Journals, key, JournalVersion.Origin, "before-seal|"u8.ToArray(), token);

        // Isolate both remote replicas of the ledger so primary A can reach only 1 of 3 replicas (< K=2 quorum),
        // keeping the in-flight write blocked in the writer while PrepareRelease runs.
        var remoteReplicas = ledger.Replicas
            .Where(n => n != primary.Node)
            .Select(n => nodes.Single(node => node.NodeId == n))
            .ToArray();
        foreach (var remote in remoteReplicas)
        {
            remote.Isolate();
        }

        using var blockedBody = ArcBuffer.Wrap("blocked-by-partition|"u8.ToArray());
        using var blockedRecord = primary.Journals.EncodeAppend(key, v1, blockedBody);
        var blockedWrite = primary.Journals.WriteAsync(blockedRecord, token);

        // Give the write a moment to enter the writer's proposal queue while remote replicas are isolated.
        await Task.Delay(TimeSpan.FromMilliseconds(10), SimulationEnvironment.Clock, token);

        // Request planned handoff and give PrepareRelease a generous 10s deadline so immediate abort is unmistakable.
        var longDeadline = TimeSpan.FromSeconds(10);
        controller.Decisions.RequestHandOff(primary.Node, generation: 1);
        var pass1 = controller.Decisions.RunPass();
        var sentPrepare = Assert.Single(pass1.PrepareReleases);
        var longRequest = new PrepareReleaseRequest(sentPrepare.Request.Ledger, sentPrepare.Request.Range, longDeadline);

        var startedAt = environment.TimeProvider.GetUtcNow();
        var prepareTask = primary.Controller.PrepareReleaseAsync(longRequest, token);
        await Task.Delay(TimeSpan.FromMilliseconds(15), SimulationEnvironment.Clock, token);
        Assert.False(prepareTask.IsCompleted, "PrepareRelease should still be waiting while the in-flight proposal is blocked.");

        // Concurrently heal the remote replicas and have another node seal the ledger (as a concurrent takeover does).
        foreach (var remote in remoteReplicas)
        {
            remote.Reconnect();
        }

        var sealer = processes.First(p => p.Node != primary.Node);
        var replicaOptions = new LedgerReplicaOptions { WindowSize = 4 };
        foreach (var replicaNode in ledger.Replicas)
        {
            await using var replica = replicas.Open(replicaNode, ledger.Id, replicaOptions, sealer.TransportAddress);
            _ = await replica.ReadTailAsync(Slot.Zero, seal: true, token);
        }

        // Primary A must observe the seal and abort PrepareRelease immediately, far below the 10s deadline.
        var reply = await prepareTask;
        var elapsed = environment.TimeProvider.GetUtcNow() - startedAt;
        _ = Assert.IsType<PrepareReleaseReply.Sealed>(reply.Value);
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Expected immediate abort on concurrent seal, took {elapsed}.");

        // Primary A must NOT record a sealed ledger as cleanly released.
        var report = primary.CreateReport();
        Assert.True(report.ReleasedLedgers is null || !report.ReleasedLedgers.ContainsKey(ledger.Id));

        // Controller receives Sealed and falls back to non-cooperative TakenOver.
        controller.Decisions.PrepareReleaseCompleted(sentPrepare, reply);
        var pass2 = controller.Decisions.RunPass();
        Assert.Empty(pass2.PrepareReleases);
        var sentReplace = Assert.Single(pass2.Transitions);
        var replace = Assert.IsType<Transition.Replace>(sentReplace.Transition.Value);
        Assert.Equal(PredecessorReleaseHint.TakenOver, replace.PredecessorHint);

        var replaceReply = await ExecuteTransitionAsync(controller, processes, sentReplace, token);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(replaceReply.Value);
        await RefreshViewsAsync(controller.View, processes, token);

        // Once the map records Closed(end), the blocked write finishes decisively (Fenced or Accepted).
        _ = await blockedWrite;
    }

    private static async Task RunControllerFailoverIdempotencyAsync(
        SimulatedConditionalRegister register,
        SimulatedObjectStore objects,
        ControlOptions options,
        RegisteredNodes registry,
        FrozenDictionary<NodeId, NodeLiveness> membership,
        ControllerHost controller1,
        List<ControllerHost> controllers,
        List<NodeProcess> processes,
        NodeProcess primary,
        LedgerMetadata ledger,
        JournalRowKey key,
        CancellationToken token)
    {
        var v1 = await WriteAcceptedAsync(primary.Journals, key, JournalVersion.Origin, "before-failover|"u8.ToArray(), token);

        // Controller 1 initiates PrepareRelease on primary A, which drains cleanly and records ReleasedLedgers.
        controller1.Decisions.RequestHandOff(primary.Node, generation: 1);
        var pass1 = controller1.Decisions.RunPass();
        var sentPrepare1 = Assert.Single(pass1.PrepareReleases);
        var reply1 = Assert.IsType<PrepareReleaseReply.Released>((await primary.Controller.PrepareReleaseAsync(sentPrepare1.Request, token)).Value);

        // Controller 1 dies BEFORE emitting Replace!
        // Part A: Controller 2 starts fresh (before receiving A's NodeReport) and repeats PrepareRelease -> idempotent Released(same tail).
        var controller2 = await CreateControllerAsync(register, objects, options, registry, membership, token);
        controllers.Add(controller2);
        controller2.Decisions.RequestHandOff(primary.Node, generation: 1);
        var pass2 = controller2.Decisions.RunPass();
        var sentPrepare2 = Assert.Single(pass2.PrepareReleases);
        var reply2 = Assert.IsType<PrepareReleaseReply.Released>((await primary.Controller.PrepareReleaseAsync(sentPrepare2.Request, token)).Value);
        Assert.Equal(reply1.ContiguousTail, reply2.ContiguousTail);

        // Part B: Controller 3 starts fresh and receives A's NodeReport carrying ReleasedLedgers -> skips PrepareRelease and emits Cooperative Replace directly.
        var controller3 = await CreateControllerAsync(register, objects, options, registry, membership, token);
        controllers.Add(controller3);
        var report = primary.CreateReport();
        Assert.NotNull(report.ReleasedLedgers);
        Assert.Equal(reply1.ContiguousTail, report.ReleasedLedgers[ledger.Id]);
        _ = controller3.Decisions.Report(report);
        controller3.Decisions.RequestHandOff(primary.Node, generation: 1);

        var pass3 = controller3.Decisions.RunPass();
        Assert.Empty(pass3.PrepareReleases);
        var sentReplace = Assert.Single(pass3.Transitions);
        var replace = Assert.IsType<Transition.Replace>(sentReplace.Transition.Value);
        Assert.Equal(ledger.Id, replace.Ledger);
        Assert.Equal(PredecessorReleaseHint.Cooperative, replace.PredecessorHint);

        var replaceReply = await ExecuteTransitionAsync(controller3, processes, sentReplace, token);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(replaceReply.Value);
        await RefreshViewsAsync(controller3.View, processes, token);

        // Once primary A observes the closed ledger in its map view, its next report prunes ReleasedLedgers.
        var reportAfterClose = primary.CreateReport();
        Assert.True(reportAfterClose.ReleasedLedgers is null || !reportAfterClose.ReleasedLedgers.ContainsKey(ledger.Id));

        // Successor serves the pre-failover write cleanly.
        var successor = processes.Single(p => p.Node == sentReplace.Executor);
        var read = await ReadAllBytesAsync(successor.Journals, key, token);
        Assert.Equal("before-failover|", Encoding.UTF8.GetString(read.Content));
        Assert.Equal(v1, read.Version);
    }

    private static async Task<ControllerHost> CreateControllerAsync(
        SimulatedConditionalRegister register,
        SimulatedObjectStore objects,
        ControlOptions options,
        RegisteredNodes registry,
        FrozenDictionary<NodeId, NodeLiveness> membership,
        CancellationToken token)
    {
        var view = await LedgerMapView.OpenAsync(register, token);
        var host = new ControllerHost(view, register, objects, options);
        host.Decisions.Observe(registry, membership);
        return host;
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
        public NodeProcess(NodeId node, LedgerMapView view, NodeTransport transport, IArchiveStore archive, LedgerTrace trace)
        {
            Node = node;
            View = view;
            TransportAddress = transport.Address;
            Reader = new LedgerReader(view, transport, SimulationEnvironment.Clock, node);
            Registry = new LedgerWriterRegistry(Reader, 1 << 25, SmallSegments.Options, SimulationEnvironment.Clock, NullLoggerFactory.Instance, SmallSegments.Random(node.ToString()));
            var journalOptions = new JournalStoreOptions
            {
                MaxBodyBytes = 4096,
                MaxBatchEntries = 4,
                MaxBatchBytes = 4096,
                MaxQueuedBytes = 1 << 20,
                WarmStateBytes = 8192,
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

        public string TransportAddress { get; }

        public LedgerReader Reader { get; }

        public LedgerWriterRegistry Registry { get; }

        public JournalStore Journals { get; }

        public NodeController Controller { get; }

        public NodeReport CreateReport() =>
            Controller.Report(
                diskPressure: false,
                lostReplicas: [],
                handingOff: false,
                refused: false,
                archiveGatePassed: true,
                draining: true);

        public async ValueTask DisposeAsync()
        {
            await Journals.DisposeAsync();
            await Registry.DisposeAsync();
            await Reader.DisposeAsync();
        }
    }

    private sealed class NodeTransport(SimulatedLedgerReplicaTransport replicas, string client) : ILedgerReplicaTransport
    {
        public string Address => client;

        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) => replicas.Open(node, ledger, options, client);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            replicas.HeartbeatAsync(node, ledgers, cancellationToken);
    }
}
