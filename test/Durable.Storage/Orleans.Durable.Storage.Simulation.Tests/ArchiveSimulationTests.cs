using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// History reads against a reference history under deterministic simulation (archive.md §2.2, §8.1, §10; HI-I1, HI-I4):
/// two processes, each able to serve primaries, on two of three LedgerStore nodes propose rows of four keys, of every
/// kind, through the ledgers they serve, storing each key's token chain and versions as Journals will
/// (<see cref="ArchiveChains"/>), and execute replaces, splits, and merges, which move ranges' primaries between them,
/// while a drainer in a third process drains each closed ledger's segments in lineage order into a simulated archive
/// store, sometimes only its first segments before a read which meets rows the store holds in a listed ledger, drops it,
/// and has every node drop its replicas. The store answers <c>Stale</c> at random, and replica reads answer backpressure at
/// random. Readers in all three processes read histories and first touches throughout, and a takeover moves a key's
/// primary to the other process during a read.
/// </summary>
/// <remarks>
/// The reference history of a key is its decided rows in tag order: each drained ledger's rows as its drain read them to
/// <c>T</c>, and at the end each listed ledger's to its <c>T</c> or, open, to the tail its primary's process confirms.
/// Each read's rows are exactly the reference's rows of the ledgers it read below the ends it names, and its prefix
/// followed by its rows not held folds to the reference over its cut: those rows and every row of an earlier ledger it did
/// not read, which the store covered. Over that cut no row was proposed after the read ended, every row of a ledger closed
/// before it began is in it, and a read which reports itself linearizable holds every row answered durable before it began
/// (HI-I1, LG-I8). After every step, the chain checker judges each new read's rows against its prefix's token, and each
/// newly drained ledger's rows with the key's whole chain from <c>Origin</c>; the version checker finds no key's version
/// repeated, and every version base a read or first touch answers names one state of its key, so a rebase to it is fresh
/// (HI-I4). Each first touch's prefix, merged by <c>Holds</c> with the reference rows of the ledgers its map value lists,
/// folds to the reference below the value's next ID.
/// </remarks>
public sealed class ArchiveSimulationTests
{
    private const int FirstSeed = 1;

