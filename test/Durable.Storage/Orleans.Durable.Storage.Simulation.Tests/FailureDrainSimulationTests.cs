using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// The failure-triggered drains (orleans.md §5.6, OR-I23) in deterministic simulation: a primary's node controller over
/// three LedgerStore replicas on simulated nodes serves three open ledgers, which writers fill without pause across lost
/// and duplicated messages, and drains them, and their closed predecessors, into a simulated object store through a
/// manifest committer, within three drain slots which the open ledgers' backlogs keep busy. Its segments end every two
/// keys, faster than one drain a segment keeps up with, so each ledger holds a backlog of closed segments when a replica's
/// node dies, which a failure-triggered drain takes in one start.
/// </summary>
/// <remarks>
/// <para>
/// Each scenario forces its failure by construction: a replica's death; two deaths, below <c>Q</c>, and one replica's
/// return; a death, the replica's return, and its death again; a false suspicion of a node which stays up; and a death
/// during whose drain the node stops. The node's membership view learns of each after a random delay, the detection.
/// </para>
/// <para>
/// The oracles, checked against the node's events and every manifest change: for each open ledger, a failure-triggered
/// drain is due once the node detects the death, starts at the first control interval once its ledger's own drain then
/// in flight ended, its wait for a slot, and reaches its marker in one start besides those which stalled, each later
/// start at the first interval once its stall's backoff passed and its slot is free, ending exactly at the marker, which lies past every row the ledger decided before the failure; so a row which
/// relies on one live replica is archived within the detection, the wait for a free slot, one control interval, and one
/// drain (OR-I23). Every drain's sections hold exactly the decided rows of its segments, and the drains of each ledger
/// together hold every decided row below its watermark (LSM-I2). A node which stops during the drain ends it at once with
/// no failure. Mutants whose drain is never due, which drain one segment a start, or whose bound is the marker's slot are
/// each caught, by their own message.
/// </para>
/// </remarks>
public sealed class FailureDrainSimulationTests
{
    private const int FirstSeed = 1;

    // Every change runs three seeds, and exploration runs more through CLOG_SIM_SEEDS (docs/spec/verification.md §7.4, §12).
    private const int SeedCount = 3;

    // One slot per open ledger, so a ledger's failure-triggered drain waits for a slot only while its own drain runs.
    private const int Concurrency = 3;
    private const string Oracle = "single-copy exposure";
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan ControlInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>A failure the scenario forces.</summary>
    public enum Failure
    {
        /// <summary>A replica's node dies.</summary>
        Death,

        /// <summary>Two replicas' nodes die, below <c>Q</c>, and then one returns.</summary>
        BelowQuorumThenBack,

        /// <summary>A replica's node dies, returns once its drain ended, and dies again.</summary>
        ReturnAndSecondDeath,

        /// <summary>The node's view declares a replica's node dead which stays up, and then live again.</summary>
        FalseSuspicion,

        /// <summary>A replica's node dies, and the primary's node stops during the drain.</summary>
        StopDuringTheDrain,
    }

    /// <summary>
    /// Gets the seed corpus of each failure: the death's, and the first seed of each scripted failure, whose path its
    /// construction forces on every seed (docs/spec/verification.md §12).
    /// </summary>
    public static TheoryData<Failure, int> Cases
    {
        get
        {
            var cases = new TheoryData<Failure, int>();
            foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, SeedCount))
            {
                cases.Add(Failure.Death, seed);
            }

