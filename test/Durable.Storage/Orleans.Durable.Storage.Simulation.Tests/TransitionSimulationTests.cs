using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Transitions under deterministic simulation (ledgers.md §2.6, §8.1 – §8.5, §10): a process per node, each with its own
/// map view, reader, registry, and executor, over four LedgerStore nodes, of which each ledger's replicas are three, and
/// a simulated <c>ledger-map</c> register which two committers share. Two controllers with views of their own choose
/// transitions racing each other, executors are interrupted and lose their replies, the register answers with unknown
/// outcomes, mismatches, and read failures, and the network drops and duplicates messages, partitions a node, and pauses
/// one, while each process proposes through the primaries it serves.
/// </summary>
public sealed class TransitionSimulationTests
{
    private const int MaxDepth = 3;
    private const int NodeCount = 4;
    private const int ReplicaCount = 3;
    private const int ReplaySeed = 5;
    private const int FirstSeed = 1;

    // Each seed costs about 20 s of wall time, so every change runs one and exploration runs
    // more through CLOG_SIM_SEEDS (test/Durable.Storage/docs/spec/verification.md §7.4, §12).
    private const int SeedCount = 1;
    private const string PathEntry = "path: ";
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// The paths every corpus exercises, each named by the text of the trace entries which record it.
    /// </summary>
    private static readonly (string Path, string Entry)[] Paths =
    [
        ("an applied bootstrap", "path: Bootstrap Applied"),
        ("an applied replace", "path: Replace Applied"),
        ("an applied split", "path: Split Applied"),
        ("an applied merge", "path: Merge Applied"),
        ("a superseded transition", " Superseded"),
        ("an interrupted executor", " failed OperationCanceledException"),
        ("a lost reply or an unknown outcome", " failed IOException"),
        ("a durable proposal", "path: proposal Durable"),
        ("a fenced proposal", "path: proposal Fenced"),
    ];

    // The theory and the corpus check share each seed's run, so the corpus runs once.
    private static readonly ConcurrentDictionary<int, Lazy<ClusterSimulationRun>> Runs = new();

    /// <summary>
    /// Gets the seed corpus.
    /// </summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    /// <summary>
    /// LG-I1 – LG-I9, under racing and interrupted executors, duplicate committers and controllers, and unknown MetaStore
    /// outcomes.
    /// </summary>
    /// <remarks>
    /// After every step, each value the register applied keeps the map's invariants (LG-I1 – LG-I3, LG-I9, LG-I10), and
    /// each ledger it closes completed a recovery's seal coverage (LG-I9). No submitter receives <c>Rejected</c>, and no
    /// proposal fails with <see cref="ObjectDisposedException"/> before teardown. Each registry's unserved ledgers are
    /// those its view names its node's primary of and it does not serve. Once faults stop, every open range is served by
    /// its primary's process. Each ledger's durable proposals are exactly its slots below its recorded <c>T</c>, a prefix
    /// in call order after which none is durable, and each closed ledger's slots hold their durable proposals' batches
    /// (LG-I5); for every key, a proposal answered durable before another was called holds a lower tag (LG-I6). Once the
    /// closed ledgers are dropped, each node's reconciliation drops exactly the replicas its view revokes, and a reader
    /// whose view is stale meets the drop and re-reads the map. Once the floor rises past every change's
    /// <c>ChosenAt</c>, no lifetime stays pending.
    /// </remarks>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void RacingTransitionsKeepTheMapsInvariantsAndEndServed(int seed) => _ = RunSeed(seed);