    // Each seed costs about 19 s of wall time, so every change runs three and exploration runs
    // more through DURABLE_STORAGE_SIM_SEEDS (test/Durable.Storage/docs/spec/verification.md §7.4, §12).
    private const int SeedCount = 3;
    private const int ReplaySeed = 2;
    private const int Writers = 3;
    private const int MaxDepth = 2;
    private const int Window = 4;
    private const string CountsEntry = "history counts: ";
    private static readonly byte[] Keys = [0x10, 0x50, 0x90, 0xd0];
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);

    // The theory and the corpus check share each seed's run, so the corpus runs once.
    private static readonly ConcurrentDictionary<int, Lazy<ClusterSimulationRun>> Runs = new();

    /// <summary>Gets the seed corpus.</summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    /// <summary>HI-I1: every read and first touch matches the reference history.</summary>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void HistoryReadsMatchTheReferenceHistory(int seed) => _ = RunSeed(seed);

    /// <summary>
    /// Across the corpus, reads restart on each cause (a <c>Stale</c> answer, a dropped ledger, a sealed open ledger),
    /// resume after backpressure, and meet rows the store holds in a listed ledger, and a proposal is fenced, whose row no
    /// chain may join.
    /// </summary>
    [Fact]
    public void TheCorpusExercisesEveryRestartAndOverlap()
    {
        var totals = new int[6];
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, SeedCount))
        {
            var counts = RunSeed(seed).Trace.Single(static entry => entry.Contains(CountsEntry, StringComparison.Ordinal));
            var values = counts[(counts.IndexOf(CountsEntry, StringComparison.Ordinal) + CountsEntry.Length)..].Split(' ');
            for (var index = 0; index < totals.Length; index++)
            {
                totals[index] += int.Parse(values[index], CultureInfo.InvariantCulture);
            }
        }

        string[] names = ["Stale", "Dropped", "Sealed", "Backpressured", "Held rows", "Fenced proposals"];
        Assert.True(totals.All(static total => total > 0), $"The corpus never exercised: {string.Join(", ", names.Where((_, index) => totals[index] == 0))}.");
    }

    /// <summary>
    /// archive.md §10's read-order mutant, which reads the store before it takes the map, fails on every seed of the
    /// corpus, in the schedule which drains and drops a ledger between a read's store read and its scans: the read's chain
    /// check finds its live rows starting past its prefix's token (HI-I4), or the oracle its history short.
    /// </summary>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheMapAfterStoreMutantFailsTheOracle(int seed)
    {
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(seed, static environment => SimulateAsync(environment, Workload.Corpus, mapAfterStore: true), minimize: false));

        Assert.True(
            failure.Message.Contains("HI-I4", StringComparison.Ordinal) || failure.Message.Contains("which the reference over its cut folds to", StringComparison.Ordinal),
            failure.Message);
    }

    /// <summary>
    /// The chain checker's mutants (archive.md §10, verification.md §3.2): writers which store a token from the wrong
    /// parent, plan a key's chain through fenced rows, or reset a key's version mid-chain, fail with HI-I4 whenever the
    /// mutant fires, from the chain checker over the drained rows, a read's chain check, or the store's own check of a
    /// drain. <see cref="ArchiveChains.ChainMutant.PlansFenced"/> only fires on seeds where a key is decided after a
    /// fenced proposal, so it is required to fire and fail across the corpus and never pass silently when it fires.
    /// </summary>
    /// <param name="mutant">The writer's mutant.</param>
    [Theory]
    [InlineData(nameof(ArchiveChains.ChainMutant.WrongParent))]
    [InlineData(nameof(ArchiveChains.ChainMutant.PlansFenced))]
    [InlineData(nameof(ArchiveChains.ChainMutant.ResetsVersion))]
    public void AWriterWhichBreaksTheChainFails(string mutant)
    {
        var chainMutant = Enum.Parse<ArchiveChains.ChainMutant>(mutant);
        var failures = 0;
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, SeedCount))
        {
            var plannedThroughFenced = false;
            try
            {
                _ = SeededSimulation.Run(
                    seed,
                    environment => SimulateAsync(environment, Workload.Corpus, mapAfterStore: false, chainMutant, planned => plannedThroughFenced = planned),
                    minimize: false);
            }
            catch (SimulationFailedException failure)
            {
                Assert.Contains("HI-I4", failure.Message, StringComparison.Ordinal);
                failures++;
                continue;
            }

            Assert.True(
                chainMutant == ArchiveChains.ChainMutant.PlansFenced && !plannedThroughFenced,
                $"Mutant {mutant} survived seed {seed} (plannedThroughFenced={plannedThroughFenced}).");
        }

        Assert.True(failures > 0, $"Mutant {mutant} never fired on any corpus seed.");
    }

    /// <summary>The scenario replays from its seed and from its artifact.</summary>
    [Fact]
    public void TheScenarioReplaysFromItsSeed()
    {
        var run = SeededSimulation.RunAndReplay(ReplaySeed, static environment => SimulateAsync(environment, Workload.Replay, mapAfterStore: false));

        Assert.Contains(run.Trace, entry => entry.Contains("history reads match the reference", StringComparison.Ordinal));
    }

    private static ClusterSimulationRun RunSeed(int seed) =>
        Runs.GetOrAdd(seed, static seed => new Lazy<ClusterSimulationRun>(() => SeededSimulation.Run(seed, static environment => SimulateAsync(environment, Workload.Corpus, mapAfterStore: false)))).Value;

    private static async Task SimulateAsync(SimulationEnvironment environment, Workload workload, bool mapAfterStore, ArchiveChains.ChainMutant chainMutant = ArchiveChains.ChainMutant.None, Action<bool>? onCompleted = null)
    {
        // The scenario owns the token, so no thread outside the simulation cancels it (test/Durable.Storage/docs/spec/verification.md §7.1).
        using var cancellation = new CancellationTokenSource();
        var network = environment.Network;
        network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        network.MaxJitter = TimeSpan.FromMilliseconds(4);
        environment.Rpc.DuplicationRate = 0.05;
        var client = environment.Cluster.AddNode("client");
        var nodes = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, 3, cancellation.Token);
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, Deadline);
        _ = LedgerStoreInvariants.Register(environment, replicas);
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
        await using var committer = new LedgerMapCommitter(register.Writer(0), int.MaxValue);
        using var store = new SimulatedArchiveStore(environment, "history") { StaleRate = 0.1 };
        var scenario = new Scenario(environment, replicas, register, nodes, committer, store, workload, mapAfterStore, chainMutant, cancellation.Token);
        await scenario.RunAsync();
        onCompleted?.Invoke(scenario.PlannedThroughFenced);
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The scenario's work: each writer's proposals, each reader's reads, and the control rounds of transitions. The
    /// replay check runs a smaller workload, since it runs the scenario twice and determinism does not depend on its size.
    /// </summary>
    private sealed record Workload(int ProposalsPerWriter, int ReadsPerReader, int Transitions)
    {
        public static readonly Workload Corpus = new(10, 8, 4);
        public static readonly Workload Replay = new(2, 2, 1);
    }

    /// <summary>
    /// A row as the reference and the reads hold it: its key, tag, kind, its proposal's ID, its payload, its segment, its
    /// stored token, its version, and whether a read's prefix held it.
    /// </summary>
    private readonly record struct ReferenceRow(byte Key, Tag Tag, RowKind Kind, int Id, LedgerSegmentId Segment, RowToken Token, RowVersion Version, bool Held = false)
    {
        public byte[] Body => BitConverter.GetBytes(Id);

        public ChainRow Chain => new(Tag, Segment, Kind, sizeof(int), Kind == RowKind.Delete && !Held ? Body : ReadOnlyMemory<byte>.Empty, Token, Version, Held);

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Id}@{Tag}{(Held ? " held" : string.Empty)}");
    }

    /// <summary>One proposal, with the scenario's steps at which it was called and answered.</summary>
    private sealed record Proposal(int Id, byte Key, RowKind Kind, LedgerId Ledger, long CalledAt)
    {
        public long AnsweredAt { get; set; } = long.MaxValue;

        public ProposalOutcome Outcome { get; set; }
    }

    /// <summary>
    /// One history read, with its steps, its rows and <c>Through</c>, what its prefix and rows not held fold to, the chain
    /// checker's verdict on its rows, and whether it reported itself linearizable.
    /// </summary>
    private sealed record ArchiveReadRecord(string Reader, byte Key, long CalledAt, long AnsweredAt, ReferenceRow[] Rows, Dictionary<LedgerId, Slot> Through, ArchiveFold Fold, string? Chain, bool Linearizable)
    {
        /// <summary>Gets the prefix's version base, or null for <c>Absent</c>.</summary>
        public RowToken? VersionBase { get; init; }

        /// <summary>Gets the state the prefix alone holds.</summary>
        public ArchiveFold PrefixFold { get; init; } = ArchiveFold.Nothing;
    }

    /// <summary>One first touch: the map value it paired, and the store's prefix, which the record owns, or null for absence.</summary>
    private sealed record StoreReadRecord(string Reader, byte Key, LedgerMapValue Map, ArchivePrefix? Prefix)
    {
        /// <summary>Gets the state the prefix holds, read while the simulation runs, whose buffers it alone may touch.</summary>
        public string PrefixState { get; } = ArchiveFold.Of(Prefix).ToString();
    }

    /// <summary>A ledger's decided rows, as its drain or the end of the run read them, and the end it read them to.</summary>
    private sealed record DecidedLedger(ReferenceRow[] Rows, Slot End);

    /// <summary>A process: its map view, reader, and history reader; a primary-capable one also has a registry and executor.</summary>
    private sealed class Process
    {
        public Process(string name, NodeId node, LedgerMapView view, ILedgerReplicaTransport transport, IArchiveStore store, bool primary, bool mapAfterStore)
        {
            Name = name;
            Node = node;
            View = view;
            Reader = new LedgerReader(view, transport, SimulationEnvironment.Clock, node);
            Archive = new ArchiveReader(Reader, store, SimulationEnvironment.Clock) { MapAfterStoreMutant = mapAfterStore };
            if (primary)
            {
                Registry = new LedgerWriterRegistry(Reader, 1 << 25, SmallSegments.Options, SimulationEnvironment.Clock, NullLoggerFactory.Instance, SmallSegments.Random(name));
                Executor = new TransitionExecutor(Registry, NullLogger<TransitionExecutor>.Instance);
            }
        }

        public string Name { get; }

        public NodeId Node { get; }

        public LedgerMapView View { get; }

        public LedgerReader Reader { get; }

        public ArchiveReader Archive { get; }

        public LedgerWriterRegistry? Registry { get; }

        public TransitionExecutor? Executor { get; }

        public async ValueTask DisposeAsync()
        {
            if (Registry is not null)
            {
                await Registry.DisposeAsync();
            }

            await Reader.DisposeAsync();
        }
    }

    private sealed class Scenario(
        SimulationEnvironment environment,
        SimulatedLedgerReplicaTransport replicas,
        SimulatedConditionalRegister register,
        LedgerStoreNode[] nodes,
        LedgerMapCommitter committer,
        SimulatedArchiveStore store,
        Workload workload,
        bool mapAfterStore,
        ArchiveChains.ChainMutant chainMutant,
        CancellationToken cancellationToken)
    {
        private readonly SimulationRandom _random = environment.Random;
        private readonly ArchiveChains _chains = new(store) { Mutant = chainMutant };
        private readonly HashSet<byte> _proposing = [];
        private readonly VersionChecker _versions = new();
        private readonly List<Process> _processes = [];
        private readonly List<Proposal> _proposals = [];
        private readonly List<ArchiveReadRecord> _reads = [];
        private readonly List<StoreReadRecord> _storeReads = [];
        private readonly Dictionary<LedgerId, DecidedLedger> _decided = [];
        private readonly Dictionary<LedgerId, long> _closedAt = [];
        private readonly HashSet<LedgerId> _traced = [];
        private readonly List<ProcessTransport> _transports = [];
        private LedgerMapValue _previous = LedgerMapValue.Empty;
        private bool _running = true;
        private int _checked;
        private int _nextId;
        private int _overlaps;
        private int _fenced;
        private long _steps;
        private int _readsChecked;
        private int _storeReadsChecked;
        private readonly Queue<LedgerId> _drainedUnchecked = new();

        private Process Drainer => _processes[2];

        public bool PlannedThroughFenced => _chains.PlannedThroughFenced;

        private IEnumerable<Process> Primaries => _processes.Take(2);

        public async Task RunAsync()
        {
            environment.Invariants.Add("ledger-map", CheckMap);
            environment.Invariants.Add("archive-chains", CheckChainsAndVersions);
            try
            {
                _processes.Add(await StartAsync("process-0", nodes[0].NodeId, primary: true));
                _processes.Add(await StartAsync("process-1", nodes[1].NodeId, primary: true));
                _processes.Add(await StartAsync("drainer", nodes[2].NodeId, primary: false));
                while (await ExecuteAsync(_processes[0], view => new Transition.Bootstrap(Choice(_processes[0], view.Epoch))) is not LedgerMapCommitOutcome.Applied)
                {
                    await DelayAsync(5);
                }

                if (mapAfterStore)
                {
                    // The mutant runs the read-order schedule alone, which fails it on every seed.
                    _running = false;
                    await ReadAcrossADrainAndDropAsync();
                    await CollectDecidedAsync();
                    VerifyReads();
                    return;
                }

                var writers = SimulationTasks.WhenAll(Enumerable.Range(0, Writers).Select(_ => WriteAsync()));
                var readers = SimulationTasks.WhenAll(_processes.Select(ReadAsync));
                var draining = DrainAsync();
                await ControlAsync();
                await writers;
                await readers;
                await TakeOverDuringAReadAsync();
                _running = false;
                await draining;
                await ReadAcrossADrainAndDropAsync();
                await ReadAcrossAPartialDrainAsync();
                await CollectDecidedAsync();
                VerifyProposals();
                VerifyReads();
                VerifyFirstTouches();
                RecordCounts();
            }
            finally
            {
                foreach (var read in _storeReads)
                {
                    read.Prefix?.Dispose();
                }

                foreach (var process in _processes)
                {
                    await process.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// Passes each value the register applied since the last step to the ledger trace, which bounds each closed
        /// ledger's replicas by its recorded <c>T</c>, and records the step by which each ledger closed.
        /// </summary>
        private string? CheckMap()
        {
            for (; _checked < register.AppliedWrites.Count; _checked++)
            {
                if (LedgerMapValue.Decode(register.AppliedWrites[_checked].AsSpan()).Value is not LedgerMapValue value)
                {
                    return Invariant($"the register applied a value which is not a ledger map, at write {_checked}");
                }

                replicas.LedgerTrace.RecordCloses(_previous, value, node => nodes.Single(candidate => candidate.NodeId == node).Address);
                foreach (var ledger in value.Ledgers.Where(static ledger => !ledger.IsOpen))
                {
                    _ = _closedAt.TryAdd(ledger.Id, _steps);
                }

                _previous = value;
            }

            return null;
        }

        /// <summary>
        /// The chain checker and the version checker after every step (verification.md §3.2): each ledger drained since
        /// the last step, with each key's whole chain from <c>Origin</c>; each read since, whose chain check its record
        /// holds; and the version base of each read and first touch since, which names one state of its key (HI-I4).
        /// </summary>
        private string? CheckChainsAndVersions()
        {
            while (_drainedUnchecked.TryDequeue(out var ledger))
            {
                if (ChainViolation(ledger) is { } violation)
                {
                    return violation;
                }
            }

            for (; _readsChecked < _reads.Count; _readsChecked++)
            {
                var read = _reads[_readsChecked];
                if (read.Chain is { } chain)
                {
                    return Invariant($"HI-I4: {read.Reader}'s read of key {read.Key:x2}: the chain checker found {chain}");
                }

                if (read.VersionBase is { } versionBase && _versions.State([read.Key], versionBase, read.PrefixFold.ToString()) is { } repeated)
                {
                    return Invariant($"HI-I4: {read.Reader}'s read of key {read.Key:x2}: {repeated}");
                }
            }

            for (; _storeReadsChecked < _storeReads.Count; _storeReadsChecked++)
            {
                var read = _storeReads[_storeReadsChecked];
                if (read.Prefix is { } prefix && _versions.State([read.Key], prefix.VersionBase, read.PrefixState) is { } repeated)
                {
                    return Invariant($"HI-I4: {read.Reader}'s first touch of key {read.Key:x2}: {repeated}");
                }
            }

            return null;
        }

        private async Task<Process> StartAsync(string name, NodeId node, bool primary)
        {
            var view = await LedgerMapView.OpenAsync(register, cancellationToken);
            var transport = new ProcessTransport(replicas, name, environment.CreateRandom(name + "/backpressure"));
            _transports.Add(transport);
            return new Process(name, node, view, transport, store, primary, mapAfterStore);
        }

        private TransitionChoice Choice(Process executor, ulong epoch) =>
            new(executor.Node, [executor.Node, .. nodes.Select(static node => node.NodeId).Where(node => node != executor.Node)], Window, 0, epoch);

        private async Task<object?> ExecuteAsync(Process process, Func<LedgerMapValue, Transition> choose)
        {
            var view = await process.View.ReadAsync(cancellationToken);
            var transition = choose(view);
            var outcome = await process.Executor!.ExecuteAsync(transition, committer.CommitAsync, cancellationToken);
            Record($"{transition.Value?.GetType().Name} by {process.Name}: {outcome.Value}");
            return outcome.Value;
        }

        /// <summary>
        /// Chooses a replace, split, or merge of a random open ledger each round, which a random primary-capable process
        /// executes, becoming the primary of the ledgers it opens.
        /// </summary>
        private async Task ControlAsync()
        {
            for (var round = 0; round < workload.Transitions; round++)
            {
                await DelayAsync(60);
                var process = _random.Choose(Primaries.ToArray());
                _ = await ExecuteAsync(process, map =>
                {
                    LedgerMetadata[] open = [.. map.Ledgers.Where(static ledger => ledger.IsOpen)];
                    var target = _random.Choose(open);
                    var choice = Choice(process, map.Epoch);
                    return _random.Next(3) switch
                    {
                        1 when target.Range.Depth < MaxDepth => new Transition.Split(target.Id, choice),
                        2 when target.Range.Depth > 0 && open.FirstOrDefault(ledger => ledger.Range == target.Range.Sibling) is { Location.Value: not null } sibling =>
                            new Transition.Merge(target.Id, sibling.Id, choice),
                        _ => new Transition.Replace(target.Id, choice),
                    };
                });
            }
        }

        /// <summary>
        /// Proposes rows of random keys and kinds through whichever process serves the key's open ledger, each with a
        /// unique ID as its payload, proposing a fenced row again in the key's next ledger.
        /// </summary>
        private async Task WriteAsync()
        {
            for (var count = 0; count < workload.ProposalsPerWriter; count++)
            {
                await DelayAsync(20);
                var key = _random.Choose(Keys);
                var kind = _random.Next(10) switch
                {
                    < 7 => RowKind.Append,
                    < 9 => RowKind.Replace,
                    _ => RowKind.Delete,
                };

                while (!await ProposeAsync(key, kind))
                {
                    await DelayAsync(5);
                }
            }
        }

        /// <summary>
        /// Proposes one row, returning false when no process serves the key's ledger or its writer fenced it. A key's
        /// proposals run one at a time, as its owner's admission runs them, so each settles its key's chain before the next.
        /// </summary>
        private async Task<bool> ProposeAsync(byte key, RowKind kind)
        {
            while (!_proposing.Add(key))
            {
                // A whole millisecond, so the key's proposal in flight runs before the next look.
                await Task.Delay(TimeSpan.FromMilliseconds(1), environment.TimeProvider, cancellationToken);
            }

            try
            {
                foreach (var process in Primaries)
                {
                    var listed = process.View.Current.Find([key])!.Value.Open;
                    if (process.Registry!.TryGet(listed.Id) is { } writer)
                    {
                        return await ProposeAsync(process, writer, listed.Id, key, kind);
                    }
                }

                return false;
            }
            finally
            {
                _ = _proposing.Remove(key);
            }
        }

        private async Task<bool> ProposeAsync(Process process, LedgerWriter writer, LedgerId ledger, byte key, RowKind kind)
        {
            // The ledger trace bounds each replica's contiguous tail by its writer's completed prefix.
            if (_traced.Add(ledger))
            {
                replicas.LedgerTrace.TraceWriter(writer, process.Name, ledger);
            }

            var proposal = new Proposal(++_nextId, key, kind, ledger, ++_steps);
            _proposals.Add(proposal);
            using (var payload = ArcBuffer.Wrap(BitConverter.GetBytes(proposal.Id)))
            using (var row = new Row([key], kind, payload))
            {
                // The batch is written once its creation's turn comes, so the row lives until the proposal took its bytes.
                // An ambiguous outcome is decided by the drain which preceded the ledger's drop.
                proposal.Outcome = await _chains.ProposeAsync(writer, [row], () => _decided.TryGetValue(ledger, out var decided) && decided.Rows.Any(decidedRow => decidedRow.Id == proposal.Id), cancellationToken);
            }

            proposal.AnsweredAt = ++_steps;
            var fenced = proposal.Outcome.TryGetValue(out ProposalOutcome.Fenced _);
            _fenced += fenced ? 1 : 0;
            return !fenced;
        }

        /// <summary>Reads histories and first touches of random keys through the process's history reader, recording each.</summary>
        private async Task ReadAsync(Process process)
        {
            for (var count = 0; count < workload.ReadsPerReader; count++)
            {
                await DelayAsync(25);
                var key = _random.Choose(Keys);
                if (_random.Chance(0.3))
                {
                    await ReadStoreAsync(process, key);
                }
                else
                {
                    _ = await ReadHistoryAsync(process, key);
                }
            }
        }

        private async Task ReadStoreAsync(Process process, byte key)
        {
            using var answer = await process.Archive.ReadStoreAsync(new[] { key }, cancellationToken);
            _storeReads.Add(new StoreReadRecord(process.Name, key, answer.Map, answer.Prefix?.Retain()));
        }

        private async Task<ArchiveReadRecord> ReadHistoryAsync(Process process, byte key)
        {
            var calledAt = ++_steps;
            using var history = await process.Archive.ReadAsync(new[] { key }, cancellationToken);
            var rows = RowsOf(key, history.Rows);
            _overlaps += rows.Count(static row => row.Held);
            var chain = TokenChainChecker.Check(store, history.Prefix?.Token ?? default, [.. rows.Select(static row => row.Chain)]);
            var record = new ArchiveReadRecord(process.Name, key, calledAt, ++_steps, rows, history.Through.ToDictionary(), ArchiveOracle.Fold(history.Prefix, history.Rows), chain, history.Linearizable)
            {
                VersionBase = history.Prefix?.VersionBase,
                PrefixFold = ArchiveFold.Of(history.Prefix),
            };
            _reads.Add(record);
            return record;
        }

        /// <summary>
        /// Moves a key's primary to the other process while the first reads it: the first serves the key's open ledger
        /// and reads it while the second replaces the ledger. The read is checked as every other is; afterwards the first
        /// process's reads of the key are not linearizable, and the second's are.
        /// </summary>
        private async Task TakeOverDuringAReadAsync()
        {
            var key = _random.Choose(Keys);
            var (first, second) = (_processes[0], _processes[1]);
            _ = await ExecuteAsync(first, map => new Transition.Replace(map.Find([key])!.Value.Open.Id, Choice(first, map.Epoch)));
            while (!await ProposeAsync(key, RowKind.Append))
            {
                await DelayAsync(5);
            }

            var reading = ReadHistoryAsync(first, key);
            _ = await ExecuteAsync(second, map => new Transition.Replace(map.Find([key])!.Value.Open.Id, Choice(second, map.Epoch)));
            var during = await reading;
            Record($"takeover during a read of key {key:x2}: linearizable {during.Linearizable}");

            Assert.False((await ReadHistoryAsync(first, key)).Linearizable, "a read in the process the primary left reported itself linearizable");
            Assert.True((await ReadHistoryAsync(second, key)).Linearizable, "a read in the new primary's process did not report itself linearizable");
        }

        /// <summary>
        /// archive.md §10's read-order schedule: a key's ledger closes with rows of it, and a read in the drainer's process,
        /// once the store answered it, meets the ledger's drain and drop and a refresh of its view before it scans. The
        /// reader, which took the map first, meets the drop and reads again; the mutant, which takes the map after the
        /// store, misses the drained rows.
        /// </summary>
        private async Task ReadAcrossADrainAndDropAsync()
        {
            var key = _random.Choose(Keys);
            var primary = _processes[0];
            while (!await ProposeAsync(key, RowKind.Append))
            {
                await DelayAsync(5);
            }

            var closed = await ExecuteAsync(primary, map => new Transition.Replace(map.Find([key])!.Value.Open.Id, Choice(primary, map.Epoch)));
            Assert.IsType<LedgerMapCommitOutcome.Applied>(closed);
            var ledger = (await Drainer.View.ReadAsync(cancellationToken)).Ledgers.Single(listed => !listed.IsOpen && listed.Range.Contains([key]));
            store.AfterNextRead = async () =>
            {
                await DrainAndDropAsync(ledger);
                await ReconcileAsync();
            };

            var read = await ReadHistoryAsync(Drainer, key);
            Record($"a read across s{ledger.Id}'s drain and drop answered {read.Rows.Length} rows");
        }

        /// <summary>
        /// Takes the overlap path by construction, with every fault off: a key's ledger closes with a row of the key, its
        /// drain writes every row into the store, and a read in the drainer's process, whose map still lists the ledger,
        /// scans it from slot 0 and merges the rows both the store and the ledger hold, before the ledger drops.
        /// </summary>
        private async Task ReadAcrossAPartialDrainAsync()
        {
            store.StaleRate = 0;
            foreach (var transport in _transports)
            {
                transport.SaturationRate = 0;
            }

            var key = _random.Choose(Keys);
            var primary = _processes[0];
            while (!await ProposeAsync(key, RowKind.Append))
            {
                await DelayAsync(5);
            }

            var closed = await ExecuteAsync(primary, map => new Transition.Replace(map.Find([key])!.Value.Open.Id, Choice(primary, map.Epoch)));
            Assert.IsType<LedgerMapCommitOutcome.Applied>(closed);
            var ledger = (await Drainer.View.ReadAsync(cancellationToken)).Ledgers.Single(listed => !listed.IsOpen && listed.Range.Contains([key]));
            var overlaps = _overlaps;
            await DrainAndDropAsync(ledger, readKey: key);
            Assert.True(_overlaps > overlaps, Invariant($"a read of key {key:x2} across s{ledger.Id}'s drain before its drop merged no row both held"));
            await ReconcileAsync();
        }

        /// <summary>
        /// Drains each closed ledger whose lineage predecessors dropped into the store, reporting <c>T</c> or a lower bound,
        /// drops it, and has every node drop its replicas, until the run ends and no closed ledger remains.
        /// </summary>
        private async Task DrainAsync()
        {
            while (true)
            {
                await DelayAsync(30);
                var map = await Drainer.View.ReadAsync(cancellationToken);
                LedgerMetadata[] droppable = [.. map.Ledgers.Where(ledger => !ledger.IsOpen && !map.Ledgers.Any(lower => lower.Id < ledger.Id && lower.Range.Overlaps(ledger.Range)))];
                if (droppable.Length == 0 && !_running)
                {
                    return;
                }

                foreach (var ledger in droppable)
                {
                    await DrainAndDropAsync(ledger, readBeforeDrop: true);
                }

                if (droppable.Length > 0)
                {
                    await ReconcileAsync();
                }
            }
        }

        /// <summary>
        /// Drains the closed <paramref name="ledger"/>'s segments into the store, in order, and drops it, once the chain
        /// checker and the version checker judged its rows with each key's whole chain. With
        /// <paramref name="readBeforeDrop"/>, it sometimes drains only its first segments, and a process reads a key of the
        /// ledger's range, whose map still lists the ledger, so the read meets rows the store holds in a listed ledger; with
        /// <paramref name="readKey"/>, the drainer's process reads that key once every segment drained, before the drop.
        /// </summary>
        private async Task DrainAndDropAsync(LedgerMetadata ledger, bool readBeforeDrop = false, byte? readKey = null)
        {
            var end = ((LedgerStatus.Closed)ledger.Status.Value).T;
            var segments = await ScanAsync(Drainer, ledger, end);
            try
            {
                _decided[ledger.Id] = new DecidedLedger([.. segments.SelectMany(static segment => RowsOf(segment.ToArray()))], end);
                _drainedUnchecked.Enqueue(ledger.Id);
                var first = readBeforeDrop && segments.Count > 1 && _random.Chance(0.5) ? 1 + _random.Next(segments.Count - 1) : segments.Count;
                foreach (var segment in segments.Take(first))
                {
                    KeyedRow.DrainByKey(CollectionsMarshal.AsSpan(segment), (key, rows) => store.Drain(key, rows));
                }

                if (first < segments.Count && segments.Take(first).SelectMany(static segment => segment).Select(static row => row.Key[0]).Distinct().ToArray() is [_, ..] keys)
                {
                    _ = await ReadHistoryAsync(_random.Choose(_processes), _random.Choose(keys));
                }

                foreach (var segment in segments.Skip(first))
                {
                    KeyedRow.DrainByKey(CollectionsMarshal.AsSpan(segment), (key, rows) => store.Drain(key, rows));
                }

                if (readKey is { } read)
                {
                    _ = await ReadHistoryAsync(Drainer, read);
                }
            }
            finally
            {
                foreach (var segment in segments)
                {
                    KeyedRow.Release(segment);
                }
            }

            Assert.IsType<LedgerMapCommitOutcome.Applied>((await committer.CommitAsync(new LedgerMapChange.Drop(ledger.Id), cancellationToken)).Value);
            Record($"s{ledger.Id} drained below {end} and dropped");
        }

        /// <summary>
        /// The chain checker and the version checker over the keys of <paramref name="ledger"/>'s rows: each key's rows of
        /// every drained ledger, from its first, replay its chain from <c>Origin</c>, and no version of a key repeats (HI-I4).
        /// </summary>
        /// <returns>The first violation, in words, or null.</returns>
        private string? ChainViolation(LedgerId ledger)
        {
            foreach (var key in _decided[ledger].Rows.Select(static row => row.Key).Distinct())
            {
                var rows = _decided.Values.SelectMany(static decided => decided.Rows).Where(row => row.Key == key).OrderBy(static row => row.Tag).ToArray();
                if (TokenChainChecker.Check(store, default, [.. rows.Select(static row => row.Chain)]) is { } violation)
                {
                    return Invariant($"the chain checker found key {key:x2}'s drained rows break their chain: {violation}");
                }
            }

            foreach (var row in _decided[ledger].Rows)
            {
                if (_versions.Version([row.Key], row.Version) is { } repeated)
                {
                    return Invariant($"HI-I4: the version checker found {repeated}");
                }
            }

            return null;
        }

        /// <summary>Reads the drainer's view, and has every node drop the replicas it revokes.</summary>
        private async Task ReconcileAsync()
        {
            _ = await Drainer.View.ReadAsync(cancellationToken);
            foreach (var node in nodes)
            {
                _ = await new ReplicaReconciler(replicas.Node(node.NodeId), Drainer.View).ReconcileAsync(cancellationToken);
            }
        }

        /// <summary>
        /// The reference's remaining ledgers: each listed ledger which no drain read, to its <c>T</c> or, open, to the tail
        /// its primary's process confirms, which covers every proposal answered durable (ledgers.md §2.5).
        /// </summary>
        private async Task CollectDecidedAsync()
        {
            var map = await _processes[0].View.ReadAsync(cancellationToken);
            foreach (var process in Primaries)
            {
                _ = await process.View.ReadAsync(cancellationToken);
                await process.Registry!.ReconciledAsync(map.Epoch, cancellationToken);
            }

            foreach (var ledger in map.Ledgers.Where(ledger => !_decided.ContainsKey(ledger.Id)))
            {
                Slot end;
                var reader = _processes[0];
                if (ledger.Status.Value is LedgerStatus.Closed closed)
                {
                    end = closed.T;
                }
                else
                {
                    reader = Primaries.Single(process => process.Registry!.TryGet(ledger.Id) is not null);
                    var tail = Assert.IsType<LedgerTail>((await reader.Reader.ReadTailAsync(ledger, cancellationToken)).Value);
                    Assert.True(tail.ServedHere, Invariant($"s{ledger.Id}'s seal check in its primary's process was not served there"));
                    end = tail.ContiguousTail;
                }

                var segments = await ScanAsync(reader, ledger, end);
                _decided[ledger.Id] = new DecidedLedger([.. segments.SelectMany(static segment => RowsOf(segment.ToArray()))], end);
                foreach (var segment in segments)
                {
                    KeyedRow.Release(segment);
                }
            }
        }

        /// <summary>Scans <paramref name="ledger"/> below <paramref name="end"/>, resuming after backpressure: its rows, by segment, owned.</summary>
        private async Task<List<List<KeyedRow>>> ScanAsync(Process process, LedgerMetadata ledger, Slot end)
        {
            var segments = new List<List<KeyedRow>>();
            using var view = new LedgerSegmentView(ledger.Id);
            using var bodies = new ArcBufferWriter();
            LedgerSegmentId? current = null;
            var from = Slot.Zero;
            while (from < end)
            {
                await foreach (var item in process.Reader.ScanAsync(ledger, from, end, view, cancellationToken: cancellationToken))
                {
                    var read = item.Read;
                    if (!read.TryGetValue(out LedgerRead.Data data))
                    {
                        Assert.True(read.TryGetValue(out ReplicaWriteOutcome.Backpressure? _), Invariant($"{process.Name}'s scan of s{ledger.Id} read {read}"));
                        await DelayAsync(5);
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

            return segments;
        }

        /// <summary>
        /// Each reference row appears once and matches its proposal, and each durable proposal is in the reference at its
        /// slot: the reference holds every acknowledged row.
        /// </summary>
        private void VerifyProposals()
        {
            var byId = _proposals.ToDictionary(static proposal => proposal.Id);
            var all = _decided.Values.SelectMany(static ledger => ledger.Rows).ToArray();
            Assert.Equal(all.Length, all.Select(static row => row.Id).Distinct().Count());
            foreach (var row in all)
            {
                var proposal = byId[row.Id];
                Assert.Equal((proposal.Key, proposal.Kind, proposal.Ledger), (row.Key, row.Kind, row.Tag.Ledger));
            }

            var ids = all.ToDictionary(static row => row.Id);
            foreach (var proposal in _proposals)
            {
                if (proposal.Outcome.TryGetValue(out ProposalOutcome.Durable durable))
                {
                    Assert.True(ids.TryGetValue(proposal.Id, out var row) && row.Tag.Slot == durable.Slot, Invariant($"proposal {proposal.Id}, durable at s{proposal.Ledger}:{durable.Slot}, is not in the reference there"));
                }
            }
        }

        /// <summary>
        /// Each read's rows are the reference's rows of the ledgers it read, below their decided ends, each held or not; its
        /// prefix and rows not held fold to the reference over its cut, with those rows and every row of an earlier ledger it
        /// did not read; the chain checker passes its rows; and over that cut, no row was proposed after the read ended, every
        /// row of a ledger closed before it began is in it, and, for a linearizable read, every row answered durable before
        /// it began.
        /// </summary>
        private void VerifyReads()
        {
            var byId = _proposals.ToDictionary(static proposal => proposal.Id);
            foreach (var read in _reads)
            {
                foreach (var (ledger, end) in read.Through)
                {
                    Assert.True(end <= _decided[ledger].End, Invariant($"{read.Reader}'s read of key {read.Key:x2} reached s{ledger}:{end}, past its decided end {_decided[ledger].End}"));
                }

                var reference = Reference(read.Key);
                var highest = read.Through.Count == 0 ? (LedgerId?)null : read.Through.Keys.Max();
                List<ReferenceRow> cut = [.. reference.Where(row => read.Through.TryGetValue(row.Tag.Ledger, out var end) ? row.Tag.Slot < end : row.Tag.Ledger < highest)];
                ReferenceRow[] live = [.. cut.Where(row => read.Through.ContainsKey(row.Tag.Ledger))];
                Assert.True(
                    live.Select(static row => (row.Tag, row.Kind)).SequenceEqual(read.Rows.Select(static row => (row.Tag, row.Kind))),
                    Invariant($"{read.Reader}'s read of key {read.Key:x2} between steps {read.CalledAt} and {read.AnsweredAt} scanned [{string.Join(", ", read.Rows)}], not the live rows [{string.Join(", ", live)}]"));
                var expected = ArchiveOracle.Fold(cut, static row => row.Kind, static row => row.Body);
                Assert.True(
                    expected.SameAs(read.Fold),
                    Invariant($"{read.Reader}'s read of key {read.Key:x2} between steps {read.CalledAt} and {read.AnsweredAt} folds to {read.Fold}, not {expected}, which the reference over its cut folds to"));
                Assert.True(read.Chain is null, Invariant($"{read.Reader}'s read of key {read.Key:x2}: the chain checker found {read.Chain}"));

                var proposedAfter = cut.Where(row => byId[row.Id].CalledAt > read.AnsweredAt).ToArray();
                Assert.True(proposedAfter.Length == 0, Invariant($"{read.Reader}'s read of key {read.Key:x2} holds rows proposed after it ended: [{string.Join(", ", proposedAfter)}]"));
                var missed = reference.Where(row => !cut.Contains(row)
                    && (_closedAt.TryGetValue(row.Tag.Ledger, out var closedAt) && closedAt < read.CalledAt
                        || read.Linearizable && byId[row.Id].AnsweredAt < read.CalledAt && byId[row.Id].Outcome.TryGetValue(out ProposalOutcome.Durable _))).ToArray();
                Assert.True(missed.Length == 0, Invariant($"{read.Reader}'s read of key {read.Key:x2} (linearizable {read.Linearizable}) misses rows decided before it began: [{string.Join(", ", missed)}]"));
            }
        }

        /// <summary>
        /// Each first touch's prefix, merged by <c>Holds</c> with the reference rows of the ledgers its map value lists, folds
        /// to the reference's rows below the value's next ID: the ledgers it no longer lists hold no row it lacks.
        /// </summary>
        private void VerifyFirstTouches()
        {
            foreach (var read in _storeReads)
            {
                var reference = Reference(read.Key);
                var live = reference.Where(row => read.Map.Get(row.Tag.Ledger) is not null).Select(row => ToArchiveRow(row)).ToArray();
                try
                {
                    var outcome = ArchiveMerge.Merge(store, read.Prefix, live, new Dictionary<LedgerId, Slot>(), openLedger: null, linearizable: false);
                    Assert.True(outcome.Value is ArchiveRead, Invariant($"{read.Reader}'s first touch of key {read.Key:x2} at epoch {read.Map.Epoch} merges with the reference's live rows to {outcome.Value}"));
                    using var merged = (ArchiveRead)outcome.Value;
                    var composed = ArchiveOracle.Fold(merged.Prefix, merged.Rows);
                    var expected = ArchiveOracle.Fold(reference.Where(row => row.Tag.Ledger < read.Map.NextLedgerId), static row => row.Kind, static row => row.Body);
                    Assert.True(
                        expected.SameAs(composed),
                        Invariant($"{read.Reader}'s first touch of key {read.Key:x2} at epoch {read.Map.Epoch} composes {composed}, not {expected}: the ledgers it no longer lists hold rows it lacks"));
                }
                finally
                {
                    ArchiveMerge.Release(live);
                }
            }
        }

        private void RecordCounts()
        {
            var counts = _processes.Select(static process => process.Archive.Counts).ToArray();
            Record($"{CountsEntry}{counts.Sum(static c => c.Stale.Value)} {counts.Sum(static c => c.Dropped.Value)} {counts.Sum(static c => c.Sealed.Value)} {counts.Sum(static c => c.Backpressured.Value)} {_overlaps} {_fenced}");
            Record($"history reads match the reference: {_reads.Count} reads and {_storeReads.Count} first touches over {_proposals.Count} proposals");
        }

        private List<ReferenceRow> Reference(byte key) =>
            [.. _decided.Values.SelectMany(static ledger => ledger.Rows).Where(row => row.Key == key).OrderBy(static row => row.Tag)];

        private static ReferenceRow[] RowsOf(ReadOnlySpan<KeyedRow> rows)
        {
            var result = new ReferenceRow[rows.Length];
            for (var index = 0; index < rows.Length; index++)
            {
                result[index] = RowOf(rows[index].Key[0], rows[index].Row);
            }

            return result;
        }

        private static ReferenceRow[] RowsOf(byte key, ReadOnlySpan<ArchiveRow> rows)
        {
            var result = new ReferenceRow[rows.Length];
            for (var index = 0; index < rows.Length; index++)
            {
                result[index] = RowOf(key, rows[index]);
            }

            return result;
        }

        private static ReferenceRow RowOf(byte key, in ArchiveRow row) =>
            new(key, row.Tag, row.Kind, row.IsHeld ? -1 : BitConverter.ToInt32(row.Body.ToArray()), row.Segment, row.Token, row.Version, row.IsHeld);

        private static ArchiveRow ToArchiveRow(ReferenceRow row)
        {
            using var body = ArcBuffer.Wrap(row.Body);
            return ArchiveRow.Live(row.Tag, row.Kind, row.Version, row.Segment, row.Token, body);
        }

        private Task DelayAsync(int maximumMilliseconds) =>
            Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(maximumMilliseconds)), environment.TimeProvider, cancellationToken);

        private void Record(FormattableString entry) => environment.Trace.Record(Invariant(entry));
    }

    /// <summary>
    /// A process's transport: each replica it opens is the process's own, which the node serves as the process's; a
    /// replica read of a slot answers backpressure from every replica, now and then, so the quorum read answers it.
    /// </summary>
    private sealed class ProcessTransport(SimulatedLedgerReplicaTransport replicas, string client, SimulationRandom random) : ILedgerReplicaTransport
    {
        private readonly Dictionary<(LedgerId, Slot), int> _saturated = [];

        /// <summary>Gets or sets the probability that a slot's reads answer backpressure from every replica.</summary>
        public double SaturationRate { get; set; } = 0.03;

        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            new Replica(replicas.Open(node, ledger, options, client), ledger, this);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            replicas.HeartbeatAsync(node, ledgers, cancellationToken);

        /// <summary>Whether this replica's read of the slot answers backpressure: once for each of a read's replicas.</summary>
        private bool Saturated(LedgerId ledger, Slot slot)
        {
            if (!_saturated.TryGetValue((ledger, slot), out var remaining))
            {
                remaining = random.Chance(SaturationRate) ? 3 : 0;
            }

            _saturated[(ledger, slot)] = Math.Max(remaining - 1, 0);
            return remaining > 0;
        }

        private sealed class Replica(ILedgerReplica inner, LedgerId ledger, ProcessTransport transport) : ILedgerReplica
        {
            public ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
                inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

            public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

            public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                transport.Saturated(ledger, slot)
                    ? ValueTask.FromResult(new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance))
                    : inner.ReadAsync(slot, contiguousTail, cancellationToken);

            public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
                inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