            foreach (var failure in Enum.GetValues<Failure>().Where(static failure => failure != Failure.Death))
            {
                foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, 1))
                {
                    cases.Add(failure, seed);
                }
            }

            return cases;
        }
    }

    /// <summary>
    /// OR-I23 and LSM-I2 under each forced failure: each exposed ledger's failure-triggered drain starts within the bound
    /// of the detection and reaches its marker, past every row decided before the failure, in one start, and the drains
    /// hold exactly the decided rows.
    /// </summary>
    /// <param name="failure">The failure.</param>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Cases))]
    public void SingleCopyExposureEndsWithinDetectionAndOneDrain(Failure failure, int seed)
    {
        var run = SeededSimulation.Run(seed, environment => SimulateAsync(environment, failure, FailureDrainMutant.None));

        Assert.Contains(run.Trace, static entry => entry.Contains("oracles held", StringComparison.Ordinal));
        TestContext.Current.TestOutputHelper?.WriteLine(Invariant($"{run.Steps} steps, {run.Elapsed} virtual"));
        foreach (var entry in run.Trace.Where(static entry => entry.Contains("round ", StringComparison.Ordinal) || entry.Contains("oracles held", StringComparison.Ordinal)))
        {
            TestContext.Current.TestOutputHelper?.WriteLine(entry);
        }
    }

    /// <summary>The scenario replays from its seed (verification.md §7.4).</summary>
    [Fact]
    public void TheScenarioReplaysFromItsSeed()
    {
        var run = SeededSimulation.RunAndReplay(FirstSeed, environment => SimulateAsync(environment, Failure.Death, FailureDrainMutant.None));

        Assert.Contains(run.Trace, static entry => entry.Contains("oracles held", StringComparison.Ordinal));
    }

    /// <summary>
    /// The oracles catch, by their own messages, a node whose failure-triggered drain is never due, one whose drain takes
    /// one segment a start, and one whose bound is the marker's slot, below which its segment never closes. Each takes
    /// the same path on every seed, so one seed runs each (verification.md §12).
    /// </summary>
    /// <param name="mutant">The mutant.</param>
    /// <param name="message">A part of the message of the oracle which catches it.</param>
    [Theory]
    [InlineData(FailureDrainMutant.NeverDue, "no failure-triggered drain was due")]
    [InlineData(FailureDrainMutant.OneDrainAStart, "starts to reach its marker")]
    [InlineData(FailureDrainMutant.BoundAtTheMarker, "open below its bound")]
    internal void TheOraclesCatchEachMutant(FailureDrainMutant mutant, string message)
    {
        var failure = Assert.ThrowsAny<Exception>(() => SeededSimulation.Run(FirstSeed, environment => SimulateAsync(environment, Failure.Death, mutant), minimize: false));
        Assert.Contains(message, failure.ToString(), StringComparison.Ordinal);
    }

    private static async Task SimulateAsync(SimulationEnvironment environment, Failure failure, FailureDrainMutant mutant)
    {
        // The scenario owns the token, so no thread outside the simulation cancels it (docs/spec/verification.md §7.1).
        using var cancellation = new CancellationTokenSource();
        var network = environment.Network;
        network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        network.MaxJitter = TimeSpan.FromMilliseconds(4);
        var client = environment.Cluster.AddNode("client");
        var nodes = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, 3, cancellation.Token);
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, Deadline);
        _ = LedgerStoreInvariants.Register(environment, replicas);
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
        await using var objects = new SimulatedObjectStore(environment, "archive");
        await new Scenario(environment, replicas, register, objects, nodes, failure, mutant, cancellation.Token).RunAsync();
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>One of the node's events: its ID, when, its ledger, its marker or backoff, and its own words.</summary>
    private sealed record Event(int Id, DateTimeOffset At, LedgerId? Ledger, Slot? Marker, TimeSpan? Backoff, string Message);

    private sealed class Scenario(
        SimulationEnvironment environment,
        SimulatedLedgerReplicaTransport replicas,
        SimulatedConditionalRegister register,
        SimulatedObjectStore objects,
        LedgerStoreNode[] nodes,
        Failure failure,
        FailureDrainMutant mutant,
        CancellationToken cancellationToken)
    {
        private readonly SimulationRandom _random = environment.Random;
        private readonly Dictionary<(LedgerId Ledger, Slot Slot), (int Index, DateTimeOffset At)> _decided = [];
        private readonly List<(ManifestChange.Drain Change, DateTimeOffset At)> _applied = [];
        private readonly List<(DateTimeOffset At, LedgerId Ledger)> _starts = [];
        private readonly List<(DateTimeOffset At, LedgerId Ledger)> _failures = [];
        private readonly List<Event> _events = [];
        private readonly List<string> _violations = [];
        private readonly Dictionary<NodeId, int> _dead = [];
        private readonly CancellationTokenSource _stopping = new();
        private bool _writing = true;
        private DateTimeOffset? _stoppedAt;

        private DateTimeOffset Now => environment.TimeProvider.GetUtcNow();

        public async Task RunAsync()
        {
            environment.Invariants.Add("failure-drains", () => _violations.Count == 0 ? null : string.Join(" | ", _violations));
            var primary = nodes[0];
            var view = await LedgerMapView.OpenAsync(register, cancellationToken);
            await using var committer = new LedgerMapCommitter(register.Writer(0), int.MaxValue, view);
            var manifestView = new ManifestView(objects);
            await using var manifest = new ManifestCommitter(objects, manifestView, SimulationEnvironment.Clock, TimeSpan.Zero);
            var reader = new LedgerReader(view, new ProcessTransport(replicas, primary.Address), SimulationEnvironment.Clock, primary.NodeId);
            var registry = new LedgerWriterRegistry(reader, 1 << 25, SmallSegments.Options, SimulationEnvironment.Clock, NullLoggerFactory.Instance, SmallSegments.Random(primary.Address));
            try
            {
                var archive = new ArchiveOptions();
                var lsm = new NodeArchive.Lsm(new DrainWorker(objects, manifestView, reader, archive, SimulationEnvironment.Clock), new CompactionWorker(objects, new TailCache(objects, archive), manifestView, archive), manifestView);
                var node = new NodeController(primary.NodeId, generation: 1, view, registry, reader, new TransitionExecutor(registry, NullLogger<TransitionExecutor>.Instance), static () => true, () => _dead.ToImmutableDictionary(), lsm, SimulationEnvironment.Clock, TimeSpan.FromMinutes(1), Concurrency, new EventLogger(this))
                {
                    FailureDrainMutant = mutant,
                    FirstExposedBackoff = ControlInterval,
                };

                var ledgers = await OpenLedgersAsync(node, committer, view);

                // The ledger trace bounds each replica's contiguous tail by its writer's completed prefix, so each writer
                // is traced from its first proposal.
                foreach (var ledger in ledgers)
                {
                    replicas.LedgerTrace.TraceWriter(registry.TryGet(ledger.Id)!, primary.Address, ledger.Id);
                }

                environment.Network.MessageDropRate = 0.05;
                environment.Rpc.DuplicationRate = 0.05;
                var writes = SimulationTasks.WhenAll(ledgers.Select(ledger => WriteAsync(registry.TryGet(ledger.Id)!, ledger)));
                var control = ControlAsync(node, (change, token) => CommitAsync(manifest, change, token));
                await DelayAsync(100, 200);
                await FailAsync(ledgers);
                _writing = false;
                await writes;
                if (failure != Failure.StopDuringTheDrain)
                {
                    _stopping.Cancel();
                }

                await control;
                await node.DrainsEnded;
                CheckContents();
            }
            finally
            {
                await registry.DisposeAsync();
                await reader.DisposeAsync();
                _stopping.Dispose();
            }

            Assert.True(_violations.Count == 0, string.Join(Environment.NewLine, _violations));
            Record($"oracles held: {_starts.Count} starts, {_applied.Count} drains, {_decided.Count} rows, {_events.Count(static e => e.Id == 6249)} failure-triggered drains ended at their markers");
        }

        /// <summary>
        /// Bootstraps the root through the node, and splits it and its lower half, so the node is primary of three open
        /// ledgers whose closed predecessors it drains too.
        /// </summary>
        private async Task<ImmutableArray<LedgerMetadata>> OpenLedgersAsync(NodeController node, LedgerMapCommitter committer, LedgerMapView view)
        {
            ImmutableArray<NodeId> all = [.. nodes.Select(static node => node.NodeId)];
            Task<LedgerMapCommitOutcome> Commit(LedgerMapChange change, CancellationToken token) => committer.CommitAsync(change, token);
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await node.ExecuteAsync(new Transition.Bootstrap(new TransitionChoice(all[0], all, 4, 0, 0)), Commit, cancellationToken)).Value);
            var root = Assert.Single((await view.ReadAsync(cancellationToken)).Ledgers);
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await node.ExecuteAsync(new Transition.Split(root.Id, new TransitionChoice(all[0], all, 4, 0, view.Current.Epoch)), Commit, cancellationToken)).Value);
            var lower = (await view.ReadAsync(cancellationToken)).Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == KeyRange.Root.Children.Lower);
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await node.ExecuteAsync(new Transition.Split(lower.Id, new TransitionChoice(all[0], all, 4, 0, view.Current.Epoch)), Commit, cancellationToken)).Value);
            var open = (await view.ReadAsync(cancellationToken)).Ledgers.Where(static ledger => ledger.IsOpen).ToImmutableArray();
            Assert.Equal(3, open.Length);
            return open;
        }

        /// <summary>Forces the scenario's failure, and checks the exposure bound once each drain it triggers ended.</summary>
        private async Task FailAsync(ImmutableArray<LedgerMetadata> ledgers)
        {
            // The closed predecessors drain before any failure, so the open ledgers alone contend for the slots.
            while (_events.Count(static e => e.Id == 6254 && e.Ledger is { } ledger && ledger.Value <= 2) < 2 || _applied.Count(static applied => applied.Change.Ledger.Value <= 2 && applied.Change.Complete) < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), environment.TimeProvider, cancellationToken);
            }

            var victim = _random.Choose(nodes[1..]);
            var other = victim == nodes[1] ? nodes[2] : nodes[1];
            switch (failure)
            {
                case Failure.Death:
                case Failure.StopDuringTheDrain:
                    await CheckBoundAsync(ledgers, await DieAsync(victim, generation: 1), round: 1);
                    break;
                case Failure.BelowQuorumThenBack:
                    {
                        // Both die before the view declares either, so no interval sees exactly Q live.
                        var exposed = await DieAsync([victim, other]);
                        await DelayAsync(ControlInterval.Milliseconds * 2, ControlInterval.Milliseconds * 3);
                        if (_events.Any(static e => e.Id == 6247))
                        {
                            _violations.Add("A failure-triggered drain was due below Q live replicas.");
                        }

                        // The other node returns over its disk: Q replicas live, the victim dead, and the rows decided before
                        // its death exposed from now on.
                        await ReturnAsync(other);
                        await CheckBoundAsync(ledgers, exposed with { DetectedAt = Now }, round: 1);
                        break;
                    }

                case Failure.ReturnAndSecondDeath:
                    await CheckBoundAsync(ledgers, await DieAsync(victim, generation: 1), round: 1);
                    await ReturnAsync(victim);
                    await DelayAsync(50, 100);
                    await CheckBoundAsync(ledgers, await DieAsync(victim, generation: 2), round: 2);
                    break;
                case Failure.FalseSuspicion:
                    {
                        var exposed = Snapshot();
                        await DelayAsync(0, 50);
                        _dead[victim.NodeId] = 1;
                        Record($"node {victim.NodeId} falsely declared dead");
                        await CheckBoundAsync(ledgers, new Exposure(exposed, Now), round: 1);
                        _ = _dead.Remove(victim.NodeId);
                        break;
                    }

                default:
                    throw new InvalidOperationException("A failure the scenario forces.");
            }
        }

        /// <summary>Kills <paramref name="victim"/>, declared dead at <paramref name="generation"/>.</summary>
        private Task<Exposure> DieAsync(LedgerStoreNode victim, int generation) => DieAsync([victim], generation);

        /// <summary>
        /// Snapshots the highest slot each ledger decided, isolates and crashes <paramref name="victims"/>, and declares them
        /// dead together at <paramref name="generation"/> after a random detection delay.
        /// </summary>
        private async Task<Exposure> DieAsync(LedgerStoreNode[] victims, int generation = 1)
        {
            var exposed = Snapshot();
            foreach (var victim in victims)
            {
                victim.Isolate();
                await victim.CrashAsync(operatingSystem: false);
                Record($"node {victim.NodeId} crashed");
            }

            await DelayAsync(0, 100);
            foreach (var victim in victims)
            {
                _dead[victim.NodeId] = generation;
                Record($"node {victim.NodeId} declared dead at generation {generation}");
            }

            return new Exposure(exposed, Now);
        }

        private async Task ReturnAsync(LedgerStoreNode node)
        {
            await node.StartAsync(cancellationToken);
            node.Reconnect();
            _ = _dead.Remove(node.NodeId);
            Record($"node {node.NodeId} returned");
        }

        /// <summary>Returns the highest slot each ledger has decided.</summary>
        private Dictionary<LedgerId, Slot> Snapshot() =>
            _decided.Keys.GroupBy(static row => row.Ledger).ToDictionary(static rows => rows.Key, static rows => rows.Max(static row => row.Slot));

        /// <summary>
        /// Waits until each exposed ledger's failure-triggered drain of <paramref name="round"/> ended, or the run's patience
        /// passed, and checks OR-I23's bound for each: the drain was due, started at the first interval once the detection
        /// passed and its slot was free, and reached its marker, which lies past every row the ledger decided before the
        /// failure, in one start besides those which stalled, each later start likewise once its backoff passed, with a
        /// drain ending exactly at it and no step finding the segment before the marker open; a node which stops during the
        /// drain instead ends it at once, with no failure.
        /// </summary>
        private async Task CheckBoundAsync(ImmutableArray<LedgerMetadata> ledgers, Exposure exposure, int round)
        {
            var patience = exposure.DetectedAt + TimeSpan.FromSeconds(5);
            bool Ended(LedgerId ledger) => _events.Count(e => e.Id == 6249 && e.Ledger == ledger) >= round;

            // A drain is due at the first interval after the detection, so two intervals without one end the wait.
            bool NoneDue() => Now > exposure.DetectedAt + ControlInterval * 2 && !_events.Any(e => e.Id == 6247 && e.At >= exposure.DetectedAt);
            while (!ledgers.All(ledger => Ended(ledger.Id)) && Now < patience && !NoneDue() && !(failure == Failure.StopDuringTheDrain && _stoppedAt is not null))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), environment.TimeProvider, cancellationToken);
            }

            if (failure == Failure.StopDuringTheDrain)
            {
                if (_stoppedAt is null)
                {
                    _violations.Add("The node never stopped during a failure-triggered drain.");
                }

                return;
            }

            foreach (var ledger in ledgers)
            {
                CheckBound(ledger.Id, exposure, round);
            }
        }

        /// <summary>
        /// Returns when the drain of <paramref name="ledger"/> in flight at <paramref name="ready"/>, if any, ended, freeing
        /// its slot; otherwise <paramref name="ready"/>.
        /// </summary>
        private DateTimeOffset SlotFree(LedgerId ledger, DateTimeOffset ready)
        {
            var started = _starts.Where(start => start.Ledger == ledger && start.At <= ready).Select(static start => start.At).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
            var ended = _events.Where(e => e.Id == 6254 && e.Ledger == ledger && e.At >= started).Select(static e => e.At).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
            return started == DateTimeOffset.MinValue || ended <= ready ? ready : ended;
        }

        private void CheckBound(LedgerId ledger, Exposure exposure, int round)
        {
            var after = _events.Where(e => e.Ledger == ledger && e.At >= exposure.DetectedAt).ToList();
            if (!after.Any(static e => e.Id == 6247))
            {
                _violations.Add(Invariant($"{Oracle}: no failure-triggered drain was due for s{ledger} after the detection at {exposure.DetectedAt:O}."));
                return;
            }

            if (after.FirstOrDefault(static e => e.Id == 6249) is not { Marker: { } marker } reached)
            {
                _violations.Add(Invariant($"{Oracle}: s{ledger}'s failure-triggered drain never reached its marker after the detection at {exposure.DetectedAt:O}."));
                return;
            }

            // The first start begins at the first interval once the detection passed and the ledger's own drain then in
            // flight ended, its wait for a slot, and each later one likewise once the backoff of the stalled start before it
            // passed: a drain reaches its marker in one start besides those which stalled.
            var stalls = after.Where(e => e.Id == 6253 && e.At <= reached.At).ToList();
            var starts = after.Where(e => e.Id == 6252 && e.At <= reached.At).ToList();
            foreach (var stall in stalls.Where(static stall => stall.Message.Contains("open below its bound", StringComparison.Ordinal)))
            {
                _violations.Add(Invariant($"{Oracle}: s{ledger}'s failure-triggered drain found the segment before its marker open below its bound at {stall.At:O}."));
            }

            if (starts.Count > 1 + stalls.Count)
            {
                _violations.Add(Invariant($"{Oracle}: s{ledger}'s failure-triggered drain took {starts.Count} starts to reach its marker, with {stalls.Count} of them stalled."));
            }

            for (var start = 0; start < Math.Min(starts.Count, 1 + stalls.Count); start++)
            {
                var ready = start == 0 ? exposure.DetectedAt : stalls[start - 1].At + stalls[start - 1].Backoff!.Value;
                var bound = SlotFree(ledger, ready) + ControlInterval;
                if (starts[start].At > bound)
                {
                    _violations.Add(Invariant($"{Oracle}: s{ledger}'s failure-triggered drain started at {starts[start].At:O}, past {bound:O}, an interval after it was ready at {ready:O} and its slot free."));
                }
            }

            if (exposure.Decided.TryGetValue(ledger, out var last) && marker <= last)
            {
                _violations.Add(Invariant($"{Oracle}: s{ledger}'s marker at {marker} lies at or below slot {last}, decided before the failure."));
            }

            if (!_applied.Any(applied => applied.Change.Ledger == ledger && applied.Change.Below == marker && applied.At <= reached.At)
                && !_applied.Any(applied => applied.Change.Ledger == ledger && applied.Change.Below > marker && applied.Change.From < marker))
            {
                _violations.Add(Invariant($"{Oracle}: no drain of s{ledger} ended exactly at its marker {marker}."));
            }

            Record($"round {round}: s{ledger} exposed to slot {(exposure.Decided.TryGetValue(ledger, out var exposed) ? exposed.Value : -1)}, detected at {exposure.DetectedAt:O}, marker {marker}, {starts.Count} starts, {stalls.Count} stalled, reached at {reached.At:O}");
        }

        /// <summary>
        /// LSM-I2: every drain's sections hold exactly the decided rows of its segment, each in its slot range and as it was
        /// decided, and the drains of each ledger together hold every row it decided below the highest slot they reached.
        /// </summary>
        private void CheckContents()
        {
            var drained = new Dictionary<LedgerId, (HashSet<Slot> Slots, Slot Below)>();
            foreach (var (change, _) in _applied)
            {
                if (!drained.TryGetValue(change.Ledger, out var ledger))
                {
                    drained[change.Ledger] = ledger = ([], Slot.Zero);
                }

                drained[change.Ledger] = ledger with { Below = change.Below > ledger.Below ? change.Below : ledger.Below };
                foreach (var section in change.Sections)
                {
                    using var content = ArcBuffer.Wrap(objects.AppliedWritesOf(section.Object)[0]);
                    using var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(content).Value);
                    foreach (var row in decoded.Sections.Single(read => read.Metadata.Entry.Offset == section.Offset).Rows.Rows.ToArray())
                    {
                        var slot = row.Tag.Slot;
                        if (row.Tag.Ledger != change.Ledger || slot < change.From || slot >= change.Below)
                        {
                            _violations.Add(Invariant($"LSM-I2: s{change.Ledger}'s drain of [{change.From}, {change.Below}) holds a row tagged s{row.Tag.Ledger} at slot {slot}."));
                        }
                        else if (!_decided.TryGetValue((change.Ledger, slot), out var decided) || decided.Index != BitConverter.ToInt32(row.Row.Body.ToArray()))
                        {
                            _violations.Add(Invariant($"LSM-I2: s{change.Ledger}'s drain holds a row at slot {slot} which no proposal decided there."));
                        }

                        _ = drained[change.Ledger].Slots.Add(slot);
                    }
                }
            }

            foreach (var (ledger, (slots, below)) in drained)
            {
                var missing = _decided.Keys.Where(row => row.Ledger == ledger && row.Slot < below && !slots.Contains(row.Slot)).Select(static row => row.Slot).ToList();
                if (missing.Count > 0)
                {
                    _violations.Add(Invariant($"LSM-I2: s{ledger}'s drains reached slot {below} without the decided rows at {string.Join(", ", missing)}."));
                }
            }
        }

        /// <summary>
        /// Proposes one row at a time to <paramref name="ledger"/>, each after a short random delay, cycling through three
        /// keys of its range so segments end every two keys, and records each slot, its row's index, and when it was decided.
        /// </summary>
        private async Task WriteAsync(LedgerWriter writer, LedgerMetadata ledger)
        {
            byte[] keys = [.. Enumerable.Range(0, 256).Select(static key => (byte)key).Where(key => ledger.Range.Contains([key])).Take(3)];
            for (var index = 0; _writing; index++)
            {
                await DelayAsync(1, 4);
                using var payload = ArcBuffer.Wrap(BitConverter.GetBytes(index));
                using var row = new Row([keys[index % keys.Length]], RowKind.Append, payload);
                var outcome = await writer.ProposeAsync([row], cancellationToken);
                if (!outcome.TryGetValue(out ProposalOutcome.Durable durable))
                {
                    _violations.Add(Invariant($"s{ledger.Id}'s writer did not decide row {index}: {outcome}."));
                    return;
                }

                _decided[(ledger.Id, durable.Slot)] = (index, Now);
            }
        }

        /// <summary>Starts the node's drains due each control interval until it stops, recording each start and failure.</summary>
        private async Task ControlAsync(NodeController node, Func<ManifestChange, CancellationToken, Task<ManifestCommitOutcome>> commit)
        {
            var stopping = _stopping.Token;
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(ControlInterval, environment.TimeProvider, cancellationToken);
                if (stopping.IsCancellationRequested)
                {
                    break;
                }

                var at = Now;
                foreach (var ledger in node.StartDrains([], commit, (ledger, failed) => _failures.Add((Now, ledger)), stopping))
                {
                    _starts.Add((at, ledger));
                }
            }

            if (_stoppedAt is { } stoppedAt)
            {
                // The stop ends every drain at once, and none it ends reports a failure.
                await node.DrainsEnded;
                if (Now - stoppedAt > ControlInterval)
                {
                    _violations.Add(Invariant($"The node's drains ended {Now - stoppedAt} after its stop."));
                }

                if (_failures.Any(failed => failed.At >= stoppedAt))
                {
                    _violations.Add("A drain the stop ended reported a failure.");
                }
            }
        }

        /// <summary>
        /// Submits a drain's change through the manifest committer, recording when an applied one landed; in the stop
        /// scenario, the node stops at the first change of a failure-triggered drain, which the stop's cancellation ends.
        /// </summary>
        private async Task<ManifestCommitOutcome> CommitAsync(ManifestCommitter manifest, ManifestChange change, CancellationToken token)
        {
            if (failure == Failure.StopDuringTheDrain && _stoppedAt is null && change.Value is ManifestChange.Drain stopped
                && _events.Any(e => e.Id == 6248 && e.Ledger == stopped.Ledger))
            {
                _stoppedAt = Now;
                Record($"the node stops during s{stopped.Ledger}'s failure-triggered drain");
                _stopping.Cancel();
            }

            var outcome = await manifest.CommitAsync(change, token);
            if (change.Value is ManifestChange.Drain drain && outcome.Value is ManifestCommitOutcome.Applied)
            {
                _applied.Add((drain, Now));
            }

            return outcome;
        }

        private Task DelayAsync(int minimumMilliseconds, int maximumMilliseconds) =>
            Task.Delay(TimeSpan.FromMilliseconds(minimumMilliseconds) + _random.NextTimeSpan(TimeSpan.FromMilliseconds(maximumMilliseconds - minimumMilliseconds)), environment.TimeProvider, cancellationToken);

        private void Record(FormattableString entry) => environment.Trace.Record(Invariant(entry));

        /// <summary>The rows exposed by a failure, the highest slot each ledger decided before it, and when the node's view detected it.</summary>
        private sealed record Exposure(Dictionary<LedgerId, Slot> Decided, DateTimeOffset DetectedAt);

        /// <summary>Records the node's failure-triggered drains' events, with their ledgers, markers, and backoffs.</summary>
        private sealed class EventLogger(Scenario scenario) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                var fields = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
                T? Field<T>(string name)
                    where T : struct => fields.FirstOrDefault(field => field.Key == name).Value is T value ? value : null;
                var message = formatter(state, exception);
                scenario._events.Add(new Event(eventId.Id, scenario.Now, Field<LedgerId>("Ledger"), Field<Slot>("Marker"), Field<TimeSpan>("Backoff"), message));
                scenario.Record($"event {eventId.Id}: {message}");
            }
        }
    }

    /// <summary>A process's transport: each replica it opens is the process's own, which the node serves as the process's.</summary>
    private sealed class ProcessTransport(SimulatedLedgerReplicaTransport replicas, string client) : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) => replicas.Open(node, ledger, options, client);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            replicas.HeartbeatAsync(node, ledgers, cancellationToken);
    }
}