    /// <summary>
    /// Across the corpus, every path of <see cref="Paths"/> occurs: each kind of transition applies, one is superseded,
    /// an executor is interrupted, a reply is lost, and proposals end durable and fenced.
    /// </summary>
    [Fact]
    public void TheCorpusExercisesEveryPath()
    {
        var entries = new List<string>();
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, SeedCount))
        {
            entries.AddRange(RunSeed(seed).Trace.Where(static entry => entry.Contains(PathEntry, StringComparison.Ordinal)));
        }

        string[] missing = [.. Paths.Where(path => !entries.Any(entry => entry.Contains(path.Entry, StringComparison.Ordinal))).Select(static path => path.Path)];
        Assert.True(missing.Length == 0, $"The corpus never exercised: {string.Join(", ", missing)}.");
    }

    /// <summary>
    /// The scenario replays from its seed and from its artifact.
    /// </summary>
    [Fact]
    public void TheScenarioReplaysFromItsSeed()
    {
        var run = SeededSimulation.RunAndReplay(ReplaySeed, static environment => SimulateAsync(environment, Workload.Replay));

        Assert.Contains(run.Trace, entry => entry.Contains("every open range served", StringComparison.Ordinal));
    }

    private static ClusterSimulationRun RunSeed(int seed) =>
        Runs.GetOrAdd(seed, static seed => new Lazy<ClusterSimulationRun>(() => SeededSimulation.Run(seed, static environment => SimulateAsync(environment, Workload.Corpus)))).Value;

    private static async Task SimulateAsync(SimulationEnvironment environment, Workload workload)
    {
        // The scenario owns the token, so no thread outside the simulation cancels it (test/Durable.Storage/docs/spec/verification.md §7.1).
        using var cancellation = new CancellationTokenSource();
        var network = environment.Network;
        network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        network.MaxJitter = TimeSpan.FromMilliseconds(4);
        network.MessageDropRate = 0.05;
        environment.Rpc.DuplicationRate = 0.05;
        var client = environment.Cluster.AddNode("client");
        var nodes = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, NodeCount, cancellation.Token);
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, Deadline);
        _ = LedgerStoreInvariants.Register(environment, replicas);
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map")
        {
            UnknownOutcomeRate = 0.15,
            AppliedMismatchRate = 0.1,
            ReadFailureRate = 0.05,
        };
        await using var first = new LedgerMapCommitter(register.Writer(0), int.MaxValue);
        await using var second = new LedgerMapCommitter(register.Writer(1), int.MaxValue);
        await new Scenario(environment, replicas, register, nodes, [first, second], workload, cancellation.Token).RunAsync();
    }

    /// <summary>
    /// One node's process: its map view, reader, registry, and executor.
    /// </summary>
    private sealed class Process
    {
        public Process(string name, NodeId node, LedgerMapView view, ILedgerReplicaTransport transport)
        {
            Name = name;
            Node = node;
            View = view;
            Reader = new LedgerReader(view, transport, SimulationEnvironment.Clock, node);
            Registry = new LedgerWriterRegistry(Reader, 1 << 25, SmallSegments.Options, SimulationEnvironment.Clock, NullLoggerFactory.Instance, SmallSegments.Random(name));
            Executor = new TransitionExecutor(Registry, NullLogger<TransitionExecutor>.Instance);
        }

        public string Name { get; }

        public NodeId Node { get; }

        public LedgerMapView View { get; }

        public LedgerReader Reader { get; }

        public LedgerWriterRegistry Registry { get; }

        public TransitionExecutor Executor { get; }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await Reader.DisposeAsync();
        }
    }

    /// <summary>A fault a scripted execution takes for certain.</summary>
    private enum Fault
    {
        None,
        Interrupt,
        LoseReply,
    }

    /// <summary>One proposal's answer, with its key and the scenario's steps at which it was called and answered.</summary>
    private readonly record struct Answered(LedgerId Ledger, int Call, byte Key, long CalledAt, long AnsweredAt, ProposalOutcome Outcome, RowVersion? Version);

    /// <summary>
    /// The scenario's work: the controllers' rounds, the proposals the processes make, whether it then forces each
    /// transition path, and whether it exports its ledger map trace when <see cref="LedgerMapTraces.Enabled"/>. The replay
    /// check runs a smaller workload without the forced paths, since it runs the scenario three times and determinism does
    /// not depend on its size.
    /// </summary>
    private sealed record Workload(int Rounds, int MaxProposals, bool ForcePaths, bool ExportsTrace)
    {
        public static readonly Workload Corpus = new(7, 60, ForcePaths: true, ExportsTrace: true);
        public static readonly Workload Replay = new(2, 15, ForcePaths: false, ExportsTrace: false);
    }

    private sealed class Scenario(
        SimulationEnvironment environment,
        SimulatedLedgerReplicaTransport replicas,
        SimulatedConditionalRegister register,
        LedgerStoreNode[] nodes,
        LedgerMapCommitter[] committers,
        Workload workload,
        CancellationToken cancellationToken)
    {
        private readonly SimulationRandom _random = environment.Random;
        private readonly List<Process> _processes = [];
        private readonly List<Task> _proposals = [];
        private readonly Dictionary<LedgerId, int> _calls = [];
        private readonly HashSet<LedgerId> _traced = [];
        private readonly List<Answered> _outcomes = [];
        private readonly List<string> _rejected = [];
        private readonly List<LedgerMapSubmission> _submissions = [];

        // The register's values and the durable answers in the order the run met them, and the ledgers' batches, which
        // trace validation replays against the ledger map model (test/Durable.Storage/docs/spec/verification.md §5.1). Recording takes no step.
        private readonly LedgerMapTrace _mapTrace = new();
        private bool _running = true;
        private bool _faulty = true;
        private int _checked;
        private long _steps;
        private LedgerMapValue _previous = LedgerMapValue.Empty;

        public async Task RunAsync()
        {
            environment.Invariants.Add("ledger-map", CheckMap);
            try
            {
                for (var index = 0; index < nodes.Length; index++)
                {
                    _processes.Add(await StartAsync($"process-{index}", nodes[index].NodeId));
                }

                if (workload.ForcePaths)
                {
                    await BootstrapAsync();
                }

                var following = SimulationTasks.WhenAll(_processes.Select(FollowAsync));
                var proposing = SimulationTasks.WhenAll(_processes.Select(ProposeAsync));
                var faulty = _random.Choose(nodes);
                var slow = _random.Choose(nodes.Where(node => node != faulty).ToArray());
                var controllers = SimulationTasks.WhenAll(ControlAsync("controller-1"), ControlAsync("controller-2"));
                await SimulationTasks.WhenAll(PartitionAsync(faulty), PauseAsync(slow));
                await controllers;
                Heal();
                await CheckRegistriesAsync();

                await ServeEveryRangeAsync();
                await CloseOneAboveZeroAsync();
                await ServeEveryRangeAsync();
                await CheckRegistriesAsync();
                Record($"every open range served");
                _running = false;
                await proposing;
                await SimulationTasks.WhenAll(_proposals);
                await following;
                if (workload.ForcePaths)
                {
                    await ForceEveryTransitionPathAsync();
                    await ServeEveryRangeAsync();
                    await CheckRegistriesAsync();
                }

                VerifyProposals();
                await VerifyDecidedBatchesAsync();
                VerifyTagOrder();
                await DropAndReconcileAsync();
                await RaiseFloorAsync();
                Assert.Empty(_rejected);
                await VerifyAnswersAsync();
                if (workload.ExportsTrace && LedgerMapTraces.Enabled)
                {
                    await TraceOpenLedgersAsync();
                    _mapTrace.Observe(register.AppliedWrites);
                    LedgerMapTraces.Export(environment, "Transition", nameof(TransitionSimulationTests), _mapTrace, MaxDepth, tracesWrites: true);
                }
            }
            finally
            {
                foreach (var process in _processes)
                {
                    await process.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// Checks each value the register applied since the last step: epochs advance by one, each keeps the map's
        /// invariants against the value before it (LG-I1 – LG-I3, LG-I9, LG-I10), and the ledger trace checks each ledger
        /// it closes (LG-I9).
        /// </summary>
        private string? CheckMap()
        {
            for (; _checked < register.AppliedWrites.Count; _checked++)
            {
                if (LedgerMapValue.Decode(register.AppliedWrites[_checked].AsSpan()).Value is not LedgerMapValue value)
                {
                    return Invariant($"the register applied a value which is not a ledger map, at write {_checked}");
                }

                if (value.Epoch != _previous.Epoch + 1)
                {
                    return Invariant($"epoch {value.Epoch} follows epoch {_previous.Epoch}");
                }

                if (LedgerMapReference.Violation(_previous, value) is { } violation)
                {
                    return violation;
                }

                replicas.LedgerTrace.RecordCloses(_previous, value, AddressOf);
                _previous = value;
            }

            return null;
        }

        private string AddressOf(NodeId node) => nodes.Single(candidate => candidate.NodeId == node).Address;

        private async Task<Process> StartAsync(string name, NodeId node)
        {
            var view = await RetryAsync(() => LedgerMapView.OpenAsync(register, cancellationToken));
            return new Process(name, node, view, new ProcessTransport(replicas, name));
        }

        /// <summary>
        /// A controller with a view of its own chooses a transition from it each round and asks a random process to execute
        /// it: a bootstrap while its view lists no open ledger, and otherwise a replace, split, or merge of an open ledger.
        /// Its executor may be interrupted, and its reply may be lost.
        /// </summary>
        private async Task ControlAsync(string name)
        {
            var view = await RetryAsync(() => LedgerMapView.OpenAsync(register, cancellationToken));
            for (var round = 0; round < workload.Rounds; round++)
            {
                await DelayAsync(60);
                _ = await RetryAsync(() => view.ReadAsync(cancellationToken));
                var process = _random.Choose(_processes);
                _ = await ExecuteAsync(name, process, Choose(view.Current, process.Node), faults: true);
            }
        }

        /// <summary>
        /// Chooses a transition from <paramref name="map"/> for <paramref name="executor"/>: a bootstrap while no ledger is
        /// open, and otherwise a replace, split, or merge of a random open ledger.
        /// </summary>
        private Transition Choose(LedgerMapValue map, NodeId executor)
        {
            var choice = Choice(executor, map.Epoch);
            LedgerMetadata[] open = [.. map.Ledgers.Where(static ledger => ledger.IsOpen)];
            if (open.Length == 0)
            {
                return new Transition.Bootstrap(choice);
            }

            var target = _random.Choose(open);
            return _random.Next(3) switch
            {
                1 when target.Range.Depth < MaxDepth => new Transition.Split(target.Id, choice),
                2 when target.Range.Depth > 0 && open.FirstOrDefault(ledger => ledger.Range == target.Range.Sibling) is { Location.Value: not null } sibling =>
                    new Transition.Merge(target.Id, sibling.Id, choice),
                _ => new Transition.Replace(target.Id, choice),
            };
        }

        /// <summary>
        /// Chooses the replicas of the ledgers a transition opens: its executor, which becomes their primary, and others
        /// at random, so that a replace or a roll changes a range's replicas.
        /// </summary>
        private TransitionChoice Choice(NodeId executor, ulong epoch)
        {
            var others = nodes.Select(static node => node.NodeId).Where(node => node != executor).ToList();
            var chosen = new List<NodeId> { executor };
            while (chosen.Count < ReplicaCount)
            {
                var other = _random.Choose(others.ToArray());
                chosen.Add(other);
                _ = others.Remove(other);
            }

            return new TransitionChoice(executor, [.. chosen], 4, 0, epoch);
        }

        /// <summary>
        /// Executes <paramref name="transition"/> in <paramref name="process"/>, through a random committer; with faults,
        /// the executor may be cancelled, and the committer's reply may be lost after it answered. Records the path it took.
        /// </summary>
        /// <returns>The executor's outcome, or <see langword="null"/> when it failed.</returns>
        private async Task<LedgerMapCommitOutcome?> ExecuteAsync(string controller, Process process, Transition transition, bool faults, Fault forced = Fault.None)
        {
            using var interruption = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interruption.Token);
            if (forced == Fault.Interrupt)
            {
                Interrupt(interruption);
            }
            else if (faults && _random.Chance(0.15))
            {
                _ = InterruptAsync(interruption);
            }

            var loseReply = forced == Fault.LoseReply || faults && _random.Chance(0.15);
            var kind = transition.Value?.GetType().Name;
            try
            {
                var outcome = await process.Executor.ExecuteAsync(
                    transition,
                    async (change, token) =>
                    {
                        var answer = await CommitAsync(committers, _random.Next(committers.Length), change, token);
                        if (answer.Value is LedgerMapCommitOutcome.Rejected rejected)
                        {
                            _rejected.Add(Invariant($"{controller}'s {transition.Value} was rejected: {rejected.Reason}"));
                        }

                        return loseReply ? throw new IOException("The reply was lost.") : answer;
                    },
                    linked.Token);
                Record($"{controller} {kind} by {process.Name}: {outcome.Value}");
                Record($"{PathEntry}{kind} {outcome.Value?.GetType().Name}");
                return outcome;
            }
            catch (Exception failure) when (failure is OperationCanceledException or IOException or TimeoutException
                || failure is AggregateException aggregate && QuorumFailures.IsAdmitted(aggregate, mediaDamage: false))
            {
                Record($"{controller} {kind} by {process.Name} failed: {failure.GetType().Name}");
                Record($"{PathEntry}{kind} failed {failure.GetType().Name}");
                return null;
            }
        }

        /// <summary>
        /// Applies the bootstrap without faults, so every seed of the corpus applies one; the controllers race on everything
        /// after it.
        /// </summary>
        private async Task BootstrapAsync()
        {
            var faults = (environment.Network.MessageDropRate, environment.Rpc.DuplicationRate, register.UnknownOutcomeRate, register.AppliedMismatchRate, register.ReadFailureRate);
            (environment.Network.MessageDropRate, environment.Rpc.DuplicationRate, register.UnknownOutcomeRate, register.AppliedMismatchRate, register.ReadFailureRate) = (0, 0, 0, 0, 0);
            _ = await ExecuteAsync("scripted", _processes[0], new Transition.Bootstrap(Choice(_processes[0].Node, 0)), faults: false);
            (environment.Network.MessageDropRate, environment.Rpc.DuplicationRate, register.UnknownOutcomeRate, register.AppliedMismatchRate, register.ReadFailureRate) = faults;
        }

        /// <summary>
        /// Once faults and proposals stop, takes each transition path the random rounds may miss, so the corpus covers each
        /// by construction: a split applied by the ledger's primary, the same split again, superseded since its ledger closed,
        /// a merge of the split's children applied, an executor interrupted before it submits, and a reply lost after the
        /// change applied.
        /// </summary>
        private async Task ForceEveryTransitionPathAsync()
        {
            var map = await ReadEveryViewAsync();
            var target = map.Ledgers.First(static ledger => ledger.IsOpen && ledger.Range.Depth < MaxDepth);
            var primary = ProcessOf(target.Primary.Node);
            var split = new Transition.Split(target.Id, Choice(primary.Node, map.Epoch));
            _ = await ExecuteAsync("scripted", primary, split, faults: false);
            _ = await ExecuteAsync("scripted", primary, split, faults: false);
            await ServeEveryRangeAsync();

            map = await ReadEveryViewAsync();
            var (lower, upper) = target.Range.Children;
            if (map.Ledgers.FirstOrDefault(ledger => ledger.IsOpen && ledger.Range == lower) is { Location.Value: not null } first
                && map.Ledgers.FirstOrDefault(ledger => ledger.IsOpen && ledger.Range == upper) is { Location.Value: not null } second
                && first.Primary.Node == second.Primary.Node)
            {
                var executor = ProcessOf(first.Primary.Node);
                _ = await ExecuteAsync("scripted", executor, new Transition.Merge(first.Id, second.Id, Choice(executor.Node, map.Epoch)), faults: false);
            }

            await ServeEveryRangeAsync();
            map = await ReadEveryViewAsync();
            var open = map.Ledgers.First(static ledger => ledger.IsOpen);
            var process = ProcessOf(open.Primary.Node);
            _ = await ExecuteAsync("scripted", process, new Transition.Replace(open.Id, Choice(process.Node, map.Epoch)), faults: false, Fault.Interrupt);
            map = await ReadEveryViewAsync();
            open = map.Ledgers.First(static ledger => ledger.IsOpen);
            process = ProcessOf(open.Primary.Node);
            _ = await ExecuteAsync("scripted", process, new Transition.Replace(open.Id, Choice(process.Node, map.Epoch)), faults: false, Fault.LoseReply);
        }

        private async Task InterruptAsync(CancellationTokenSource interruption)
        {
            await DelayAsync(30);
            Interrupt(interruption);
        }

        /// <summary>
        /// Cancels on the simulation's thread: <see cref="CancellationTokenSource.CancelAsync"/> would run the callbacks
        /// the system under test registered on a real thread (test/Durable.Storage/docs/spec/verification.md §7.1).
        /// </summary>
        private static void Interrupt(CancellationTokenSource interruption) => interruption.Cancel();

        /// <summary>
        /// The process reads the map from time to time, as its composition does, until the run ends.
        /// </summary>
        private async Task FollowAsync(Process process)
        {
            while (_running)
            {
                await DelayAsync(40);
                try
                {
                    _ = await process.View.ReadAsync(cancellationToken);
                }
                catch (Exception failure) when (failure is IOException or TimeoutException)
                {
                    // A failed read delays the view; its next read advances it.
                }
            }
        }

        /// <summary>
        /// The process proposes through each primary it serves from time to time, recording every outcome in its ledger's
        /// call order, until the run ends.
        /// </summary>
        private async Task ProposeAsync(Process process)
        {
            while (_running)
            {
                await DelayAsync(40);
                LedgerMetadata[] open = [.. process.View.Current.Ledgers.Where(static ledger => ledger.IsOpen)];
                if (open.Length > 0 && _proposals.Count < workload.MaxProposals)
                {
                    var ledger = _random.Choose(open);
                    if (process.Registry.TryGet(ledger.Id) is { } writer)
                    {
                        _proposals.Add(ProposeAsync(process, writer, ledger));
                    }
                }
            }
        }

        private Task<ProposalOutcome> ProposeAsync(Process process, LedgerWriter writer, LedgerMetadata listed)
        {
            // The ledger trace bounds each replica's contiguous tail by its writer's completed prefix.
            if (_traced.Add(listed.Id))
            {
                replicas.LedgerTrace.TraceWriter(writer, process.Name, listed.Id);
            }

            return ProposeAsync(writer, listed);
        }

        private async Task<ProposalOutcome> ProposeAsync(LedgerWriter writer, LedgerMetadata listed)
        {
            var ledger = listed.Id;
            var call = _calls[ledger] = _calls.GetValueOrDefault(ledger) + 1;
            byte[] key = [(byte)Enumerable.Range(0, 256).First(value => listed.Range.Contains([(byte)value]))];
            var calledAt = ++_steps;
            ProposalOutcome outcome;
            var versions = new List<RowVersion>(1);
            try
            {
                using var buffer = new ArcBufferWriter();
                buffer.Write(BitConverter.GetBytes(call));
                using var payload = buffer.PeekSlice(buffer.Length);
                using var row = new Row(key, RowKind.Append, payload);

                // The batch is written once its creation's turn comes, so the row lives until the proposal took its bytes.
                outcome = await writer.ProposeAsync([row], versions, cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                // A writer leaves its registry only once its view decides it, and the run awaits every proposal before its
                // teardown, so no proposal fails with ObjectDisposedException (ledgers.md §2.4).
                Assert.Fail(Invariant($"s{ledger}'s proposal {call} failed with ObjectDisposedException before teardown"));
                throw;
            }

            _outcomes.Add(new Answered(ledger, call, key[0], calledAt, ++_steps, outcome, versions.Count == 1 ? versions[0] : null));
            if (outcome.Value is ProposalOutcome.Durable durable)
            {
                // The map's writes which landed before the answer precede it in the trace.
                _mapTrace.Observe(register.AppliedWrites);
                _mapTrace.Durable(ledger, durable.Slot, key[0]);
            }

            return outcome;
        }

        /// <summary>
        /// Once faults stop, issues a replace of every open ledger whose primary's process does not serve it, until every
        /// open range is served (ledgers.md §8.6).
        /// </summary>
        private async Task ServeEveryRangeAsync()
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var map = await ReadEveryViewAsync();
                LedgerMetadata[] unserved = [.. map.Ledgers.Where(ledger => ledger.IsOpen && ProcessOf(ledger.Primary.Node).Registry.TryGet(ledger.Id) is null)];
                if (unserved.Length == 0 && map.Ledgers.Any(static ledger => ledger.IsOpen))
                {
                    return;
                }

                if (!map.Ledgers.Any(static ledger => ledger.IsOpen))
                {
                    _ = await ExecuteAsync("healer", _processes[0], Choose(map, _processes[0].Node), faults: false);
                    continue;
                }

                foreach (var ledger in unserved)
                {
                    var process = _random.Choose(_processes);
                    _ = await ExecuteAsync("healer", process, new Transition.Replace(ledger.Id, Choice(process.Node, map.Epoch)), faults: false);
                }
            }

            Assert.Fail("Once faults stopped, some open range stayed without a served primary.");
        }

        /// <summary>
        /// Closes a ledger with a durable proposal, so the run closes one with <c>T</c> above zero, whose drop a stale
        /// reader then meets.
        /// </summary>
        private async Task CloseOneAboveZeroAsync()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var map = await ReadEveryViewAsync();
                if (map.Ledgers.Any(static ledger => ledger.Status.Value is LedgerStatus.Closed { T.Value: > 0 }))
                {
                    return;
                }

                var ledger = _random.Choose(map.Ledgers.Where(static ledger => ledger.IsOpen).ToArray());
                var primary = ProcessOf(ledger.Primary.Node);
                if (primary.Registry.TryGet(ledger.Id) is { } writer)
                {
                    var proposal = ProposeAsync(primary, writer, ledger);
                    _proposals.Add(proposal);
                    _ = await proposal;
                }

                var process = _random.Choose(_processes);
                _ = await ExecuteAsync("healer", process, new Transition.Replace(ledger.Id, Choice(process.Node, map.Epoch)), faults: false);
                await ServeEveryRangeAsync();
            }

            Assert.Fail("Once faults stopped, no ledger closed above slot zero.");
        }

        /// <summary>
        /// Checks that each process's registry lists unserved exactly the open ledgers its view names its node's primary of
        /// and it does not serve, once it reconciled with its view; a lifetime which stopped meanwhile is listed once the
        /// follower reconciles again.
        /// </summary>
        private async Task CheckRegistriesAsync()
        {
            foreach (var process in _processes)
            {
                for (var attempt = 0; ; attempt++)
                {
                    var epoch = process.View.Current.Epoch;
                    await process.Registry.ReconciledAsync(epoch, cancellationToken);
                    var view = process.View.Current;
                    LedgerId[] expected = [.. view.Ledgers.Where(ledger => ledger.IsOpen && ledger.Primary.Node == process.Node && process.Registry.TryGet(ledger.Id) is null).Select(static ledger => ledger.Id)];
                    var unserved = process.Registry.Unserved;
                    if (view.Epoch == epoch && expected.SequenceEqual(unserved))
                    {
                        break;
                    }

                    Assert.True(
                        attempt < 10,
                        Invariant($"{process.Name}'s registry lists [{string.Join(", ", unserved)}] unserved, where its view and writers make [{string.Join(", ", expected)}]"));
                }
            }
        }

        /// <summary>
        /// LG-I5: each ledger's durable proposals take increasing slots in call order, below its recorded <c>T</c> once it is
        /// closed, after which none is durable; none is ambiguous, since every ledger stays in the map. The slots between
        /// them hold the markers which ended aged segments, which <see cref="VerifyDecidedBatchesAsync"/> reads.
        /// </summary>
        private void VerifyProposals()
        {
            var map = Assert.IsType<LedgerMapValue>(LedgerMapValue.Decode(register.AppliedWrites[^1].AsSpan()).Value);
            foreach (var group in _outcomes.GroupBy(static outcome => outcome.Ledger))
            {
                var durable = new List<long>();
                var fenced = false;
                foreach (var answered in group.OrderBy(static outcome => outcome.Call))
                {
                    switch (answered.Outcome.Value)
                    {
                        case ProposalOutcome.Durable at:
                            Assert.False(fenced, Invariant($"s{group.Key}'s proposal {answered.Call} is durable at {at.Slot} after a fenced one"));
                            durable.Add(at.Slot.Value);
                            break;
                        case ProposalOutcome.Fenced:
                            fenced = true;
                            break;
                        case ProposalOutcome.Ambiguous:
                            Assert.Fail(Invariant($"s{group.Key}'s proposal {answered.Call} is ambiguous, though its ledger stayed in the map"));
                            break;
                        default:
                            throw new InvalidOperationException("A proposal outcome holds no case.");
                    }
                }

                // Proposals take slots in call order, so the durable ones are increasing, and below T once it is recorded.
                for (var i = 1; i < durable.Count; i++)
                {
                    Assert.True(durable[i - 1] < durable[i], Invariant($"s{group.Key}'s durable slots [{string.Join(", ", durable)}] are out of call order"));
                }

                if (map.Get(group.Key)!.Value.Status.Value is LedgerStatus.Closed closed && durable.Count > 0)
                {
                    Assert.True(durable[^1] < closed.T.Value, Invariant($"s{group.Key}'s proposal is durable at {durable[^1]}, at or past its recorded T {closed.T}"));
                }
            }

            Record($"{_outcomes.Count} proposals over {_calls.Count} ledgers");
            foreach (var path in _outcomes.Select(static answered => answered.Outcome.Value?.GetType().Name).Distinct().Order(StringComparer.Ordinal))
            {
                Record($"{PathEntry}proposal {path}");
            }
        }

        /// <summary>
        /// LG-I5 against the replicas: each closed ledger's slots below its recorded <c>T</c> hold the batches of the
        /// proposals answered durable at them.
        /// </summary>
        private async Task VerifyDecidedBatchesAsync()
        {
            var view = await LedgerMapView.OpenAsync(register, cancellationToken);
            await using var reader = new LedgerReader(view, new ProcessTransport(replicas, "verifier"), SimulationEnvironment.Clock, localNode: null);
            var closedLedgers = 0;
            foreach (var listed in view.Current.Ledgers)
            {
                if (listed.Status.Value is not LedgerStatus.Closed { T.Value: > 0 })
                {
                    continue;
                }

                var durableAt = _outcomes
                    .Where(answered => answered.Ledger == listed.Id && answered.Outcome.Value is ProposalOutcome.Durable)
                    .ToDictionary(static answered => ((ProposalOutcome.Durable)answered.Outcome.Value).Slot.Value, static answered => answered.Call);
                var versionAt = _outcomes
                    .Where(answered => answered.Ledger == listed.Id && answered.Outcome.Value is ProposalOutcome.Durable && answered.Version is not null)
                    .ToDictionary(static answered => ((ProposalOutcome.Durable)answered.Outcome.Value).Slot.Value, static answered => answered.Version!.Value);
                var rewindFrom = _random.Next((int)Math.Min(int.MaxValue, ((LedgerStatus.Closed)listed.Status.Value).T.Value));
                await DecidedBatches.VerifyAsync(reader, listed, durableAt, () => DelayAsync(5), cancellationToken, versionAt, rewindFrom, (slot, item, data) => _mapTrace.Batch(listed.Id, slot, item, data));
                closedLedgers++;
            }

            Record($"{closedLedgers} closed ledgers hold their durable proposals' batches");
        }

        /// <summary>
        /// Scans each open ledger through its highest slot answered durable, so the trace holds the batches of the ledgers
        /// <see cref="VerifyDecidedBatchesAsync"/> did not read. It runs only when the trace is exported, after every check,
        /// so it leaves the schedule of the run the checks judge as it is.
        /// </summary>
        private async Task TraceOpenLedgersAsync()
        {
            var view = await LedgerMapView.OpenAsync(register, cancellationToken);
            await using var reader = new LedgerReader(view, new ProcessTransport(replicas, "tracer"), SimulationEnvironment.Clock, localNode: null);
            foreach (var listed in view.Current.Ledgers.Where(static ledger => ledger.IsOpen))
            {
                var end = _outcomes
                    .Where(answered => answered.Ledger == listed.Id)
                    .Select(static answered => answered.Outcome.Value is ProposalOutcome.Durable durable ? durable.Slot.Value + 1 : 0)
                    .DefaultIfEmpty(0)
                    .Max();
                while (end > 0)
                {
                    try
                    {
                        // An open ledger's scan reads below the contiguous tail a seal check establishes (LP-I9).
                        if ((await reader.ReadTailAsync(listed, cancellationToken)).Value is not LedgerTail tail)
                        {
                            await DelayAsync(5);
                            continue;
                        }

                        var slot = 0L;
                        await foreach (var item in reader.ScanAsync(listed, Slot.Zero, new Slot(Math.Min(end, tail.ContiguousTail.Value)), cancellationToken))
                        {
                            Assert.True(item.Read.TryGetValue(out LedgerRead.Data data), Invariant($"open s{listed.Id}'s slot {slot}, below a durable answer, read {item.Read}"));
                            using (data)
                            {
                                _mapTrace.Batch(listed.Id, slot, item, data);
                            }

                            slot++;
                        }

                        break;
                    }
                    catch (AggregateException failure) when (QuorumFailures.IsAdmitted(failure, mediaDamage: false))
                    {
                        await DelayAsync(5);
                    }
                }
            }
        }

        /// <summary>
        /// LG-I6, across ledgers: for every key, a proposal answered durable before another durable one was called holds the
        /// lower tag, its ledger's ID and its slot, so the key's tag order is its acceptance order.
        /// </summary>
        private void VerifyTagOrder()
        {
            var pairs = 0;
            foreach (var key in _outcomes.Where(static answered => answered.Outcome.Value is ProposalOutcome.Durable).GroupBy(static answered => answered.Key))
            {
                var accepted = key.ToArray();
                foreach (var earlier in accepted)
                {
                    foreach (var later in accepted)
                    {
                        if (earlier.AnsweredAt >= later.CalledAt)
                        {
                            continue;
                        }

                        pairs++;
                        var (earlierTag, laterTag) = (Tag(earlier), Tag(later));
                        Assert.True(
                            earlierTag.CompareTo(laterTag) < 0,
                            Invariant($"key {key.Key:x2}: s{earlier.Ledger}'s proposal {earlier.Call}, durable at tag {earlierTag} before s{later.Ledger}'s proposal {later.Call} was called, does not precede its tag {laterTag}"));
                    }
                }
            }

            Record($"{pairs} pairs of durable proposals of one key keep their acceptance order in tag order");

            static (ulong Ledger, long Slot) Tag(Answered answered) =>
                (answered.Ledger.Value, ((ProposalOutcome.Durable)answered.Outcome.Value).Slot.Value);
        }

        /// <summary>
        /// Drops every closed ledger in lineage order (LG-I10), reconciles every node from its process's view, and has a
        /// reader whose view predates the drops meet one and re-read the map (ledgers.md §8.4, §8.5).
        /// </summary>
        private async Task DropAndReconcileAsync()
        {
            var stale = await LedgerMapView.OpenAsync(register, cancellationToken);
            await using var reader = new LedgerReader(stale, new ProcessTransport(replicas, "stale-reader"), SimulationEnvironment.Clock, localNode: null);
            var dropped = stale.Current.Ledgers.First(static ledger => ledger.Status.Value is LedgerStatus.Closed { T.Value: > 0 });
            while (true)
            {
                var map = await ReadEveryViewAsync();
                LedgerMetadata[] droppable = [.. map.Ledgers.Where(ledger => !ledger.IsOpen && !map.Ledgers.Any(lower => lower.Id < ledger.Id && lower.Range.Overlaps(ledger.Range)))];
                if (droppable.Length == 0)
                {
                    break;
                }

                foreach (var ledger in droppable)
                {
                    Assert.IsType<LedgerMapCommitOutcome.Applied>((await CommitAsync(committers, 0, new LedgerMapChange.Drop(ledger.Id), cancellationToken)).Value);
                }
            }

            _ = await ReadEveryViewAsync();
            var reconciled = 0;
            foreach (var process in _processes)
            {
                var store = replicas.Node(process.Node);
                var before = await store.ListReplicasAsync(cancellationToken);
                var view = process.View.Current;
                var dropping = await new ReplicaReconciler(store, process.View).ReconcileAsync(cancellationToken);

                // ledgers.md §8.5, restated: a view revokes a node's replica of a ledger whose ID lies below the view's
                // NextLedgerId and which the view does not list with the node (LG-I7).
                LedgerId[] revoked = [.. before.Where(id => id.Value < view.NextLedgerId.Value && !(view.Get(id) is { } listed && listed.Replicas.Contains(process.Node)))];
                Assert.Equal(revoked, dropping);
                var after = await store.ListReplicasAsync(cancellationToken);
                Assert.Equal([.. before.Except(revoked)], after);
                foreach (var listed in view.Ledgers.Where(ledger => ledger.Replicas.Contains(process.Node) && before.Contains(ledger.Id)))
                {
                    Assert.True(after.Contains(listed.Id), Invariant($"{process.Name}'s node dropped s{listed.Id}, which its view lists with it"));
                }

                reconciled += dropping.Count;
            }

            var read = await reader.ReadAsync(dropped, Slot.Zero, cancellationToken);
            Assert.True(read.TryGetValue(out ReplicaWriteOutcome.Dropped? _), Invariant($"a stale reader of dropped s{dropped.Id} read {read}"));
            Assert.Null(stale.Current.Get(dropped.Id));
            Record($"the nodes dropped {reconciled} revoked replicas; a stale reader met s{dropped.Id}'s drop and re-read the map");
        }

        /// <summary>
        /// Raises the retirement floor above every change's <c>ChosenAt</c>, after which no change a lifetime awaits can
        /// apply, so once each registry reconciles with the floor no lifetime stays pending (ledgers.md §8.3).
        /// </summary>
        private async Task RaiseFloorAsync()
        {
            Assert.IsType<LedgerMapCommitOutcome.Applied>((await CommitAsync(committers, 0, new LedgerMapChange.RaiseFloor(1), cancellationToken)).Value);
            _ = await ReadEveryViewAsync();
            foreach (var process in _processes)
            {
                Assert.True(process.Registry.PendingLifetimes == 0, Invariant($"{process.Name}'s registry holds {process.Registry.PendingLifetimes} lifetimes pending, whose changes can no longer apply"));
            }

            Record($"no lifetime pending once the floor passed every change");
        }

        /// <summary>
        /// Submits <paramref name="change"/> to committer <paramref name="writer"/>, which writes as the register's writer
        /// of that index, and records the answer, or the failure which leaves it unknown, for the history oracle.
        /// </summary>
        private async Task<LedgerMapCommitOutcome> CommitAsync(LedgerMapCommitter[] committers, int writer, LedgerMapChange change, CancellationToken token)
        {
            var committer = committers[writer];
            var submittedAt = (ulong)register.AppliedWrites.Count;
            try
            {
                var answer = await committer.CommitAsync(change, token);
                _submissions.Add(new LedgerMapSubmission(change, writer, submittedAt, answer, AnsweredAt: register.Dispatches));
                return answer;
            }
            catch (Exception failure) when (failure is IOException or TimeoutException)
            {
                _submissions.Add(new LedgerMapSubmission(change, writer, submittedAt, null, failure, register.Dispatches));
                throw;
            }
        }

        /// <summary>
        /// Once every delayed write has landed, <see cref="LedgerMapHistoryOracle"/> judges every committer answer against
        /// the register's values since its submission.
        /// </summary>
        private async Task VerifyAnswersAsync()
        {
            await Task.Delay(register.MaxLandingDelay + 2 * register.MaxLatency, environment.TimeProvider, cancellationToken);
            List<LedgerMapValue> history = [LedgerMapValue.Empty, .. register.AppliedWrites.Select(static bytes => Assert.IsType<LedgerMapValue>(LedgerMapValue.Decode(bytes.AsSpan()).Value))];
            Assert.Empty(LedgerMapHistoryOracle.Violations(history, [-1, .. register.AppliedDispatches], [-1, .. register.AppliedWriters], _submissions));
            Record($"{_submissions.Count} committer answers hold in the register's history");
        }

        /// <summary>
        /// Reads every process's view, and waits for its registry's own follower to reconcile with it.
        /// </summary>
        private async Task<LedgerMapValue> ReadEveryViewAsync()
        {
            LedgerMapValue latest = LedgerMapValue.Empty;
            foreach (var process in _processes)
            {
                latest = await process.View.ReadAsync(cancellationToken);
                await process.Registry.ReconciledAsync(latest.Epoch, cancellationToken);
            }

            return latest;
        }

        private Process ProcessOf(NodeId node) => _processes.Single(process => process.Node == node);

        private async Task<T> RetryAsync<T>(Func<Task<T>> read)
        {
            while (true)
            {
                try
                {
                    return await read();
                }
                catch (Exception failure) when (failure is IOException or TimeoutException)
                {
                    await DelayAsync(5);
                }
            }
        }

        private void Heal()
        {
            _faulty = false;
            environment.Network.MessageDropRate = 0;
            environment.Rpc.DuplicationRate = 0;
            register.UnknownOutcomeRate = 0;
            register.AppliedMismatchRate = 0;
            register.ReadFailureRate = 0;
            Record($"faults healed");
        }

        private async Task PartitionAsync(LedgerStoreNode node)
        {
            await DelayAsync(100);
            node.Isolate();
            await DelayAsync(60);
            node.Reconnect();
        }

        private async Task PauseAsync(LedgerStoreNode node)
        {
            await DelayAsync(100);
            var pause = _random.NextTimeSpan(TimeSpan.FromMilliseconds(100));
            node.Pause(pause);
            await Task.Delay(pause + TimeSpan.FromMilliseconds(1), environment.TimeProvider, cancellationToken);
        }

        private Task DelayAsync(int maximumMilliseconds) =>
            Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(maximumMilliseconds)), environment.TimeProvider, cancellationToken);

        private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

        private void Record(FormattableString entry) => environment.Trace.Record(Invariant(entry) + (_faulty ? " (faulty)" : string.Empty));
    }

    /// <summary>
    /// A process's transport: each replica it opens is the process's own, which the node serves as the process's.
    /// </summary>
    private sealed class ProcessTransport(SimulatedLedgerReplicaTransport replicas, string client) : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) => replicas.Open(node, ledger, options, client);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            replicas.HeartbeatAsync(node, ledgers, cancellationToken);
    }
}
