using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// The controllers (orleans.md §5.6, §5.7) in deterministic simulation: two controller activations, a duplicate, each
/// with its own map view, which its own committer raises, its own membership view, which learns a node's failure late,
/// and its own registry view, which lags a retirement, choose transitions which each node's <see cref="NodeController"/>
/// executes over LedgerStore replicas, while the map register answers unknown outcomes, mismatches, and failed reads,
/// messages drop and repeat, one node is cut off, executors' replies are lost, and the committers race on one register.
/// </summary>
/// <remarks>
/// <para>
/// After every step each value the register applied keeps the map's invariants (OR-I12). Every transition names
/// <c>N</c> distinct nodes, its executor first, each of which may be given work at its <c>ChosenAt</c> and in the membership
/// view its controller chose from, or is a degraded takeover or leaving primary's hand-off (OR-I5, <see cref="ReplicaChoice"/>). Every committer answer holds in the register's history and names an epoch
/// which had landed when it was answered, every executor verdict and report reply names an epoch which had landed, and
/// each controller's replies never go back (OR-I19). Once the gate answered a retired node's disk releasable, no later
/// map value names the node (OR-I4, the gate checker).
/// </para>
/// <para>
/// Once faults stop, the controllers serve every range, split toward their target, and spread the primaries. Scripted
/// steps then take their paths by construction, with every fault off: both controllers taking over one dead primary's
/// ledger from one stale value, of which one applies and one is superseded, while a manifest replace of the first is held
/// at the store, which answers only after both takeovers; a primary cut off while another node stops, whose ledgers are
/// taken over onto degraded successors which keep it, and which returns with holey replicas (OR-I5); a replica its live node lost to media damage, which the node's
/// store lists once a read finds it and the controllers roll out at once by a repair onto the same nodes, whose successor
/// replica on the node is fresh, though <c>G</c> is an hour, while the ledger keeps admitting (LR-I2); a retirement, which one controller learns late, with a stopping
/// node's hand-off request, in which the first hand-off of the retired primary's ledger is lost unanswered and expires by
/// <c>TransitionTimeout</c>, its only expiry by the timeout whether from a late reply or a lagging view, and no ledger goes
/// back to the stopping node, and after which a transition whose executor is retired while it recovers answers
/// <c>Refused</c> (OR-I3); the retirement's gate, which opens once the controllers' passes raised the map's floor and the
/// closed ledgers naming the node dropped, after which the second controller, paused since before the retirement, sends
/// a roll placing the node which reads <c>Superseded</c> (OR-I4); and cold siblings merged. Each step asserts its paths,
/// and the run asserts the transition paths they reach. Each mutant runs from the shortest scenario which reaches its
/// oracle.
/// </para>
/// <para>
/// Each scripted step forces a path the review of the controllers required, so the run's steps are its cost
/// (test/Durable.Storage/docs/spec/verification.md §12): the rounds are two, the retirement runs while the map holds the target's few ledgers
/// and repairs the fewest, and the timeout is the shortest which no reply reaches.
/// </para>
/// </remarks>
public sealed class ControllerSimulationTests
{
    private const int NodeCount = 4;
    private const int FirstSeed = 1;

    // Every change runs one seed, and exploration runs more through DURABLE_STORAGE_SIM_SEEDS (test/Durable.Storage/docs/spec/verification.md §7.4, §12).
    private const int SeedCount = 1;
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);

    // Scale the lease duration to this scenario's millisecond network delays so unplanned takeovers wait out real
    // leases without exceeding TransitionTimeout or the Clockwork replay artifact size limit.
    private static readonly LedgerLeaseOptions Lease = new()
    {
        LeaseDuration = TimeSpan.FromMilliseconds(50),
        LeaseGuardBand = TimeSpan.FromMilliseconds(10),
    };

    private enum Mutant
    {
        None,

        /// <summary>Controller 0's projection hides the retirement, so its placement may name a retired node (OR-I5).</summary>
        RetirementIgnored,

        /// <summary>Each report reply names one epoch past the controller's newest (OR-I19).</summary>
        ReplyEpochAhead,

        /// <summary>The scaffold relays each committer's <c>Superseded</c> as <c>Applied</c> at its epoch (the history oracle).</summary>
        SupersededReadAsApplied,

        /// <summary>A rogue writer puts a map value with overlapping open ledgers in the register (the map's invariants).</summary>
        RogueMapWrite,

        /// <summary>The scaffold records each committer answer as given before its write landed (OR-I19's landing check).</summary>
        AnsweredBeforeItsWriteLanded,

        /// <summary>
        /// The first controller's production gate ignores the floor, opening once no ledger names the retired node (OR-I4).
        /// </summary>
        GateIgnoresTheFloor,

        /// <summary>
        /// A controller whose registry view lies below the map's floor sends transitions all the same, each of which the
        /// commit rules refuse (LG-I11).
        /// </summary>
        RegistryBehindIgnored,

        /// <summary>The controllers ignore reports' lost replicas, so none is repaired (orleans.md §5.6, LR-I2).</summary>
        LostReplicaIgnored,

        /// <summary>The controllers place every transition degraded, a WAL-release roll among them (OR-I5).</summary>
        DegradedEverywhere,
    }

    /// <summary>
    /// The orders of a membership change in which one of a ledger's replicas dies, an operator retires a second, and the
    /// dead one returns or is replaced (<see cref="MembershipChangesLeaveEveryLedgerServedOnNLiveReplicas"/>).
    /// </summary>
    public enum MembershipChange
    {
        /// <summary>The second is retired while the first is dead, then the first returns over its disk.</summary>
        RetireThenReturn,

        /// <summary>The first returns over its disk, then the second is retired.</summary>
        ReturnThenRetire,

        /// <summary>The second is retired while the first is dead, then a new node replaces the first at its ordinal.</summary>
        RetireThenReplace,

        /// <summary>A new node replaces the first at its ordinal, then the second is retired.</summary>
        ReplaceThenRetire,

        /// <summary>
        /// A new node replaces the first while membership still shows it live, membership then declares it dead, and the
        /// second is retired.
        /// </summary>
        ReplaceBeforeDeath,
    }

    /// <summary>Gets the seed corpus.</summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    /// <summary>OR-I5, OR-I12, OR-I19 under duplicated controllers with stale views, and the scripted paths.</summary>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void DuplicatedControllersWithStaleViewsKeepTheMapsInvariants(int seed) =>
        _ = SeededSimulation.Run(seed, static environment => SimulateAsync(environment, Mutant.None));

    /// <summary>
    /// OR-I4, OR-I5, and liveness through a membership change, the scenario which left a repository unavailable for good
    /// in Enroute's deterministic simulation (enroute-sh/enroute#7): with <c>N = 3</c> and every range served, one
    /// replica's node dies with its silo, an operator retires a second replica's node, and the dead node returns over its
    /// disk or a new node replaces it at its ordinal, in each order of <paramref name="change"/>, each step reached by
    /// construction. Within a bounded virtual time every open ledger is served by a live primary on <c>N</c> live, non-retired
    /// replicas; each retired node's gate opens; the controllers then send nothing, so no takeover or repair repeats, and no
    /// range is taken over more than twice; and a write to each open ledger reads back from each of its replicas. Every
    /// placement passes the OR-I5 checker and every committer answer the history oracle throughout.
    /// </summary>
    /// <param name="change">The order of the membership change.</param>
    [Theory]
    [InlineData(MembershipChange.RetireThenReturn)]
    [InlineData(MembershipChange.ReturnThenRetire)]
    [InlineData(MembershipChange.RetireThenReplace)]
    [InlineData(MembershipChange.ReplaceThenRetire)]
    [InlineData(MembershipChange.ReplaceBeforeDeath)]
    public void MembershipChangesLeaveEveryLedgerServedOnNLiveReplicas(MembershipChange change) =>
        _ = SeededSimulation.Run(FirstSeed, environment => SimulateAsync(environment, Mutant.None, change));

    /// <summary>The OR-I5 check flags a controller whose registry projection hides a retirement.</summary>
    [Fact]
    public void ThePlacementCheckFlagsAControllerBlindToARetirement() => AssertEverySeedFails(Mutant.RetirementIgnored, "OR-I5");

    /// <summary>The reply check flags a report reply naming an epoch which has not landed.</summary>
    [Fact]
    public void TheReplyCheckFlagsAnEpochAhead() => AssertEverySeedFails(Mutant.ReplyEpochAhead, "OR-I19");

    /// <summary>The history oracle flags a superseded change answered applied.</summary>
    [Fact]
    public void TheHistoryOracleFlagsASupersededChangeReadAsApplied() => AssertEverySeedFails(Mutant.SupersededReadAsApplied, "history");

    /// <summary>The history oracle's landing check flags an answer given before its write landed.</summary>
    [Fact]
    public void TheLandingCheckFlagsAnAnswerBeforeItsWriteLanded() => AssertEverySeedFails(Mutant.AnsweredBeforeItsWriteLanded, "before its value was durable");

    /// <summary>The map's invariant check flags a value with overlapping open ledgers.</summary>
    [Fact]
    public void TheMapCheckFlagsOverlappingOpenLedgers() => AssertEverySeedFails(Mutant.RogueMapWrite, "ledger-map");

    /// <summary>
    /// The gate checker flags a gate which opened before the map's floor covered the retirement: the paused duplicate's
    /// install, chosen before the retirement, then names the retired node again.
    /// </summary>
    [Fact]
    public void TheGateCheckerFlagsAGateWhichIgnoresTheFloor() => AssertEverySeedFails(Mutant.GateIgnoresTheFloor, "OR-I4");

    /// <summary>
    /// The pause check flags a controller which, its registry view below the map's floor, sends transitions the commit rules
    /// would refuse, and asks for pass after pass.
    /// </summary>
    [Fact]
    public void ThePauseCheckFlagsAControllerWhichSendsBelowTheFloor() => AssertEverySeedFails(Mutant.RegistryBehindIgnored, "LG-I11");

    /// <summary>The lost-replica step flags controllers which ignore the replicas reports name lost.</summary>
    [Fact]
    public void TheLostReplicaStepFlagsControllersWhichIgnoreTheLoss() => AssertEverySeedFails(Mutant.LostReplicaIgnored, "rolled out of its lost replica");

    /// <summary>
    /// The OR-I5 check flags controllers which place every transition degraded: in the degraded step, a WAL-release roll of
    /// a ledger whose primary serves keeps the cut-off replica.
    /// </summary>
    [Fact]
    public void ThePlacementCheckFlagsADegradedWalReleaseRoll() => AssertEverySeedFails(Mutant.DegradedEverywhere, "OR-I5");

    private static void AssertEverySeedFails(Mutant mutant, string oracle)
    {
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, SeedCount))
        {
            var failure = Assert.ThrowsAny<Exception>(() => SeededSimulation.Run(seed, environment => SimulateAsync(environment, mutant), minimize: false));
            Assert.Contains(oracle, failure.ToString(), StringComparison.Ordinal);
        }
    }

    private static async Task SimulateAsync(SimulationEnvironment environment, Mutant mutant, MembershipChange? change = null)
    {
        // The scenario owns the token, so no thread outside the simulation cancels it (test/Durable.Storage/docs/spec/verification.md §7.1).
        using var cancellation = new CancellationTokenSource();
        var network = environment.Network;
        network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        network.MaxJitter = TimeSpan.FromMilliseconds(4);
        network.MessageDropRate = 0.05;
        environment.Rpc.DuplicationRate = 0.05;
        var client = environment.Cluster.AddNode("client");
        // A membership change's scenario starts a spare node, unregistered until it replaces a dead one.
        var nodes = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, change is null ? NodeCount : NodeCount + 1, cancellation.Token, Lease);
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, Deadline);
        _ = LedgerStoreInvariants.Register(environment, replicas);
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map")
        {
            UnknownOutcomeRate = 0.1,
            AppliedMismatchRate = 0.05,
            ReadFailureRate = 0.05,
        };
        await using var objects = new SimulatedObjectStore(environment, "archive");
        await new Scenario(environment, replicas, register, objects, nodes, mutant, change, cancellation.Token).RunAsync();
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>One node's process: its map view, reader, registry, executor, and node controller.</summary>
    private sealed class Process
    {
        public Process(NodeId node, LedgerMapView view, ILedgerReplicaTransport transport, Func<bool> active)
        {
            Node = node;
            View = view;
            Reader = new LedgerReader(view, transport, SimulationEnvironment.Clock, node) { LeaseOptions = Lease };
            Registry = new LedgerWriterRegistry(Reader, 1 << 25, SmallSegments.Options, SimulationEnvironment.Clock, NullLoggerFactory.Instance, SmallSegments.Random(node.ToString()));
            // The scenario's nodes drain nothing: an LSM of their own, which no request reaches.
            var objects = new InMemoryObjectStore();
            var manifest = new ManifestView(objects);
            var archive = new ArchiveOptions();
            var lsm = new NodeArchive.Lsm(new DrainWorker(objects, manifest, Reader, archive, SimulationEnvironment.Clock), new CompactionWorker(objects, new TailCache(objects, archive), manifest, archive), manifest);
            Controller = new NodeController(node, generation: 1, view, Registry, Reader, new TransitionExecutor(Registry, NullLogger<TransitionExecutor>.Instance), active, static () => ImmutableDictionary<NodeId, int>.Empty, lsm, SimulationEnvironment.Clock, TimeSpan.FromMinutes(1));
        }

        public NodeId Node { get; }

        public LedgerMapView View { get; }

        public LedgerReader Reader { get; }

        public LedgerWriterRegistry Registry { get; }

        public NodeController Controller { get; }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await Reader.DisposeAsync();
        }
    }

    /// <summary>A controller activation: its decisions, its views, and its committers, as a controller grain holds them.</summary>
    private sealed class Controller
    {
        private readonly LedgerMapCommitter _committer;
        private readonly ManifestCommitter _manifest;

        public Controller(int writer, LedgerMapView view, IConditionalRegister register, SimulatedObjectStore objects, ControlOptions options)
        {
            Writer = writer;
            View = view;
            _committer = new LedgerMapCommitter(register, int.MaxValue, view);
            var manifestView = new ManifestView(objects);
            _manifest = new ManifestCommitter(objects, manifestView, SimulationEnvironment.Clock, TimeSpan.Zero);
            Decisions = new ClusterController(options, view, _committer, new ClusterArchive.Lsm(manifestView, _manifest, new ArchiveOptions()), SimulationEnvironment.Clock, NullLogger.Instance);
        }

        public int Writer { get; }

        public ClusterController Decisions { get; }

        public LedgerMapView View { get; }

        public Dictionary<NodeId, NodeLiveness> Membership { get; } = [];

        /// <summary>Gets or sets the membership view the controller last observed, from which its next pass chooses.</summary>
        public FrozenDictionary<NodeId, NodeLiveness> Observed { get; set; } = [];

        /// <summary>Gets each node's latest report this controller took, which tells which ledgers it reported unserved.</summary>
        public Dictionary<NodeId, NodeReport> Reports { get; } = [];

        /// <summary>Gets or sets the registry value this controller's view holds, which may lag the newest.</summary>
        public RegisteredNodes Registry { get; set; } = RegisteredNodes.Empty;

        public ulong LastReplyEpoch { get; set; }

        public async ValueTask DisposeAsync()
        {
            await _committer.DisposeAsync();
            await _manifest.DisposeAsync();
        }
    }

    private sealed class Scenario(
        SimulationEnvironment environment,
        SimulatedLedgerReplicaTransport replicas,
        SimulatedConditionalRegister register,
        SimulatedObjectStore objects,
        LedgerStoreNode[] nodes,
        Mutant mutant,
        MembershipChange? change,
        CancellationToken cancellationToken)
    {
        private readonly ControlOptions _options = new()
        {
            Replicas = 3,
            SequencerWindow = 4,
            LedgersPerNode = 1,
            MaxConcurrentTransitions = 2,
            // About twice the longest execution (including the 50 ms lease-expiry wait on unplanned takeovers), so only the
            // scripted lost send expires; ExecuteAsync flags any reply which arrives at or after it, and the forced timeout waits it out.
            TransitionTimeout = TimeSpan.FromMilliseconds(200),
            PrepareReleaseTimeout = TimeSpan.FromMilliseconds(150),
            ReplicaGrace = TimeSpan.FromHours(1),
            SplitBytesPerSecond = long.MaxValue,
        };

        private readonly SimulationRandom _random = environment.Random;
        private readonly List<Process> _processes = [];
        private readonly List<Controller> _controllers = [];
        private readonly List<Task> _executions = [];
        private readonly List<LedgerMapSubmission> _submissions = [];
        private readonly List<string> _violations = [];
        private readonly HashSet<NodeId> _isolated = [];
        private readonly HashSet<NodeId> _activeOnce = [];
        private readonly Dictionary<ulong, RegisteredNodes> _registries = [];
        private readonly Dictionary<(TransitionPurpose Purpose, string Reply), int> _paths = [];
        private readonly List<(SentTransition Sent, string Reply)> _answers = [];
        private readonly Dictionary<NodeId, ulong> _gateOpened = [];
        private readonly HashSet<LedgerId> _traced = [];
        private int _gateChecked;
        private int _sent;
        private bool _holdFloorRaises;
        private ForcedTimeout? _timeout;
        private readonly List<LedgerMapValue> _history = [LedgerMapValue.Empty];
        private RegisteredNodes _registry = RegisteredNodes.Empty;
        private LedgerMapValue _previous = LedgerMapValue.Empty;
        private int _checked;
        private (NodeId Node, int Generation)? _stopping;
        private readonly Dictionary<TransitionPurpose, int> _degraded = [];

        public async Task RunAsync()
        {
            environment.Invariants.Add("ledger-map", CheckMap);
            environment.Invariants.Add("retirement-gate", CheckGate);

            // A violation stops the run at the step after it, so a mutant's run ends at its first.
            environment.Invariants.Add("controllers", () => _violations.Count == 0 ? null : string.Join(" | ", _violations));
            try
            {
                // The registry's projection, which names no silo: Orleans' silo addresses arm an interning timer which would
                // outlive the simulation (verification §7.1).
                Publish(nodes.Take(NodeCount).Select((node, index) => (node.NodeId, index)).ToFrozenDictionary(static entry => entry.NodeId, static entry => new RegisteredNode(RetiredRevision: null, Invariant($"domain-{entry.index}"), Generation: 1, Ordinal: null)));
                foreach (var node in nodes)
                {
                    var view = await RetryAsync(() => LedgerMapView.OpenAsync(register, cancellationToken));
                    var id = node.NodeId;
                    _processes.Add(new Process(id, view, new ProcessTransport(replicas, node.Address), () => _registry.Find(id) is { IsRetired: false } || _activeOnce.Remove(id)));
                }

                for (var writer = 0; writer < 2; writer++)
                {
                    var view = await RetryAsync(() => LedgerMapView.OpenAsync(register, cancellationToken));
                    var controller = new Controller(writer, view, register.Writer(writer), objects, _options) { Registry = _registry };
                    foreach (var node in nodes)
                    {
                        controller.Membership[node.NodeId] = NodeLiveness.Live.Instance;
                    }

                    _controllers.Add(controller);
                }

                if (mutant == Mutant.RogueMapWrite)
                {
                    await WriteRogueValueAsync();
                }

                if (change is { } membership)
                {
                    Heal();
                    await ServeEveryRangeAsync(ranges: NodeCount, done: Spread);
                    await ChangeMembershipAsync(membership);
                    await SimulationTasks.WhenAll(_executions);
                    await VerifyAnswersAsync();
                    Assert.True(_violations.Count == 0, string.Join(Environment.NewLine, _violations));
                    return;
                }

                if (mutant is Mutant.GateIgnoresTheFloor or Mutant.RegistryBehindIgnored)
                {
                    // The shortest scenario which opens a gate: serve the first range, retire one of its replicas, and roll
                    // it out. Under the gate mutant, the first controller's gate, which the scenario asks, ignores the floor;
                    // under the pause mutant, the duplicate below the floor sends transitions all the same.
                    _controllers[0].Decisions.GateIgnoresTheFloorMutant = mutant == Mutant.GateIgnoresTheFloor;
                    Heal();
                    await ServeEveryRangeAsync(ranges: 1, oneController: true);
                    var root = (await ReadEveryViewAsync()).Ledgers.First(static ledger => ledger.IsOpen);
                    var replica = root.Replicas.First(node => node != root.Primary.Node);
                    var retiredAt = _registry.Revision + 1;
                    _holdFloorRaises = true;
                    Publish(_registry.Nodes.ToFrozenDictionary(static entry => entry.Key, entry => entry.Key == replica ? entry.Value with { RetiredRevision = retiredAt } : entry.Value));
                    _controllers[0].Registry = _controllers[1].Registry = _registry;
                    await ServeEveryRangeAsync(ranges: 1, done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && Names(ledger, replica)), oneController: true);
                    await OpenTheGateBeforeAPausedDuplicateAsync(replica, retiredAt);
                    return;
                }

                if (mutant == Mutant.LostReplicaIgnored)
                {
                    // The shortest scenario which reaches the lost-replica step: serve the first range, then lose a replica.
                    foreach (var controller in _controllers)
                    {
                        controller.Decisions.LostReplicasIgnoredMutant = true;
                    }

                    Heal();
                    await ServeEveryRangeAsync(ranges: 1);
                    await RepairALostReplicaAsync();
                    return;
                }

                if (mutant == Mutant.DegradedEverywhere)
                {
                    // The shortest scenario which reaches the degraded step: serve a range per node, then take one over while
                    // another node stops.
                    foreach (var controller in _controllers)
                    {
                        controller.Decisions.DegradedPlacementMutant = true;
                    }

                    Heal();
                    await ServeEveryRangeAsync(ranges: NodeCount, done: Spread);
                    await TakeOverDegradedAsync();
                    return;
                }

                if (mutant == Mutant.RetirementIgnored)
                {
                    // The shortest scenario which reaches the OR-I5 check of a retirement: serve the first range, then retire its
                    // primary; the blind controller's next split keeps the retired replica.
                    Heal();
                    await ServeEveryRangeAsync(ranges: 1);
                    await RetireANodeAsync();
                    return;
                }

                var faulty = _random.Choose(nodes);
                await SimulationTasks.WhenAll(ControlAsync(_controllers[0], rounds: 2), ControlAsync(_controllers[1], rounds: 2), CutOffAsync(faulty));
                Record($"rounds ended");
                await SimulationTasks.WhenAll(_executions);
                Heal();

                // Primary balance spreads the primaries, so a cut-off primary leaves few ledgers to take over.
                await ServeEveryRangeAsync(ranges: NodeCount, done: Spread);
                Record($"every open range served");
                await RaceDuplicateTakeoversAsync();
                await ServeEveryRangeAsync(ranges: NodeCount);
                await TakeOverDegradedAsync();
                await ServeEveryRangeAsync(ranges: NodeCount, done: Balanced);
                await RepairALostReplicaAsync();
                await RetireANodeAsync();
                await MergeColdSiblingsAsync();
                await SimulationTasks.WhenAll(_executions);
                await VerifyAnswersAsync();
                AssertPaths();
                Assert.True(_violations.Count == 0, string.Join(Environment.NewLine, _violations));
            }
            finally
            {
                foreach (var controller in _controllers)
                {
                    await controller.DisposeAsync();
                }

                foreach (var process in _processes)
                {
                    await process.DisposeAsync();
                }
            }
        }

        /// <summary>Publishes a registry value at the next revision, which each controller observes when its view takes it.</summary>
        private void Publish(FrozenDictionary<NodeId, RegisteredNode> entries)
        {
            _registry = new RegisteredNodes(_registry.Revision + 1, entries);
            _registries[_registry.Revision] = _registry;
        }

        /// <summary>
        /// The gate checker of verification.md §3.2 (OR-I4): no map value after the one from which the gate answered a node's
        /// disk releasable names the node in any ledger.
        /// </summary>
        private string? CheckGate()
        {
            for (; _gateChecked < _history.Count; _gateChecked++)
            {
                var value = _history[_gateChecked];
                foreach (var (node, epoch) in _gateOpened)
                {
                    if (value.Epoch > epoch && value.Ledgers.Any(ledger => ledger.Primary.Node == node || ledger.Replicas.Contains(node)))
                    {
                        return Invariant($"OR-I4: the gate answered node {node}'s disk releasable at epoch {epoch}, and the map at epoch {value.Epoch} names it.");
                    }
                }
            }

            return null;
        }

        /// <summary>Checks each value the register applied since the last step: epochs advance by one, each keeps the map's invariants.</summary>
        private string? CheckMap()
        {
            for (; _checked < register.AppliedWrites.Count; _checked++)
            {
                if (LedgerMapValue.Decode(register.AppliedWrites[_checked].AsSpan()).Value is not LedgerMapValue value)
                {
                    return Invariant($"the register applied a value which is not a ledger map, at write {_checked}");
                }

                _history.Add(value);
                if (value.Epoch != _previous.Epoch + 1)
                {
                    return Invariant($"epoch {value.Epoch} follows epoch {_previous.Epoch}");
                }

                if (LedgerMapReference.Violation(_previous, value) is { } violation)
                {
                    return violation;
                }

                _previous = value;
            }

            return null;
        }

        /// <summary>
        /// A controller's rounds: it reads its map view only now and then, so it chooses from stale values; it takes each
        /// node's report; and it sends what each pass chooses without waiting for the executions, as its grain's turns do.
        /// </summary>
        private async Task ControlAsync(Controller controller, int rounds)
        {
            for (var round = 0; round < rounds; round++)
            {
                await DelayAsync(40);
                await PassAsync(controller, readView: _random.Chance(0.5));
            }
        }

        private async Task PassAsync(Controller controller, bool readView, Action<Controller>? beforePass = null)
        {
            if (readView)
            {
                _ = await RetryAsync(() => controller.View.ReadAsync(cancellationToken));
            }

            Observe(controller);
            beforePass?.Invoke(controller);
            // A spare node's process reports only once its node is registered.
            foreach (var process in _processes.Where(process => !_isolated.Contains(process.Node) && _registry.Nodes.ContainsKey(process.Node)))
            {
                // Every ledger is idle: a node reports no appends for each ledger it is primary of, so cold siblings merge.
                var idle = process.View.Current.Ledgers.Where(ledger => ledger.IsOpen && ledger.Primary.Node == process.Node).ToImmutableDictionary(static ledger => ledger.Id, static _ => 0L);
                // A process stands for each of its node's silos: it reports the generation its registry entry names.
                var report = process.Controller.Report(diskPressure: false, LostReplicas(process.Node), handingOff: false, refused: false, archiveGatePassed: false, draining: false) with { Generation = _registry.Nodes[process.Node].Generation, ProposedBytesPerSecond = idle };
                var reply = controller.Decisions.Report(report);
                controller.Reports[process.Node] = report;
                if (mutant == Mutant.ReplyEpochAhead)
                {
                    reply = reply with { MapEpoch = reply.MapEpoch + 1 };
                }

                CheckReply(controller, reply.MapEpoch);
                try
                {
                    await process.Controller.ObserveAsync(reply, cancellationToken);
                }
                catch (Exception failure) when (failure is IOException or TimeoutException or ArgumentOutOfRangeException)
                {
                    // The node's view reaches the epoch at a later reply; an epoch past the register's fails the reply check.
                }
            }

            var pass = controller.Decisions.RunPass();
            foreach (var prepare in pass.PrepareReleases)
            {
                _executions.Add(PrepareReleaseAsync(controller, prepare));
            }

            _sent += pass.Transitions.Length;
            foreach (var sent in pass.Transitions)
            {
                CheckPlacement(controller, sent);
                if (!Withhold(controller, sent))
                {
                    _executions.Add(ExecuteAsync(controller, sent));
                }
            }

            if (pass.FloorRaise is { } floor)
            {
                if (_holdFloorRaises)
                {
                    // Held until the gate step's drops: the raise ends unsubmitted, and a later pass returns it again.
                    controller.Decisions.FloorRaiseEnded();
                }
                else
                {
                    _executions.Add(RaiseFloorAsync(controller, floor));
                }
            }

            if (controller.Writer == 0)
            {
                AskTheGate();
            }
        }

        /// <summary>
        /// Asks the first controller's gate, the production one, of every node the registry holds retired, and records the
        /// epoch from which it first answers a node releasable, which the gate checker holds every later map value to.
        /// </summary>
        private void AskTheGate()
        {
            foreach (var (node, entry) in _registry.Nodes)
            {
                if (entry.IsRetired && !_gateOpened.ContainsKey(node) && _controllers[0].Decisions.Retirement(node).Value is NodeStanding.Retired { IsReleasable: true } open)
                {
                    _gateOpened[node] = open.Epoch;
                    _gateChecked = 0;
                    Record($"node {node}'s gate opened at epoch {open.Epoch}");
                }
            }
        }

        /// <summary>
        /// Raises the map's retirement floor through the controller's committer, as its grain does after a pass, and ends the
        /// raise: a later pass raises it again while the map's floor is below the retirement.
        /// </summary>
        private async Task RaiseFloorAsync(Controller controller, ulong floor)
        {
            try
            {
                _ = await CommitAsync(controller, new LedgerMapChange.RaiseFloor(floor), cancellationToken);
            }
            catch (Exception failure) when (failure is IOException or TimeoutException)
            {
                // The next pass raises it again.
            }
            finally
            {
                controller.Decisions.FloorRaiseEnded();
            }
        }

        /// <summary>
        /// Loses, unexecuted and unanswered, controller 0's first hand-off of the forced timeout's ledger, so only
        /// <c>TransitionTimeout</c> ends it, and records the controller's next send of that ledger.
        /// </summary>
        private bool Withhold(Controller controller, SentTransition sent)
        {
            if (_timeout is not { } timeout || controller.Writer != 0 || !sent.Transition.Closes.Contains(timeout.Ledger))
            {
                return false;
            }

            if (timeout.Withheld is null)
            {
                if (sent.Purpose != TransitionPurpose.Handoff)
                {
                    return false;
                }

                (timeout.Withheld, timeout.WithheldAt) = (sent, environment.TimeProvider.GetUtcNow());
                return true;
            }

            if (timeout.Resent is null)
            {
                (timeout.Resent, timeout.ResentAt) = (sent, environment.TimeProvider.GetUtcNow());
            }

            return false;
        }

        /// <summary>Gives the controller its registry view, hiding a retirement under the mutant, and its membership view.</summary>
        private void Observe(Controller controller)
        {
            var registry = controller.Registry;
            if (mutant == Mutant.RetirementIgnored && controller.Writer == 0)
            {
                registry = registry with { Nodes = registry.Nodes.ToFrozenDictionary(static entry => entry.Key, static entry => entry.Value with { RetiredRevision = null }) };
            }

            controller.Observed = controller.Membership.ToFrozenDictionary();
            controller.Decisions.Observe(registry, controller.Observed);
        }

        /// <summary>
        /// OR-I19: a reply names an epoch which had landed, and a controller's replies never go back.
        /// </summary>
        private void CheckReply(Controller controller, ulong epoch)
        {
            if (epoch > (ulong)register.AppliedWrites.Count)
            {
                _violations.Add(Invariant($"OR-I19: controller {controller.Writer} replied epoch {epoch} when {register.AppliedWrites.Count} writes had landed."));
            }

            if (epoch < controller.LastReplyEpoch)
            {
                _violations.Add(Invariant($"OR-I19: controller {controller.Writer} replied epoch {epoch} after {controller.LastReplyEpoch}."));
            }

            controller.LastReplyEpoch = Math.Max(controller.LastReplyEpoch, epoch);
        }

        /// <summary>
        /// OR-I5 (<see cref="ReplicaChoice"/>): the transition names <c>N</c> distinct nodes, its executor first, each of which
        /// may be given work at the registry value whose revision it carries and in the controller's membership view, or a
        /// degraded choice of a takeover or a leaving primary's hand-off, judged from the closed ledger's primary's standing;
        /// each degraded choice is counted, the step's witness.
        /// </summary>
        private void CheckPlacement(Controller controller, SentTransition sent)
        {
            var choice = sent.Transition.Choice;
            if (!_registries.TryGetValue(choice.ChosenAt, out var registry))
            {
                _violations.Add(Invariant($"OR-I5: controller {controller.Writer} sent {sent} at revision {choice.ChosenAt}, which no registry value had"));
                return;
            }

            // The membership view the pass chose from: a cut-off may change the controller's membership between its observation
            // and its pass.
            bool Shows<T>(NodeId node) => controller.Observed.TryGetValue(node, out var liveness) && liveness.Value is T;
            bool Pending(NodeId node) => _stopping is var (stopping, generation) && stopping == node && registry.Find(node)?.Generation == generation;
            var givenWork = registry.Nodes.Keys.Where(node => registry.Find(node) is { IsRetired: false } && Shows<NodeLiveness.Live>(node) && !Pending(node)).ToHashSet();
            var predecessor = sent.Transition.Value is Transition.Replace replace ? controller.View.Current.Get(replace.Ledger) : null;
            var standing = PredecessorPrimary.Serving;
            if (predecessor is { } closed)
            {
                var primary = closed.Primary.Node;
                standing = Shows<NodeLiveness.Dead>(primary) || controller.Reports.TryGetValue(primary, out var report) && report.Unserved.Contains(closed.Id)
                    ? PredecessorPrimary.Ended
                    : registry.Find(primary) is { IsRetired: true } || Shows<NodeLiveness.Departing>(primary) || Pending(primary)
                        ? PredecessorPrimary.Leaving
                        : PredecessorPrimary.Serving;
            }

            if (ReplicaChoice.Violation(
                sent,
                _options.Replicas,
                predecessor,
                standing,
                givenWork,
                node => registry.Find(node) is { IsRetired: false } && Shows<NodeLiveness.Dead>(node)) is { } violation)
            {
                _violations.Add(Invariant($"controller {controller.Writer}: {violation} (revision {choice.ChosenAt})"));
            }
            else if (!choice.Replicas.All(givenWork.Contains))
            {
                _degraded[sent.Purpose] = _degraded.GetValueOrDefault(sent.Purpose) + 1;
            }
        }

        /// <summary>
        /// Runs <paramref name="sent"/> on its primary's process and reports the reply back to the controller; an isolated
        /// primary fails the call, and a reply is now and then lost under faults.
        /// </summary>
        private async Task PrepareReleaseAsync(Controller controller, SentPrepareRelease sent, bool lossy = true)
        {
            PrepareReleaseReply? delivered = null;
            var process = _processes.Single(process => process.Node == sent.Node);
            if (!_isolated.Contains(process.Node))
            {
                try
                {
                    var answered = await process.Controller.PrepareReleaseAsync(sent.Request, cancellationToken);
                    delivered = lossy && _random.Chance(0.1) ? (PrepareReleaseReply?)null : answered;
                }
                catch (Exception failure) when (failure is OperationCanceledException or IOException or TimeoutException
                    || failure is AggregateException aggregate && QuorumFailures.IsAdmitted(aggregate, mediaDamage: false))
                {
                    // Unknown outcome; the controller falls back to non-cooperative takeover.
                }
            }

            controller.Decisions.PrepareReleaseCompleted(sent, delivered);
            Record($"controller {controller.Writer} {sent}: {delivered?.Value?.GetType().Name ?? "no reply"}");
        }

        /// <summary>
        /// Executes <paramref name="sent"/> in its executor's process, through the sending controller's committer, and
        /// reports the reply to the controller; an executor cut off from the cluster fails the call, and a reply is now and
        /// then lost.
        /// </summary>
        /// <returns>The executor's reply, before any loss.</returns>
        private async Task<TransitionReply?> ExecuteAsync(Controller controller, SentTransition sent, bool lossy = true)
        {
            TransitionReply? delivered = null;
            TransitionReply? answered = null;
            var sentAt = environment.TimeProvider.GetUtcNow();
            var process = _processes.Single(process => process.Node == sent.Executor);
            if (!_isolated.Contains(process.Node))
            {
                try
                {
                    answered = await process.Controller.ExecuteAsync(sent.Transition, (change, token) => CommitAsync(controller, change, token), cancellationToken);
                    if (answered?.Value is LedgerMapCommitOutcome.Rejected rejected)
                    {
                        _violations.Add(Invariant($"controller {controller.Writer}'s {sent} was rejected: {rejected.Reason}"));
                    }

                    var epoch = answered?.Value switch
                    {
                        LedgerMapCommitOutcome.Applied applied => applied.Epoch,
                        LedgerMapCommitOutcome.Superseded superseded => superseded.Epoch,
                        _ => 0ul,
                    };
                    if (epoch > (ulong)register.AppliedWrites.Count)
                    {
                        _violations.Add(Invariant($"OR-I19: {sent}'s executor answered epoch {epoch} when {register.AppliedWrites.Count} writes had landed."));
                    }

                    delivered = lossy && _random.Chance(0.1) ? null : answered;
                }
                catch (Exception failure) when (failure is OperationCanceledException or IOException or TimeoutException
                    || failure is AggregateException aggregate && QuorumFailures.IsAdmitted(aggregate, mediaDamage: false))
                {
                    // The call's outcome is unknown; the controller chooses again while the map does not show it.
                }
            }

            if (delivered is not null && environment.TimeProvider.GetUtcNow() - sentAt >= _options.TransitionTimeout)
            {
                _violations.Add(Invariant($"controller {controller.Writer}'s {sent} was answered {environment.TimeProvider.GetUtcNow() - sentAt} after its send, past TransitionTimeout, which may have expired it."));
            }

            controller.Decisions.TransitionCompleted(sent, delivered);
            var path = (sent.Purpose, answered?.Value?.GetType().Name ?? "no reply");
            _paths[path] = _paths.GetValueOrDefault(path) + 1;
            _answers.Add((sent, path.Item2));
            Record($"controller {controller.Writer} {sent}: {delivered?.Value?.GetType().Name ?? "no reply"}");
            return answered;
        }

        /// <summary>Submits a change through the controller's committer, recording its answer for the history oracle (OR-I19).</summary>
        private async Task<LedgerMapCommitOutcome> CommitAsync(Controller controller, LedgerMapChange change, CancellationToken token)
        {
            var submittedAt = (ulong)register.AppliedWrites.Count;
            try
            {
                var answer = await controller.Decisions.CommitLedgerMapAsync(change, token);
                var recorded = mutant == Mutant.SupersededReadAsApplied && answer.Value is LedgerMapCommitOutcome.Superseded superseded
                    ? new LedgerMapCommitOutcome.Applied(superseded.Epoch)
                    : answer;
                var landedAt = mutant == Mutant.AnsweredBeforeItsWriteLanded ? submittedAt : (ulong)register.AppliedWrites.Count;
                var submission = new LedgerMapSubmission(change, controller.Writer, submittedAt, recorded, AnsweredAt: register.Dispatches, LandedAt: landedAt);
                _submissions.Add(submission);
                Judge(submission);
                return answer;
            }
            catch (Exception failure) when (failure is IOException or TimeoutException)
            {
                _submissions.Add(new LedgerMapSubmission(change, controller.Writer, submittedAt, null, failure, register.Dispatches));
                throw;
            }
        }

        /// <summary>
        /// Cuts a node off: the first controller's membership marks it dead at once, the second's only later, a stale view;
        /// once reconnected, both mark it live.
        /// </summary>
        private async Task CutOffAsync(LedgerStoreNode node)
        {
            await DelayAsync(150);
            Isolate(node, _controllers[0]);
            await DelayAsync(120);
            _controllers[1].Membership[node.NodeId] = new NodeLiveness.Dead(environment.TimeProvider.GetUtcNow());
            await DelayAsync(150);
            Reconnect(node);
        }

        private void Isolate(LedgerStoreNode node, params Controller[] observers)
        {
            node.Isolate();
            _ = _isolated.Add(node.NodeId);
            foreach (var controller in observers)
            {
                controller.Membership[node.NodeId] = new NodeLiveness.Dead(environment.TimeProvider.GetUtcNow());
            }
        }

        private void Reconnect(LedgerStoreNode node)
        {
            node.Reconnect();
            _ = _isolated.Remove(node.NodeId);
            foreach (var controller in _controllers)
            {
                controller.Membership[node.NodeId] = NodeLiveness.Live.Instance;
            }
        }

        private void Heal()
        {
            environment.Network.MessageDropRate = 0;
            environment.Rpc.DuplicationRate = 0;
            register.UnknownOutcomeRate = 0;
            register.AppliedMismatchRate = 0;
            register.ReadFailureRate = 0;
        }

        /// <summary>
        /// Once faults stop, runs both controllers' passes until every open range is served by its primary's process and at
        /// least <paramref name="ranges"/> ranges are open; <paramref name="beforeAttempt"/> runs before each attempt's passes,
        /// with no execution in flight, and <paramref name="goal"/> names <paramref name="done"/> in the violation.
        /// </summary>
        private async Task ServeEveryRangeAsync(int ranges, Action<Controller>? beforePass = null, Func<LedgerMapValue, bool>? done = null, bool oneController = false, Func<LedgerMapValue, Task>? beforeAttempt = null, string? goal = null)
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await SimulationTasks.WhenAll(_executions);
                var map = await ReadEveryViewAsync();
                var open = map.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();
                if (open.Length >= ranges
                    && open.All(ledger => _processes.Single(process => process.Node == ledger.Primary.Node).Registry.TryGet(ledger.Id) is not null)
                    && (done?.Invoke(map) ?? true))
                {
                    return;
                }

                if (beforeAttempt is not null)
                {
                    await beforeAttempt(map);
                }

                foreach (var controller in oneController ? _controllers.Take(1) : _controllers)
                {
                    await PassAsync(controller, readView: true, beforePass);
                }

                await DelayAsync(40);
            }

            _violations.Add(Invariant($"Once faults stopped, the controllers never left {ranges} open ranges served{(done is null ? string.Empty : $" with {goal ?? "the step's goal"}")}."));
        }

        /// <summary>
        /// Duplicated controllers acting on one stale value, by construction, with every fault off: both controllers read
        /// the same map value, an open ledger's primary is cut off and both mark it dead, and each chooses from that value,
        /// before either executes, a takeover of the ledger from membership (orleans.md §5.7). The second controller's
        /// first send of its takeover is lost unexecuted, so it counts that executor, the ledger's first successor, as
        /// failed, and its next pass chooses the next replica: two identical choices would be one repeatable change, which
        /// both would answer applied. Both are executed at once, with no reply lost: one applies, and the other, whose
        /// ledger the first closed, is answered <see cref="LedgerMapCommitOutcome.Superseded"/>. Throughout, a manifest
        /// replace of the first controller is held at the store, and it answers only once the takeovers have: its turns
        /// await no write.
        /// </summary>
        private async Task RaceDuplicateTakeoversAsync()
        {
            var held = objects.HoldReplaces();
            var tag = new Tag(new LedgerId(1), Slot.Zero, 0);
            var section = new SectionRef(new ObjectName("d/1/0/held"), 0, 10, KeyRange.Root, tag, tag, new ObjectRange(10, 1));
            var replace = _controllers[0].Decisions.CommitManifestAsync(new ManifestChange.Drain(new LedgerId(1), Slot.Zero, new Slot(1), [section], complete: false), cancellationToken);
            await held;
            if (await PrepareRaceAsync() is not var (map, target, primary))
            {
                return;
            }

            List<(Controller Controller, SentTransition Sent)> takeovers = [];
            foreach (var controller in _controllers)
            {
                var takeover = PassForTakeoverOf(controller, target, map);
                if (takeover is not null && controller != _controllers[0])
                {
                    // Its send is lost unexecuted, so the controller counts the executor as failed for the ledger, and its
                    // next pass, from the same value, takes the ledger over through the next replica.
                    controller.Decisions.TransitionCompleted(takeover, reply: null);
                    takeover = PassForTakeoverOf(controller, target, map);
                }

                if (takeover is not null)
                {
                    takeovers.Add((controller, takeover));
                }
            }

            await VerifyRaceAsync(map, target, takeovers);
            await SimulationTasks.WhenAll(_executions);
            if (replace.IsCompleted)
            {
                _violations.Add(Invariant($"The manifest replace held at the store answered before the takeovers of s{target.Id} were decided."));
            }

            objects.ReleaseReplaces();
            _ = await replace;
            Reconnect(primary);
            Record($"duplicate takeovers from one stale value while a manifest replace was held");
        }

        /// <summary>Whether at least three nodes are primaries of <paramref name="map"/>'s open ledgers.</summary>
        private static bool Spread(LedgerMapValue map) => map.Ledgers.Where(static ledger => ledger.IsOpen).Select(static ledger => ledger.Primary.Node).Distinct().Count() >= 3;

        /// <summary>
        /// Every node leads one open ledger and holds the same share of replicas, so the controllers have no pending
        /// primary hand-off or replica-balance roll to race the lost-replica repair step.
        /// </summary>
        private static bool Balanced(LedgerMapValue map)
        {
            LedgerMetadata[] open = [.. map.Ledgers.Where(static ledger => ledger.IsOpen)];
            return open.Length == NodeCount
                && open.Select(static ledger => ledger.Primary.Node).Distinct().Count() == NodeCount
                && open.All(static ledger => ledger.Replicas.Length == 3)
                && open.SelectMany(static ledger => ledger.Replicas).GroupBy(static node => node).All(static group => group.Count() == 3);
        }

        /// <summary>The first open ledger of the primary of the fewest open ledgers in <paramref name="map"/>.</summary>
        private static LedgerMetadata LeastLoaded(LedgerMapValue map)
        {
            var open = map.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();
            return open.OrderBy(ledger => open.Count(other => other.Primary.Node == ledger.Primary.Node)).First();
        }

        /// <summary>Brings both controllers' views to one value and cuts off the primary of its first open ledger, which both mark dead.</summary>
        private async Task<(LedgerMapValue Map, LedgerMetadata Target, LedgerStoreNode Primary)?> PrepareRaceAsync()
        {
            var map = await ReadEveryViewAsync();
            foreach (var controller in _controllers)
            {
                if ((await RetryAsync(() => controller.View.ReadAsync(cancellationToken))).Epoch != map.Epoch)
                {
                    _violations.Add(Invariant($"Controller {controller.Writer}'s view did not reach epoch {map.Epoch} before the duplicate takeovers."));
                    return null;
                }
            }

            var target = LeastLoaded(map);
            var primary = nodes.Single(node => node.NodeId == target.Primary.Node);
            Isolate(primary, [.. _controllers]);
            return (map, target, primary);
        }

        /// <summary>
        /// Runs a pass of <paramref name="controller"/> and returns its takeover of <paramref name="target"/> from
        /// <paramref name="map"/>, executing every other transition it sends.
        /// </summary>
        private SentTransition? PassForTakeoverOf(Controller controller, LedgerMetadata target, LedgerMapValue map)
        {
            Observe(controller);
            SentTransition? takeover = null;
            var pass = controller.Decisions.RunPass();
            foreach (var prepare in pass.PrepareReleases)
            {
                _executions.Add(PrepareReleaseAsync(controller, prepare));
            }

            foreach (var sent in pass.Transitions)
            {
                CheckPlacement(controller, sent);
                if (sent is { Purpose: TransitionPurpose.Takeover, Transition.Value: Transition.Replace replace } && replace.Ledger == target.Id && replace.Choice.Epoch == map.Epoch)
                {
                    takeover = sent;
                }
                else
                {
                    _executions.Add(ExecuteAsync(controller, sent));
                }
            }

            return takeover;
        }

        /// <summary>Executes both takeovers at once, with no reply lost, and records a violation unless one applies and the other is superseded.</summary>
        private async Task VerifyRaceAsync(LedgerMapValue map, LedgerMetadata target, List<(Controller Controller, SentTransition Sent)> takeovers)
        {
            if (takeovers.Count != 2)
            {
                _violations.Add(Invariant($"From epoch {map.Epoch} with s{target.Id}'s primary dead, the controllers sent {takeovers.Count} takeovers of it, not one each."));
                return;
            }

            var replies = await SimulationTasks.WhenAll(takeovers.Select(takeover => ExecuteAsync(takeover.Controller, takeover.Sent, lossy: false)));
            var applied = replies.Count(static reply => reply?.Value is LedgerMapCommitOutcome.Applied);
            var superseded = replies.Count(static reply => reply?.Value is LedgerMapCommitOutcome.Superseded);
            var after = await RetryAsync(() => _controllers[0].View.ReadAsync(cancellationToken));
            if (takeovers[0].Sent.Executor == takeovers[1].Sent.Executor)
            {
                _violations.Add(Invariant($"Both controllers chose {takeovers[0].Sent.Executor} to take over s{target.Id}, though the second counts it failed."));
            }

            if (applied != 1 || superseded != 1 || after.Get(target.Id) is { IsOpen: true })
            {
                _violations.Add(Invariant($"The duplicate takeovers of s{target.Id} answered {string.Join(", ", replies.Select(static reply => reply?.Value?.GetType().Name ?? "no reply"))}, not one Applied and one Superseded, leaving it open: {after.Get(target.Id) is { IsOpen: true }}."));
            }
        }

        /// <summary>
        /// A stopping node's hand-off, by construction: the primary of the fewest open ledgers asks to hand off, as its silo
        /// does, before every pass of both controllers, until it is primary of none. While its request stands it is given no
        /// ledger and is no replica of a new one, so no hand-off cycle occurs; then it restarts, registering a newer
        /// generation, which ends its request. It stops before the retirement: with N = 3 of four nodes, a retired node and
        /// a stopping one would leave too few nodes to place either's ledgers.
        /// </summary>
        private async Task HandOffAStoppingNodeAsync()
        {
            var map = await ReadEveryViewAsync();
            var stopping = map.Ledgers.Where(static ledger => ledger.IsOpen).GroupBy(static ledger => ledger.Primary.Node)
                .OrderBy(static primary => primary.Count()).ThenBy(static primary => primary.Key.ToString(), StringComparer.Ordinal).First().Key;
            var asked = _answers.Count;
            var generation = _registry.Nodes[stopping].Generation;
            _stopping = (stopping, generation);
            await ServeEveryRangeAsync(
                ranges: NodeCount - 1,
                beforePass: controller => controller.Decisions.RequestHandOff(stopping, generation),
                done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == stopping));

            // A stopping node is given no ledger once it asked, even when it is primary of none: no hand-off cycle.
            if (_answers.Skip(asked).FirstOrDefault(answer => answer.Sent.Transition.Choice.Replicas.Contains(stopping)).Sent is { } cycle)
            {
                _violations.Add(Invariant($"{cycle} placed {stopping}, which asked to hand off."));
            }

            if (!_answers.Skip(asked).Any(answer => answer.Sent.Purpose == TransitionPurpose.Departure && answer.Reply == nameof(LedgerMapCommitOutcome.Applied)))
            {
                _violations.Add(Invariant($"No Departure of {stopping}'s ledgers applied while it stopped."));
            }

            Publish(_registry.Nodes.ToFrozenDictionary(static entry => entry.Key, entry => entry.Key == stopping ? entry.Value with { Generation = generation + 1 } : entry.Value));
            foreach (var controller in _controllers)
            {
                controller.Registry = _registry;
            }

            Record($"node {stopping} handed off and restarted");
        }

        /// <summary>
        /// Degraded takeovers and departures, by construction (orleans.md §5.6, OR-I5), with every fault off: a node stops,
        /// asking to hand off before every pass, and the primary of an open ledger is cut off, which both controllers mark
        /// dead, so two of the four nodes may be given work, fewer than <c>N</c> and a quorum. A WAL-release roll is first
        /// requested of each open ledger which names the cut-off node under another primary, which may not degrade and so
        /// waits. The controllers take the cut-off node's ledgers over, and hand off the stopping node's which name it, onto
        /// successors which keep the cut-off replica, until the cut-off node is primary of no open ledger, and the step asserts
        /// that a degraded takeover
        /// was sent, the oracle's witness; a stopping node's ledger which names no dead replica departs once the cut-off node
        /// reconnects, its replicas of the successors holes from their first slot, which later steps' seals and recoveries
        /// reach. Then the stopping node restarts at a newer generation, which ends its request.
        /// </summary>
        private async Task TakeOverDegradedAsync()
        {
            var map = await ReadEveryViewAsync();
            var open = map.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();
            var ids = nodes.Select(static node => node.NodeId).OrderBy(static node => node.ToString(), StringComparer.Ordinal).ToArray();
            var pair = ids.Where(cut => open.Any(ledger => ledger.Primary.Node == cut))
                .SelectMany(cut => ids.Where(stop => stop != cut).Select(stop => (Cut: cut, Stop: stop)))
                .FirstOrDefault(pair => open.Any(ledger => ledger.Primary.Node != pair.Cut && ledger.Primary.Node != pair.Stop && Names(ledger, pair.Cut)));
            if (pair == default)
            {
                _violations.Add(Invariant($"No open ledger at epoch {map.Epoch} names a primary under a third node, for the degraded step."));
                return;
            }

            var (cut, stopping) = pair;
            var generation = _registry.Nodes[stopping].Generation;
            _stopping = (stopping, generation);
            foreach (var ledger in open.Where(ledger => ledger.Primary.Node != cut && ledger.Primary.Node != stopping && Names(ledger, cut)))
            {
                foreach (var controller in _controllers)
                {
                    controller.Decisions.RequestRoll(ledger.Id);
                }
            }

            var witnessed = _degraded.GetValueOrDefault(TransitionPurpose.Takeover);
            var primary = nodes.Single(node => node.NodeId == cut);
            Isolate(primary, [.. _controllers]);
            await ServeEveryRangeAsync(
                ranges: 1,
                beforePass: controller => controller.Decisions.RequestHandOff(stopping, generation),
                done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == cut),
                goal: Invariant($"no open ledger under {cut}, cut off while {stopping} stopped"));
            if (_degraded.GetValueOrDefault(TransitionPurpose.Takeover) == witnessed)
            {
                _violations.Add(Invariant($"With {cut} cut off and {stopping} stopping, the controllers sent no degraded takeover."));
            }

            // A stopping node's ledger which does not name the cut-off node has no dead replica to keep, so it departs once the
            // node reconnects.
            Reconnect(primary);
            await ServeEveryRangeAsync(
                ranges: 1,
                beforePass: controller => controller.Decisions.RequestHandOff(stopping, generation),
                done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == stopping),
                goal: Invariant($"no open ledger under {stopping}, stopping"));
            Publish(_registry.Nodes.ToFrozenDictionary(static entry => entry.Key, entry => entry.Key == stopping ? entry.Value with { Generation = generation + 1 } : entry.Value));
            foreach (var controller in _controllers)
            {
                controller.Registry = _registry;
            }

            Record($"degraded takeovers of {cut}'s ledgers while {stopping} stopped: {string.Join(", ", _degraded.Select(static entry => $"{entry.Key} x{entry.Value}"))}");
        }

        /// <summary>
        /// A retirement, by construction, of the open ledgers' primary which leaves the fewest ledgers, at least one, naming
        /// it as a replica under another primary: the registry retires it, and the second controller's registry view takes
        /// the retirement only after a few passes, a stale registry. First the first controller alone sends the hand-offs and
        /// repairs, and its hand-off of the retired primary's ledger is lost unexecuted with no reply, so only
        /// <c>TransitionTimeout</c> ends it and marks its executor failed, and it hands the ledger off again through another.
        /// Then both controllers hand off the retired primary's ledgers (OR-I3) and repair the ledgers naming the retired node
        /// as a replica, until no open ledger names it; each transition is judged against the registry value at its
        /// <c>ChosenAt</c>, and the step asserts each of its paths.
        /// </summary>
        private async Task RetireANodeAsync()
        {
            // Primary balance spreads the primaries until another one holds a ledger which names the node to retire. The blind
            // controller's mutant needs no repair: any primary retires.
            var scripted = mutant != Mutant.RetirementIgnored;
            if (scripted)
            {
                await HandOffAStoppingNodeAsync();
                await ServeEveryRangeAsync(ranges: NodeCount, done: value => RetirementOf(value) is not null);
            }

            var map = await ReadEveryViewAsync();
            if ((mutant == Mutant.RetirementIgnored ? (map.Ledgers.First(static ledger => ledger.IsOpen).Primary.Node, 0) : RetirementOf(map)) is not var (retired, repairs))
            {
                _violations.Add(Invariant($"No primary at epoch {map.Epoch} leaves a ledger to repair when it retires."));
                return;
            }

            var open = map.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();
            var answered = _answers.Count;
            var retiredAt = _registry.Revision + 1;
            _holdFloorRaises = mutant != Mutant.RetirementIgnored;
            Publish(_registry.Nodes.ToFrozenDictionary(static entry => entry.Key, entry => entry.Key == retired ? entry.Value with { RetiredRevision = retiredAt } : entry.Value));
            _controllers[0].Registry = _registry;
            if (scripted)
            {
                // The blind controller would send nothing here; its oracle's paths are the hand-offs below.
                await ForceATimeoutAsync(open.First(ledger => ledger.Primary.Node == retired).Id);
            }

            var stalePasses = 0;
            await ServeEveryRangeAsync(
                ranges: NodeCount - 1,
                beforePass: controller =>
                {
                    if (controller.Writer == 1 && ++stalePasses > 2)
                    {
                        controller.Registry = _registry;
                    }
                },
                done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Replicas.Contains(retired)));
            _controllers[1].Registry = _registry;

            foreach (var (purpose, of, closes) in new (TransitionPurpose, string, Func<LedgerMetadata, bool>)[]
            {
                (TransitionPurpose.Handoff, "the retired primary's ledger", ledger => ledger.Primary.Node == retired),
                (TransitionPurpose.Repair, "a ledger naming the retired replica", ledger => ledger.Primary.Node != retired && ledger.Replicas.Contains(retired)),
            })
            {
                if (!_answers.Skip(answered).Any(answer => answer.Sent.Purpose == purpose && answer.Reply == nameof(LedgerMapCommitOutcome.Applied)
                    && answer.Sent.Transition.Closes.Any(ledger => Ledger(ledger) is { } metadata && closes(metadata))))
                {
                    _violations.Add(Invariant($"No {purpose} of {of} applied when {retired} retired."));
                }
            }

            if (scripted)
            {
                await RefuseARetiredExecutorAsync(retired, await ReadEveryViewAsync());
            }

            Record($"node {retired} retired, {repairs} ledger(s) repaired");
            if (mutant != Mutant.RetirementIgnored)
            {
                await OpenTheGateBeforeAPausedDuplicateAsync(retired, retiredAt);
            }
        }

        /// <summary>
        /// OR-I4 by construction, against the production gate: the closed ledgers naming the retired node drop, as their
        /// drains would complete them, while the floor raises are held, so the gate the first controller answers is closed
        /// by its floor term alone; then the raises go on, and the first time the gate answers the node releasable, the
        /// duplicate controller, paused since before the retirement, so its registry view holds the node active at the
        /// revision before it, sends a roll of an open ledger which places the node, chosen at that revision: its rule holds
        /// against the map but for the floor, which supersedes it, and the gate checker holds after every later step. Under
        /// the floor-blind gate the gate opens before the raise, the roll applies, and the checker reports it.
        /// </summary>
        private async Task OpenTheGateBeforeAPausedDuplicateAsync(NodeId retired, ulong retiredAt)
        {
            for (var drop = 0; drop < 60; drop++)
            {
                var current = await ReadEveryViewAsync();
                var droppable = current.Ledgers
                    .Where(ledger => !ledger.IsOpen && LedgerMapRules.Decide(current, new LedgerMapChange.Drop(ledger.Id), int.MaxValue).Value is LedgerMapFoldVerdict.Applies)
                    .Select(static ledger => (LedgerId?)ledger.Id)
                    .FirstOrDefault();
                if (droppable is not { } closed || !current.Ledgers.Any(ledger => !ledger.IsOpen && Names(ledger, retired)))
                {
                    break;
                }

                _ = await RetryAsync(() => CommitAsync(_controllers[0], new LedgerMapChange.Drop(closed), cancellationToken));
            }

            _ = await RetryAsync(() => _controllers[0].View.ReadAsync(cancellationToken));
            Observe(_controllers[0]);
            AskTheGate();
            if (mutant != Mutant.GateIgnoresTheFloor && NodeStanding.Of(retired, _registry, _controllers[0].View.Current).Value is not NodeStanding.Retired { FloorCovers: false, Ledgers.IsEmpty: true })
            {
                _violations.Add(Invariant($"Once the ledgers naming {retired} dropped, only the floor below its retirement at {retiredAt} was to close its gate, so the step would not test the floor."));
                return;
            }

            // The raises go on only to open a gate still closed: the floor-blind gate opened at the drops, so the duplicate's
            // roll meets the floor below the retirement, as it would were it to land before the next raise.
            if (!_gateOpened.ContainsKey(retired))
            {
                _holdFloorRaises = false;
                await ServeEveryRangeAsync(ranges: NodeCount - 1, done: _ => _gateOpened.ContainsKey(retired), oneController: true);
            }

            if (!_gateOpened.ContainsKey(retired))
            {
                _violations.Add(Invariant($"The gate of {retired}, retired at {retiredAt}, did not open."));
                return;
            }

            var map = await ReadEveryViewAsync();
            var target = map.Ledgers.First(static ledger => ledger.IsOpen);
            var executor = target.Primary.Node;
            var choice = new TransitionChoice(executor, [executor, target.Replicas.First(node => node != executor), retired], _options.SequencerWindow, retiredAt - 1, map.Epoch);
            var install = new LedgerMapChange.Install([new LedgerClosing(target.Id, Slot.Zero)], [new LedgerOpening(target.Range, choice.Replicas, choice.K, new PrimaryClaim(executor, SequencerInstanceId.New()))], choice.ChosenAt);
            if (LedgerMapRules.Decide(new LedgerMapValue(map.Epoch, map.NextLedgerId, 0, map.Ledgers), install, int.MaxValue).Value is not LedgerMapFoldVerdict.Applies)
            {
                _violations.Add(Invariant($"The paused duplicate's roll of s{target.Id} would not apply even below the floor, so the floor would decide nothing."));
                return;
            }

            var reply = await ExecuteAsync(_controllers[1], new SentTransition(new Transition.Replace(target.Id, choice), TransitionPurpose.Roll), lossy: false);
            if (mutant != Mutant.GateIgnoresTheFloor && reply?.Value is not LedgerMapCommitOutcome.Superseded)
            {
                _violations.Add(Invariant($"OR-I4: the paused duplicate's roll of s{target.Id}, chosen at {choice.ChosenAt} below the retirement at {retiredAt}, answered {reply?.Value?.GetType().Name ?? "no reply"}, not Superseded."));
            }

            Record($"the paused duplicate's roll placing {retired}, chosen at {choice.ChosenAt}, answered {reply?.Value?.GetType().Name ?? "no reply"}");
            if (mutant != Mutant.GateIgnoresTheFloor)
            {
                await PauseADuplicateBelowTheFloorAsync(retiredAt);
            }
        }

        /// <summary>
        /// LG-I11 by construction: a new activation of the duplicate controller, whose registry read returned the value
        /// before the retirement, below the map's floor, with a takeover pending, as the open ledger's primary reports it
        /// unserved, sends no transition and asks for no further pass, however many passes run, since the commit rules
        /// would refuse every one; once its view reaches the floor, it sends the takeover, which ends unsent.
        /// </summary>
        private async Task PauseADuplicateBelowTheFloorAsync(ulong retiredAt)
        {
            var view = await RetryAsync(() => LedgerMapView.OpenAsync(register, cancellationToken));
            await using var duplicate = new Controller(1, view, register.Writer(1), objects, _options) { Registry = _registries[retiredAt - 1] };
            duplicate.Decisions.RegistryBehindIgnoredMutant = mutant == Mutant.RegistryBehindIgnored;
            foreach (var node in nodes)
            {
                duplicate.Membership[node.NodeId] = NodeLiveness.Live.Instance;
            }

            var ledger = view.Current.Ledgers.First(static ledger => ledger.IsOpen);
            Observe(duplicate);
            _ = duplicate.Decisions.Report(new NodeReport(ledger.Primary.Node, _registry.Find(ledger.Primary.Node)!.Generation, Sequence: 1, [ledger.Id], [], DiskPressure: false, [], [], UndrainedKeys: 0, ArchiveLogRequestsPerSecond: null, HandingOff: false, ArchiveGatePassed: false, Draining: false));
            for (var pass = 0; pass < 3; pass++)
            {
                var behind = duplicate.Decisions.RunPass();
                if (!behind.RegistryBehind || !behind.Transitions.IsEmpty || duplicate.Decisions.PassDue)
                {
                    _violations.Add(Invariant($"LG-I11: the duplicate below the floor sent {behind.Transitions.Length} transition(s), registry behind: {behind.RegistryBehind}, a pass due: {duplicate.Decisions.PassDue}."));
                }
            }

            duplicate.Registry = _registry;
            Observe(duplicate);
            var caughtUp = duplicate.Decisions.RunPass();
            if (!caughtUp.Transitions.Any(sent => sent.Purpose == TransitionPurpose.Takeover && sent.Transition.Closes.Contains(ledger.Id)))
            {
                _violations.Add(Invariant($"The duplicate whose view reached the floor sent no takeover of s{ledger.Id}, which its primary reported unserved."));
            }

            foreach (var sent in caughtUp.Transitions)
            {
                _ = duplicate.Decisions.TransitionCompleted(sent, reply: null);
            }

            Record($"the duplicate below the floor sent nothing until its view reached it");
        }

        /// <summary>
        /// The membership change of enroute-sh/enroute#7, in <paramref name="change"/>'s order: node A, a primary and a
        /// replica of an open ledger under node B, dies with its silo, and the controllers' passes take its ledgers over; B
        /// is retired, and while A is dead the passes hand B's ledgers which name A off onto degraded successors which keep
        /// A (OR-I5), while B, alive, serves its others until <c>N</c> nodes may be given work; A
        /// returns over its disk at a newer generation, or the spare replaces it at its ordinal, retiring it. Then the
        /// passes serve every range on <c>N</c> live, non-retired replicas, open each retired node's gate, and fall quiet;
        /// each open ledger takes a write which every replica holds.
        /// </summary>
        private async Task ChangeMembershipAsync(MembershipChange change)
        {
            var map = await ReadEveryViewAsync();
            var open = map.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();
            var primaries = open.Select(static ledger => ledger.Primary.Node).Distinct().OrderBy(static node => node.ToString(), StringComparer.Ordinal).ToArray();
            var pair = primaries.SelectMany(dead => primaries.Where(retired => retired != dead).Select(retired => (Dead: dead, Retired: retired)))
                .FirstOrDefault(pair => open.Any(ledger => ledger.Primary.Node == pair.Retired && ledger.Replicas.Contains(pair.Dead)));
            if (pair == default)
            {
                _violations.Add(Invariant($"No open ledger at epoch {map.Epoch} names another primary as a replica, for the membership change."));
                return;
            }

            var (dead, retired) = pair;
            var spare = nodes[NodeCount];
            var started = environment.TimeProvider.GetUtcNow();
            var answered = _answers.Count;
            List<NodeId> retirements = [retired];
            var down = nodes.Single(node => node.NodeId == dead);
            var declared = change != MembershipChange.ReplaceBeforeDeath;
            Isolate(down, declared ? [.. _controllers] : []);
            await down.CrashAsync(operatingSystem: false);
            Record($"node {dead} died with its silo{(declared ? string.Empty : ", membership still showing it live")}");
            // A retirement while the node is dead follows its death at once, before any pass: otherwise the passes, with N
            // nodes still workable, would take its ledgers over and roll its dead replica out first.
            if (declared && change is not (MembershipChange.RetireThenReturn or MembershipChange.RetireThenReplace))
            {
                await ServeEveryRangeAsync(ranges: 1, done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == dead), goal: Invariant($"no open ledger under {dead}, dead"));
            }

            switch (change)
            {
                case MembershipChange.RetireThenReturn:
                    await RetireWhileDeadAsync();
                    await ReturnAsync(down);
                    break;
                case MembershipChange.ReturnThenRetire:
                    await ReturnAsync(down);
                    Retire(retired);
                    break;
                case MembershipChange.RetireThenReplace:
                    await RetireWhileDeadAsync();
                    Replace(dead, spare.NodeId);
                    retirements.Add(dead);
                    break;
                case MembershipChange.ReplaceThenRetire:
                    Replace(dead, spare.NodeId);
                    retirements.Add(dead);
                    Retire(retired);
                    break;
                case MembershipChange.ReplaceBeforeDeath:
                    Replace(dead, spare.NodeId);
                    retirements.Add(dead);
                    await ServeEveryRangeAsync(ranges: 1, done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == dead), goal: Invariant($"no open ledger under {dead}, replaced while live in membership"));
                    foreach (var controller in _controllers)
                    {
                        controller.Membership[dead] = new NodeLiveness.Dead(environment.TimeProvider.GetUtcNow());
                    }

                    Retire(retired);
                    break;
                default:
                    throw new UnreachableException();
            }

            await ServeEveryRangeAsync(ranges: 1, done: Healthy, goal: Invariant($"every open ledger on {_options.Replicas} live, non-retired replicas after {change}"));
            Record($"after {change}, every open ledger served on live replicas in {environment.TimeProvider.GetUtcNow() - started}");
            await OpenGatesAsync(retirements);
            await QuiesceAsync();
            await AssertWritesReadBackAsync();
            foreach (var range in _answers.Skip(answered)
                .Where(static answer => answer.Sent.Purpose == TransitionPurpose.Takeover && answer.Reply == nameof(LedgerMapCommitOutcome.Applied))
                .SelectMany(answer => answer.Sent.Transition.Closes.Select(Ledger))
                .GroupBy(static ledger => ledger?.Range)
                .Where(static range => range.Count() > 2))
            {
                _violations.Add(Invariant($"Range {range.Key} was taken over {range.Count()} times after {change}."));
            }

            // With the first node dead and the second retired, fewer than N nodes may be given work, the state Enroute could not
            // leave: the first's ledgers are taken over, and the second's which name the first handed off, onto degraded
            // successors which keep the dead replica; the second's others, with no dead replica to keep, wait for N while it
            // serves them, and the repairs wait too.
            async Task RetireWhileDeadAsync()
            {
                var witnessed = _degraded.GetValueOrDefault(TransitionPurpose.Takeover) + _degraded.GetValueOrDefault(TransitionPurpose.Handoff);
                Retire(retired);
                await ServeEveryRangeAsync(
                    ranges: 1,
                    done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && (ledger.Primary.Node == dead || ledger.Primary.Node == retired && ledger.Replicas.Contains(dead))),
                    goal: Invariant($"no open ledger under {dead}, nor under {retired} naming it, {retired} retired while {dead} is dead"));
                if (_degraded.GetValueOrDefault(TransitionPurpose.Takeover) + _degraded.GetValueOrDefault(TransitionPurpose.Handoff) == witnessed)
                {
                    _violations.Add(Invariant($"With {dead} dead and {retired} retired, no degraded takeover or hand-off was sent."));
                }
            }

            bool Healthy(LedgerMapValue value)
            {
                bool Live(NodeId node) => _registry.Find(node) is { IsRetired: false } && !_isolated.Contains(node);
                return value.Ledgers.Where(static ledger => ledger.IsOpen).All(ledger => Live(ledger.Primary.Node) && ledger.Replicas.Length == _options.Replicas && ledger.Replicas.All(Live));
            }
        }

        /// <summary>Retires <paramref name="node"/> in the registry, which both controllers' views take at once.</summary>
        private void Retire(NodeId node)
        {
            Publish(_registry.Nodes.ToFrozenDictionary(static entry => entry.Key, entry => entry.Key == node ? entry.Value with { RetiredRevision = _registry.Revision + 1 } : entry.Value));
            UpdateRegistries();
            Record($"node {node} retired");
        }

        /// <summary>
        /// Registers <paramref name="replacement"/> at the ordinal of <paramref name="replaced"/>, in its failure domain, which
        /// the registration retires, as a silo configured with <c>NodeToReplace</c> registers (orleans.md §5.3).
        /// </summary>
        private void Replace(NodeId replaced, NodeId replacement)
        {
            var retiredAt = _registry.Revision + 1;
            var entries = _registry.Nodes.ToDictionary(static entry => entry.Key, static entry => entry.Value);
            entries[replacement] = new RegisteredNode(RetiredRevision: null, entries[replaced].FailureDomain, Generation: 1, Ordinal: null);
            entries[replaced] = entries[replaced] with { RetiredRevision = retiredAt };
            Publish(entries.ToFrozenDictionary());
            UpdateRegistries();
            Record($"node {replacement} replaced {replaced}");
        }

        /// <summary>
        /// A dead node returns over its disk: its store recovers, its silo starts a new process at a newer generation, which
        /// holds none of its earlier lifetimes, and membership shows it live again.
        /// </summary>
        private async Task ReturnAsync(LedgerStoreNode node)
        {
            var id = node.NodeId;
            var earlier = _processes.Single(process => process.Node == id);
            _ = _processes.Remove(earlier);
            await earlier.DisposeAsync();
            await node.StartAsync(cancellationToken);
            Reconnect(node);
            var view = await RetryAsync(() => LedgerMapView.OpenAsync(register, cancellationToken));
            _processes.Add(new Process(id, view, new ProcessTransport(replicas, node.Address), () => _registry.Find(id) is { IsRetired: false } || _activeOnce.Remove(id)));
            Publish(_registry.Nodes.ToFrozenDictionary(static entry => entry.Key, entry => entry.Key == id ? entry.Value with { Generation = entry.Value.Generation + 1 } : entry.Value));
            UpdateRegistries();
            Record($"node {id} returned over its disk");
        }

        private void UpdateRegistries()
        {
            foreach (var controller in _controllers)
            {
                controller.Registry = _registry;
            }
        }

        /// <summary>
        /// Opens the gates of <paramref name="retired"/>: drops every closed ledger the map allows, as their drains would
        /// complete them, until none names a retired node, and runs the first controller's passes, which raise the map's
        /// floor, until its gate answers each node releasable (OR-I4).
        /// </summary>
        private async Task OpenGatesAsync(IReadOnlyList<NodeId> retired)
        {
            for (var drop = 0; drop < 60; drop++)
            {
                var current = await ReadEveryViewAsync();
                var droppable = current.Ledgers
                    .Where(ledger => !ledger.IsOpen && LedgerMapRules.Decide(current, new LedgerMapChange.Drop(ledger.Id), int.MaxValue).Value is LedgerMapFoldVerdict.Applies)
                    .Select(static ledger => (LedgerId?)ledger.Id)
                    .FirstOrDefault();
                if (droppable is not { } closed || !current.Ledgers.Any(ledger => !ledger.IsOpen && retired.Any(node => Names(ledger, node))))
                {
                    break;
                }

                _ = await RetryAsync(() => CommitAsync(_controllers[0], new LedgerMapChange.Drop(closed), cancellationToken));
            }

            await ServeEveryRangeAsync(ranges: 1, done: _ => retired.All(_gateOpened.ContainsKey), oneController: true, goal: Invariant($"the gates of {string.Join(", ", retired)} open"));
        }

        /// <summary>Runs both controllers' passes until three in a row send nothing, within a bound: no transition repeats forever.</summary>
        private async Task QuiesceAsync()
        {
            var quiet = 0;
            for (var pass = 0; pass < 30 && quiet < 3; pass++)
            {
                var sent = _sent;
                foreach (var controller in _controllers)
                {
                    await PassAsync(controller, readView: true);
                }

                await SimulationTasks.WhenAll(_executions);
                quiet = _sent == sent ? quiet + 1 : 0;
                await DelayAsync(40);
            }

            if (quiet < 3)
            {
                _violations.Add(Invariant($"The controllers still sent transitions after 30 passes: {string.Join(", ", _answers.TakeLast(6).Select(static answer => $"{answer.Sent} {answer.Reply}"))}."));
            }
        }

        /// <summary>Proposes a row to each open ledger and asserts that each of its replicas holds it within a bounded wait.</summary>
        private async Task AssertWritesReadBackAsync()
        {
            foreach (var ledger in (await ReadEveryViewAsync()).Ledgers.Where(static ledger => ledger.IsOpen))
            {
                if (await ProposeAsync(ledger) is not { } slot)
                {
                    continue;
                }

                foreach (var replica in ledger.Replicas)
                {
                    var node = nodes.Single(node => node.NodeId == replica);
                    var held = false;
                    for (var attempt = 0; attempt < 60 && !held; attempt++)
                    {
                        held = await HoldsAsync(node, ledger, slot);
                        if (!held)
                        {
                            await DelayAsync(5);
                        }
                    }

                    if (!held)
                    {
                        _violations.Add(Invariant($"{replica}'s replica of s{ledger.Id} never held the write at {slot}."));
                    }
                }
            }
        }

        private static bool Names(LedgerMetadata ledger, NodeId node) => ledger.Primary.Node == node || ledger.Replicas.Contains(node);

        /// <summary>
        /// Chooses, from <paramref name="map"/>'s open ledgers' primaries, the node to retire which leaves the fewest ledgers,
        /// at least one, naming it as a replica under another primary, to repair.
        /// </summary>
        private static (NodeId Retired, int Repairs)? RetirementOf(LedgerMapValue map)
        {
            var open = map.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();
            return open.Select(static ledger => ledger.Primary.Node).Distinct().OrderBy(static node => node.ToString(), StringComparer.Ordinal)
                .Select(retired => (Retired: retired, Repairs: open.Count(ledger => ledger.Primary.Node != retired && ledger.Replicas.Contains(retired))))
                .Where(static shape => shape.Repairs > 0)
                .OrderBy(static shape => shape.Repairs)
                .Select(static shape => ((NodeId, int)?)shape)
                .FirstOrDefault();
        }

        /// <summary>
        /// OR-I3 by construction: a transition whose executor is retired while it recovers, its first check of its own entry
        /// passing as it would before the retirement landed, answers <c>Refused</c> and submits nothing. It is chosen at the
        /// retirement's revision, at the map's floor, so that only the executor's own check refuses it: one chosen below the
        /// floor its executor answers <c>Superseded</c> from its view. Its recovery seals the ledger, whose primary then
        /// reports it unserved and takes it over.
        /// </summary>
        private async Task RefuseARetiredExecutorAsync(NodeId retired, LedgerMapValue map)
        {
            var ledger = map.Ledgers.First(static ledger => ledger.IsOpen);
            var choice = new TransitionChoice(retired, [retired, .. ledger.Replicas.Where(node => node != retired).Take(_options.Replicas - 1)], _options.SequencerWindow, _registry.Revision, map.Epoch);
            _ = _activeOnce.Add(retired);
            var reply = await ExecuteAsync(_controllers[0], new SentTransition(new Transition.Replace(ledger.Id, choice), TransitionPurpose.Handoff), lossy: false);
            if (reply?.Value is not LedgerMapCommitOutcome.Refused)
            {
                _violations.Add(Invariant($"OR-I3: retired executor {retired} answered {reply?.Value?.GetType().Name ?? "no reply"} for s{ledger.Id}, not Refused."));
            }
        }

        /// <summary>
        /// The first controller alone passes until its hand-off of <paramref name="ledger"/>, lost unexecuted with no reply,
        /// has expired by <c>TransitionTimeout</c> and it has sent the ledger's next transition, which must name another
        /// executor: the timeout marked the first one failed. One balancing transition at a time, so the lost one holds the
        /// budget while it waits and no other balancing runs meanwhile; repairs keep their own budget.
        /// </summary>
        private async Task ForceATimeoutAsync(LedgerId ledger)
        {
            var timeout = _timeout = new ForcedTimeout(ledger);
            _options.MaxConcurrentTransitions = 1;
            for (var pass = 0; pass < 30 && timeout.Resent is null; pass++)
            {
                await PassAsync(_controllers[0], readView: true);
                await SimulationTasks.WhenAll(_executions);

                // Once the lost send is out, the controller only waits for its timeout: no pass is due before it.
                var wait = timeout.Withheld is null ? _random.NextTimeSpan(TimeSpan.FromMilliseconds(40)) : timeout.WithheldAt + _options.TransitionTimeout - environment.TimeProvider.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, environment.TimeProvider, cancellationToken);
                }
            }

            _options.MaxConcurrentTransitions = 2;
            _timeout = null;
            if (timeout is not { Withheld: { } withheld, Resent: { } resent } || resent.Executor == withheld.Executor || timeout.ResentAt - timeout.WithheldAt < _options.TransitionTimeout)
            {
                _violations.Add(Invariant($"The hand-off of s{ledger} lost with no reply ({timeout.Withheld}) was not expired by TransitionTimeout and sent again through another executor ({timeout.Resent})."));
                return;
            }

            Record($"the lost hand-off of s{ledger} expired after {timeout.ResentAt - timeout.WithheldAt} and went to {resent.Executor}");
        }

        private LedgerMetadata? Ledger(LedgerId ledger) => _history.Select(value => value.Get(ledger)).LastOrDefault(static metadata => metadata is not null);

        /// <summary>
        /// Cold siblings merged, by construction: with a node retired the target is below the open ranges, and a hand-off
        /// executed here gives two open siblings one primary, so the controllers' passes merge them.
        /// </summary>
        private async Task MergeColdSiblingsAsync()
        {
            if (_paths.ContainsKey((TransitionPurpose.Merge, nameof(LedgerMapCommitOutcome.Applied))))
            {
                return;
            }

            var map = await ReadEveryViewAsync();
            var open = map.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();
            var pair = open.SelectMany(ledger => open.Where(sibling => ledger.Range.Depth > 0 && sibling.Range == ledger.Range.Sibling && sibling.Id.Value > ledger.Id.Value).Select(sibling => (ledger, sibling))).FirstOrDefault();
            if (pair.ledger.Id == default)
            {
                _violations.Add(Invariant($"No two open ledgers at epoch {map.Epoch} are siblings to merge."));
                return;
            }

            if (pair.sibling.Primary.Node != pair.ledger.Primary.Node)
            {
                var executor = pair.ledger.Primary.Node;
                var choice = new TransitionChoice(executor, [executor, .. pair.sibling.Replicas.Where(node => node != executor).Concat(pair.ledger.Replicas.Where(node => node != executor)).Distinct().Take(2)], _options.SequencerWindow, _registry.Revision, map.Epoch);
                var handOff = new SentTransition(new Transition.Replace(pair.sibling.Id, choice), TransitionPurpose.Handoff);
                _ = await ExecuteAsync(_controllers[0], handOff, lossy: false);
            }

            await ServeEveryRangeAsync(ranges: 1, done: _ => _paths.ContainsKey((TransitionPurpose.Merge, nameof(LedgerMapCommitOutcome.Applied))), oneController: true);
            Record($"siblings s{pair.ledger.Id} and s{pair.sibling.Id} merged");
        }

        /// <summary>Returns the replicas the node's store lists corrupt, which its report carries, or none while it is down.</summary>
        private ImmutableArray<LedgerId> LostReplicas(NodeId node) =>
            nodes.Single(store => store.NodeId == node) is { IsUp: true } up ? up.Store.ListCorruptReplicas() : [];

        /// <summary>
        /// A replica its node lost, by construction (orleans.md §5.6, LR-I2): once an open ledger's newest record is durable
        /// on a replica node, the node's process restarts, so its recovery classifies the file, and media damage then hits
        /// the WAL pages the record was written to; a read of the replica finds it, and the node's store lists the replica.
        /// The ledger keeps admitting throughout: after the controllers learn of the loss from the node's report, and at each
        /// attempt while it stays open. The controllers repair it at once, though the node lives and <c>G</c> is an hour,
        /// onto the same nodes, whose successor replica on the node is fresh: it holds the successor's next proposal.
        /// </summary>
        private async Task RepairALostReplicaAsync()
        {
            var map = await ReadEveryViewAsync();
            var ranges = map.Ledgers.Count(static ledger => ledger.IsOpen);
            var ledger = map.Ledgers.First(static ledger => ledger.IsOpen);
            var lostOn = nodes.Single(node => node.NodeId == ledger.Replicas.First(replica => replica != ledger.Primary.Node));
            var before = lostOn.SnapshotWal();
            if (await ProposeAsync(ledger) is not { } slot || !await HeldAsync(ledger, slot))
            {
                return;
            }

            var written = lostOn.PagesWrittenSince(before);
            await lostOn.CrashAsync(operatingSystem: false);
            await lostOn.StartAsync(cancellationToken);
            var damaged = 0;
            foreach (var page in written)
            {
                damaged += lostOn.Damage(page) ? 1 : 0;
            }

            var corrupt = false;
            try
            {
                _ = await HoldsAsync(lostOn, ledger, slot);
            }
            catch (LedgerReplicaCorruptException)
            {
                corrupt = true;
            }

            if (damaged == 0 || !corrupt || !lostOn.Store.ListCorruptReplicas().Contains(ledger.Id))
            {
                _violations.Add(Invariant($"Damage to {damaged} of {written.Count} page(s) of {lostOn.NodeId}'s WAL left its replica of s{ledger.Id} unlisted as corrupt."));
                return;
            }

            // The controllers learn of the loss before any pass, and the ledger admits a proposal.
            var report = _processes.Single(process => process.Node == lostOn.NodeId).Controller.Report(diskPressure: false, LostReplicas(lostOn.NodeId), handingOff: false, refused: false, archiveGatePassed: false, draining: false) with { Generation = _registry.Nodes[lostOn.NodeId].Generation };
            foreach (var controller in _controllers)
            {
                _ = controller.Decisions.Report(report);
            }

            if (await ProposeAsync(ledger) is null)
            {
                return;
            }

            var answered = _answers.Count;
            var lostAt = environment.TimeProvider.GetUtcNow();
            await ServeEveryRangeAsync(
                ranges,
                beforeAttempt: async value =>
                {
                    if (value.Get(ledger.Id) is { IsOpen: true })
                    {
                        _ = await ProposeAsync(ledger);
                    }
                },
                done: value => value.Get(ledger.Id) is { IsOpen: false },
                goal: Invariant($"s{ledger.Id} rolled out of its lost replica on {lostOn.NodeId}"));
            // Only a repair rolls the ledger out of its lost replica; any other transition closing it, a load split among them,
            // leaves the loss unanswered.
            var repaired = _answers.Skip(answered).Any(answer => answer.Sent.Purpose == TransitionPurpose.Repair && answer.Reply == nameof(LedgerMapCommitOutcome.Applied) && answer.Sent.Transition.Closes.Contains(ledger.Id));
            var successors = repaired ? (await ReadEveryViewAsync()).Ledgers.Where(open => open.IsOpen && open.Range == ledger.Range).ToList() : [];
            if (successors is not [var successor] || !successor.Replicas.SequenceEqual(ledger.Replicas) || environment.TimeProvider.GetUtcNow() - lostAt >= _options.ReplicaGrace)
            {
                _violations.Add(Invariant($"s{ledger.Id} was not rolled out of its lost replica on {lostOn.NodeId} at once by a repair onto its nodes: its successors are [{string.Join("; ", successors.Select(static open => $"s{open.Id} on {string.Join(", ", open.Replicas)}"))}]."));
                return;
            }

            if (await ProposeAsync(successor) is { } next && await HeldAsync(successor, next))
            {
                Record($"s{ledger.Id}'s replica on {lostOn.NodeId} lost to damage on {damaged} page(s), and repaired by s{successor.Id}");
            }

            // The lost node's replica, which may trail the quorum's write, holds the slot within a bounded wait.
            async Task<bool> HeldAsync(LedgerMetadata of, Slot at)
            {
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    if (await HoldsAsync(lostOn, of, at))
                    {
                        return true;
                    }

                    await DelayAsync(5);
                }

                _violations.Add(Invariant($"{lostOn.NodeId}'s replica of s{of.Id} never held slot {at}."));
                return false;
            }
        }

        /// <summary>Returns whether <paramref name="node"/>'s replica of <paramref name="ledger"/> holds DATA at <paramref name="slot"/>.</summary>
        private async Task<bool> HoldsAsync(LedgerStoreNode node, LedgerMetadata ledger, Slot slot)
        {
            await using var replica = node.Store.OpenReplica(ledger.Id, new LedgerReplicaOptions { WindowSize = ledger.K });
            var read = await replica.ReadAsync(slot, slot, cancellationToken);
            if (read.Value is not ArcBuffer batch)
            {
                return false;
            }

            batch.Dispose();
            return true;
        }

        /// <summary>
        /// Proposes one row to <paramref name="ledger"/>, idle until then, through its primary's writer and answers its slot
        /// once durable. The ledger trace bounds each replica's contiguous tail by its writer's completed prefix, so the
        /// writer is traced from its first proposal.
        /// </summary>
        private async Task<Slot?> ProposeAsync(LedgerMetadata ledger)
        {
            if (_processes.Single(process => process.Node == ledger.Primary.Node).Registry.TryGet(ledger.Id) is not { } writer)
            {
                _violations.Add(Invariant($"s{ledger.Id}'s primary {ledger.Primary.Node} does not serve it."));
                return null;
            }

            if (_traced.Add(ledger.Id))
            {
                replicas.LedgerTrace.TraceWriter(writer, nodes.Single(node => node.NodeId == ledger.Primary.Node).Address, ledger.Id);
            }

            using var payload = Payload([1, 2, 3]);
            using var row = new Row([Enumerable.Range(0, 256).Select(static value => (byte)value).First(value => ledger.Range.Contains([value]))], RowKind.Append, payload);
            if (!(await writer.ProposeAsync([row], cancellationToken)).TryGetValue(out ProposalOutcome.Durable durable))
            {
                _violations.Add(Invariant($"s{ledger.Id}'s primary {ledger.Primary.Node} did not admit a proposal."));
                return null;
            }

            return durable.Slot;

            static ArcBuffer Payload(ReadOnlySpan<byte> body)
            {
                using var writer = new ArcBufferWriter();
                writer.Write(body);
                return writer.ConsumeSlice(body.Length);
            }
        }

        /// <summary>Asserts that the run reached every transition path its scripted steps force.</summary>
        private void AssertPaths()
        {
            Record($"paths {string.Join(", ", _paths.OrderBy(static path => path.Key.ToString(), StringComparer.Ordinal).Select(static path => $"{path.Key.Purpose}/{path.Key.Reply} x{path.Value}"))}");
            // The forced lost send is the run's only expiry by TransitionTimeout, whether a reply came late or a view lagged.
            if (_controllers.Sum(static controller => controller.Decisions.TimeoutExpiries) is var expiries and not 1)
            {
                _violations.Add(Invariant($"The controllers ended {expiries} transitions by TransitionTimeout, not only the forced lost send."));
            }

            foreach (var (purpose, reply) in new[]
            {
                (TransitionPurpose.Bootstrap, nameof(LedgerMapCommitOutcome.Applied)),
                (TransitionPurpose.Split, nameof(LedgerMapCommitOutcome.Applied)),
                (TransitionPurpose.Takeover, nameof(LedgerMapCommitOutcome.Applied)),
                (TransitionPurpose.Takeover, nameof(LedgerMapCommitOutcome.Superseded)),
                (TransitionPurpose.Handoff, nameof(LedgerMapCommitOutcome.Applied)),
                (TransitionPurpose.Handoff, nameof(LedgerMapCommitOutcome.Refused)),
                (TransitionPurpose.Departure, nameof(LedgerMapCommitOutcome.Applied)),
                (TransitionPurpose.Repair, nameof(LedgerMapCommitOutcome.Applied)),
                (TransitionPurpose.Merge, nameof(LedgerMapCommitOutcome.Applied)),
            })
            {
                if (!_paths.ContainsKey((purpose, reply)))
                {
                    _violations.Add(Invariant($"No {purpose} transition was answered {reply}."));
                }
            }
        }

        /// <summary>A rogue writer puts a value with two open ledgers over the whole keyspace in the register, for the map mutant.</summary>
        private async Task WriteRogueValueAsync()
        {
            NodeId[] three = [.. nodes.Take(3).Select(static node => node.NodeId)];
            var claim = new PrimaryClaim(three[0], SequencerInstanceId.New());
            var value = new LedgerMapValue(1, new LedgerId(3), 0, [new LedgerMetadata(new LedgerId(1), KeyRange.Root, [.. three], 4, claim, LedgerStatus.Open.Instance), new LedgerMetadata(new LedgerId(2), KeyRange.Root, [.. three], 4, claim, LedgerStatus.Open.Instance)]);
            using var encoded = value.Encode();
            _ = await register.Writer(9).WriteAsync(null, encoded.AsReadOnlySequence(), cancellationToken);
        }

        /// <summary>
        /// Judges <paramref name="submission"/>'s answer against the register's history so far, as it arrives (OR-I19): a
        /// contradiction this history shows stands in every longer one.
        /// </summary>
        private void Judge(LedgerMapSubmission submission)
        {
            _ = CheckMap();
            var count = _history.Count;
            foreach (var violation in LedgerMapHistoryOracle.Violations(_history, [-1, .. register.AppliedDispatches.Take(count - 1)], [-1, .. register.AppliedWriters.Take(count - 1)], [submission], int.MaxValue))
            {
                _violations.Add("history: " + violation);
            }
        }

        /// <summary>Once every delayed write has landed, judges every committer answer against the register's history (OR-I19).</summary>
        private async Task VerifyAnswersAsync()
        {
            await Task.Delay(register.MaxLandingDelay + 2 * register.MaxLatency, environment.TimeProvider, cancellationToken);
            List<LedgerMapValue> history = [LedgerMapValue.Empty, .. register.AppliedWrites.Select(static bytes => Assert.IsType<LedgerMapValue>(LedgerMapValue.Decode(bytes.AsSpan()).Value))];
            foreach (var violation in LedgerMapHistoryOracle.Violations(history, [-1, .. register.AppliedDispatches], [-1, .. register.AppliedWriters], _submissions, int.MaxValue))
            {
                _violations.Add("history: " + violation);
            }
        }

        /// <summary>Reads every process's view, and waits for its registry's own follower to reconcile with it.</summary>
        private async Task<LedgerMapValue> ReadEveryViewAsync()
        {
            LedgerMapValue latest = LedgerMapValue.Empty;
            foreach (var process in _processes.Where(process => !_isolated.Contains(process.Node)))
            {
                latest = await RetryAsync(() => process.View.ReadAsync(cancellationToken));
                await process.Registry.ReconciledAsync(latest.Epoch, cancellationToken);
            }

            return latest;
        }

        private async Task<T> RetryAsync<T>(Func<Task<T>> operation)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await operation();
                }
                catch (Exception failure) when (failure is IOException or TimeoutException && attempt < 50)
                {
                    await DelayAsync(10);
                }
            }
        }

        private void Record(FormattableString entry) => environment.Trace.Record(Invariant(entry));

        private Task DelayAsync(int maximumMilliseconds) =>
            Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(maximumMilliseconds)), environment.TimeProvider, cancellationToken);
    }

    /// <summary>The scripted timeout: the ledger whose hand-off is lost unanswered, that send, and the controller's next.</summary>
    private sealed class ForcedTimeout(LedgerId ledger)
    {
        public LedgerId Ledger { get; } = ledger;

        public SentTransition? Withheld { get; set; }

        public DateTimeOffset WithheldAt { get; set; }

        public SentTransition? Resent { get; set; }

        public DateTimeOffset ResentAt { get; set; }
    }

    /// <summary>A process's transport: each replica it opens is the process's own, which the node serves as the process's.</summary>
    private sealed class ProcessTransport(SimulatedLedgerReplicaTransport replicas, string client) : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) => replicas.Open(node, ledger, options, client);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            replicas.HeartbeatAsync(node, ledgers, cancellationToken);
    }
}
