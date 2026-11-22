using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Journals under deterministic simulation (journals.md §10; JS-I1 to JS-I7, JS-I10): two processes, each able to serve
/// primaries, on two of three LedgerStore nodes, each run a <see cref="JournalStore"/> over its writer registry and a
/// simulated archive store with per-generation limits. Concurrent writers append, replace, and delete four journals at the
/// versions they last saw, routed to the primary the map names and sent again after <c>Moved</c> or <c>Fenced</c>; readers
/// read them; an expiry scan expires deleted journals; and control rounds replace, split, and merge ledgers, which moves
/// primaries between the processes, while a drainer drains each closed ledger into the store and drops it, so loads
/// rebase, and a controller replaces each ledger a process lists unserved. Meanwhile a fault plan fires each fault of
/// verification.md §7.3 the composition meets beyond one ledger's quorum, and heals it: a journal process crashes with its
/// node and restarts, a node is partitioned, another paused, messages and journal answers are lost and requests
/// duplicated, a restarted node's media is damaged, the ledger map register leaves writes' outcomes unknown, two
/// committers race one transition, and an executor is interrupted once it submits. Scripted steps, once the faults heal,
/// take each rare path by construction, and each asserts it took its path: a takeover which
/// commits while a read's store read is held; a key's write across a segment's end; a <c>Replace</c>, which opens a
/// generation, and then a write which conflicts with an entry of its key in flight; a <c>Replace</c> and then appends to
/// the store's limit, the last two at once, so the refused one is decided once the other lands; two rebases of one
/// journal, each written at its rebased version; a <c>Delete</c> expired once its retention passes; a split and a
/// merge; and a read while every replica is out of reach, which fails as the store's unavailability.
/// </summary>
/// <remarks>
/// <para>
/// The oracles, the white-box ones first. Each rejection reports the version the reference gives through the position its
/// admission decided from, a rebase only once every earlier row of its key dropped and to the store's version base, and
/// was loaded again first when its key had no recorded segment in its ledger and nothing in flight (JS-I1); its entry
/// applies to no state a prefix of its key's in-flight entries leaves. Each <c>ReplaceRequired</c> was decided with nothing
/// of its key in flight, and the store's limits refused one of its two candidate writes (JS-I4). Each read answers the fold
/// of the reference over its cut, which holds every row acknowledged before it began and lies below each ledger's decided
/// end when it answered, at the version of its newest live row or, with none, the rebase to the store's version base once
/// every earlier row dropped (JS-I3). Each accepted write is exactly one decided row, at its answered version, and a write
/// answered with no effect is none (JS-I2, JS-I4). After every step, and over the whole reference at the end, the chain
/// checker replays each key's token chain, a rebase only after a dropped ledger's row, and the version checker finds no
/// version of a key repeated and no version any answer gives naming two states, each identified by its newest reference row
/// (HI-I4, JS-I10). Then every client history, per journal, is linearizable against <see cref="JournalModel"/>, which
/// relaxes a rejection's report only for a deposed owner, whose ledger the map no longer listed open. Every write and read
/// is answered once faults and transitions stop, and no admission holds a waiting entry at the end (progress).
/// </para>
/// <para>
/// The fault plan fires each fault once, in an order the seed draws, while the clients, the control rounds, and the
/// drainer run, and each fault records the evidence that it took its path, which <see cref="TheCorpusFiresEveryFault"/>
/// requires. A journal process crashes with the node it runs on, as a process or an operating system, while a write is in
/// flight on it: it answers nothing more, its executions stop, and its memory is released once its proposals in flight are
/// decided, so every call in flight on it ends unanswered, a write <c>Ambiguous</c> and unbounded in the history, as is an
/// <c>Expired</c> its expiry scan proposed, and a read is sent again, since it has no effect. The node restarts from what
/// its disk kept, and a new process starts in the old one's place as production starts one: it opens the map, reconciles
/// its node's replicas, replaces the open ledgers still named under the crashed process's claims, and loads each key again
/// from the ledgers and the store as it admits it; its expiry scan's proposals are recorded as its predecessor's were.
/// While the faults run, a call or an expiry proposal the faults leave without its quorum ends unanswered, and a transition
/// or a drop which fails as they explain is answered by the map view or submitted again; once they heal, any failure fails
/// the run. The processes
/// send their replica requests from one client node, so a partition isolates a LedgerStore node from every process at
/// once, not one process from one node. A restart here holds no warm state across the crash: warm state's claims and key
/// indexes, and restarts which keep them, come with row 5.2.
/// </para>
/// <para>
/// The reference history of a key is its decided rows in tag order: each drained ledger's rows to <c>T</c>, and at the end
/// each listed ledger's to its <c>T</c> or, open, to the tail its primary's process confirms. A final read of every journal
/// is its whole reference's fold. A handoff, in which a primary hands its own ledger to another, is not expressible here:
/// a transition's executor always becomes the primary of the ledgers it opens, so a takeover by the other process stands
/// for both.
/// </para>
/// </remarks>
public sealed class JournalSimulationTests
{
    private const int FirstSeed = 1;

    // A seed costs about forty-five thousand steps, and the scripted steps take every rare path, and the fault plan every
    // fault, on each seed by construction, so every change runs one, the replay check's smaller run a second, and
    // exploration runs more through CLOG_SIM_SEEDS (test/Durable.Storage/docs/spec/verification.md §7.4, §12).
    private const int SeedCount = 1;
    private const int ReplaySeed = 2;
    private const int MutantSeed = 1;
    private const int Writers = 4;
    private const int MaxDepth = 2;
    private const int Window = 4;

    // The appends one generation of a key holds in the store, so a scripted run of them reaches ReplaceRequired.
    private const int MaxAppends = 4;
    private const string CountsEntry = "journal counts: ";
    private const string FaultEntry = "fault fired: ";
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan Retention = TimeSpan.FromMilliseconds(50);

    // The bound within which the fault plan fires every fault and heals it, as the progress checks bound the clients.
    private static readonly TimeSpan FaultBound = TimeSpan.FromMinutes(1);

    // The theory and the corpus check share each seed's run, so the corpus runs once.
    private static readonly ConcurrentDictionary<int, Lazy<ClusterSimulationRun>> Runs = new();

    /// <summary>Gets the seed corpus.</summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    /// <summary>Every oracle holds on each seed of the corpus.</summary>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void JournalsHoldTheirContractUnderSimulation(int seed) => _ = RunSeed(seed);

    /// <summary>
    /// On each seed of the corpus, writes are accepted, rejected, answered <c>ReplaceRequired</c>, and sent again after
    /// <c>Moved</c>; deleted journals expire; reads rebase; an entry is rejected while entries of its key are in flight; and
    /// ledgers split and merge.
    /// </summary>
    [Fact]
    public void EachSeedExercisesEveryOutcomeARebaseASplitAndAMerge()
    {
        string[] names = ["Accepted", "Rejected", "ReplaceRequired", "Moved", "Expired", "Rebased", "Rejected in flight", "Split", "Merge"];
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, SeedCount))
        {
            var counts = RunSeed(seed).Trace.Single(static entry => entry.Contains(CountsEntry, StringComparison.Ordinal));
            var values = counts[(counts.IndexOf(CountsEntry, StringComparison.Ordinal) + CountsEntry.Length)..].Split(' ');
            var missing = names.Where((_, index) => int.Parse(values[index], CultureInfo.InvariantCulture) == 0).ToArray();
            Assert.True(missing.Length == 0, $"Seed {seed} never exercised: {string.Join(", ", missing)}.");
        }
    }

    /// <summary>
    /// On each seed of the corpus, every fault of the plan takes its path (verification.md §7.3): a journal process and
    /// its node crash with a call in flight, which ends unanswered, and restart and serve again; a node is partitioned and
    /// healed, and another paused; messages and journal answers are lost and requests duplicated; a restarted node's
    /// media is damaged; the ledger map register leaves writes' outcomes unknown, one which applied and one which never
    /// did; two committers race one transition, which one applies and the other finds superseded; and an executor is
    /// interrupted after it submits, and another's transition applies or finds it superseded.
    /// </summary>
    [Fact]
    public void TheCorpusFiresEveryFault()
    {
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, SeedCount))
        {
            var trace = RunSeed(seed).Trace;
            string[] missing = [.. Enum.GetNames<Fault>().Where(fault => !trace.Any(entry => entry.Contains(Invariant($"{FaultEntry}{fault}:"), StringComparison.Ordinal)))];
            Assert.True(missing.Length == 0, $"Seed {seed} never took the path of: {string.Join(", ", missing)}.");
        }
    }

    /// <summary>
    /// Admission's two chain mutants (journals.md §10) fail their own checks: a token not rolled at a segment change breaks
    /// the chain at the scripted write across a segment's end (HI-I4), and warm state's apply refuses a defining row whose
    /// version is off the fold, a defect, before the chain checks see it.
    /// </summary>
    /// <param name="mutant">The chain mutant.</param>
    /// <param name="check">A fragment of the failure its own check reports.</param>
    [Theory]
    [InlineData(nameof(LedgerAdmission.ChainMutant.TokenNotRolled), "the chain checker found")]
    [InlineData(nameof(LedgerAdmission.ChainMutant.VersionOffTheFold), "the defect JournalsWarmState")]
    public void EachChainMutantFailsItsOwnCheck(string mutant, string check) =>
        Assert.Contains(check, FailureOf(new Mutation { Chain = Enum.Parse<LedgerAdmission.ChainMutant>(mutant), Steps = Steps.AcrossASegmentsEnd }), StringComparison.Ordinal);

    /// <summary>
    /// The mutant which rejects at once an entry its key's in-flight entries leave waiting fails the white-box rejection
    /// oracle on the scripted write which conflicts with an entry in flight (JS-I1), which runs before the model.
    /// </summary>
    [Fact]
    public void RejectingAWaitingEntryFailsTheRejectionOracle() =>
        Assert.Contains("JS-I1", FailureOf(new Mutation { Rejection = LedgerAdmission.RejectionMutant.RejectsWaiting, Steps = Steps.Conflict }), StringComparison.Ordinal);

    /// <summary>
    /// The mutant which never decides a key's waiting entries again fails the progress check: the scripted conflicting
    /// write is never answered once its key's in-flight entry lands.
    /// </summary>
    [Fact]
    public void NeverDecidingAWaitingEntryFailsTheProgressCheck() =>
        Assert.Contains("progress: three writes of journal", FailureOf(new Mutation { Rejection = LedgerAdmission.RejectionMutant.NeverDecidesWaitingAgain, Steps = Steps.Conflict }), StringComparison.Ordinal);

    /// <summary>
    /// Each oracle fails a mutant of its own property (journals.md §10): a rebase to the version base an earlier load saw
    /// names two states by one version (the version checker, JS-I10); a read which rebases with live rows answers a version
    /// other than its newest live row's (the read-bound oracle, JS-I3); a rejection which does not load its warm key again
    /// first rejects the scripted write at its rebased version (the rejection oracle, JS-I1); and <c>ReplaceRequired</c>
    /// with an entry in flight, or one append early, fails the <c>ReplaceRequired</c> oracle on the scripted appends. A
    /// mutant of the oracle itself, which records every rejection deposed so the model would judge each only as a hint,
    /// fails the conflict step's check that its owner's rejections are judged strictly.
    /// </summary>
    /// <param name="mutant">The mutant.</param>
    /// <param name="check">A fragment of the failure its oracle reports.</param>
    [Theory]
    [InlineData("rebase-on-stale-token", "JS-I10: the version checker found")]
    [InlineData("read-rebase-with-live-rows", "not its newest live row's")]
    [InlineData("rejects-without-reload", "without loading it again")]
    [InlineData("replace-required-with-entries-in-flight", "entries of its key were in flight")]
    [InlineData("replace-required-one-early", "both candidate writes admit")]
    [InlineData("deposed-always", "was recorded as deposed")]
    public void EachOracleFailsItsMutant(string mutant, string check)
    {
        var mutation = mutant switch
        {
            "rebase-on-stale-token" => new Mutation { Load = JournalLoadMutant.RebaseOnStaleToken, Steps = Steps.Rebase },
            "read-rebase-with-live-rows" => new Mutation { Read = JournalLoadMutant.RebaseWithLiveRows, Steps = Steps.Rebase },
            "rejects-without-reload" => new Mutation { Reload = LedgerAdmission.ReloadMutant.RejectsWithoutReload, Steps = Steps.Rebase },
            "replace-required-with-entries-in-flight" => new Mutation { ReplaceRequired = LedgerAdmission.ReplaceRequiredMutant.AnswersWithEntriesInFlight, Steps = Steps.AppendToTheLimit },
            "replace-required-one-early" => new Mutation { ReplaceRequired = LedgerAdmission.ReplaceRequiredMutant.AnswersWhenBothAdmit, Steps = Steps.AppendToTheLimit },
            "deposed-always" => new Mutation { DeposedAlways = true, Steps = Steps.Conflict },
            _ => throw new ArgumentOutOfRangeException(nameof(mutant)),
        };

        Assert.Contains(check, FailureOf(mutation), StringComparison.Ordinal);
    }

    /// <summary>
    /// The control runs of the mutants' runs: each scripted step a mutant runs alone takes its path and passes every oracle
    /// without its mutant, within the mutant workload's progress bound, so each mutant's failure is its own.
    /// </summary>
    /// <param name="step">The scripted step.</param>
    [Theory]
    [InlineData(nameof(Steps.AcrossASegmentsEnd))]
    [InlineData(nameof(Steps.Conflict))]
    [InlineData(nameof(Steps.AppendToTheLimit))]
    [InlineData(nameof(Steps.Rebase))]
    public void EachMutantsStepAlonePassesWithoutItsMutant(string step) =>
        _ = SeededSimulation.Run(MutantSeed, environment => SimulateAsync(environment, Workload.Mutant, new Mutation { Steps = Enum.Parse<Steps>(step) }), minimize: false);

    /// <summary>The scenario replays from its seed and from its artifact.</summary>
    [Fact]
    public void TheScenarioReplaysFromItsSeed()
    {
        var run = SeededSimulation.RunAndReplay(ReplaySeed, static environment => SimulateAsync(environment, Workload.Replay, new Mutation()));

        Assert.Contains(run.Trace, entry => entry.Contains("journals hold their contract", StringComparison.Ordinal));
    }

    /// <summary>Runs the mutant workload under <paramref name="mutation"/> on the mutant seed; returns the failure, which it requires.</summary>
    private static string FailureOf(Mutation mutation) =>
        Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(MutantSeed, environment => SimulateAsync(environment, Workload.Mutant, mutation), minimize: false)).Message;
    private static ClusterSimulationRun RunSeed(int seed) =>
        Runs.GetOrAdd(seed, static seed => new Lazy<ClusterSimulationRun>(() => SeededSimulation.Run(seed, static environment => SimulateAsync(environment, Workload.Corpus, new Mutation())))).Value;

    private static async Task SimulateAsync(SimulationEnvironment environment, Workload workload, Mutation mutation)
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

        // Two committers share the register, the controllers' and the drainer's, as duplicate committers do (ledgers.md §2.6).
        await using var committer = new LedgerMapCommitter(register.Writer(0), int.MaxValue);
        await using var drainCommitter = new LedgerMapCommitter(register.Writer(1), int.MaxValue);
        using var store = new SimulatedArchiveStore(environment, "journals", maxAppends: MaxAppends, replaceRequestAppends: MaxAppends - 1) { StaleRate = 0.1 };
        await new Scenario(environment, replicas, register, nodes, [committer, drainCommitter], store, workload, mutation, cancellation.Token).RunAsync();
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The scenario's work: each writer's writes, each reader's reads, the expiry scan's expiries, the control rounds of
    /// transitions, and the bound within which, once nothing else moves, every call is answered (progress). The replay
    /// check runs a smaller workload, since determinism does not depend on its size, and a mutant runs none: its scripted
    /// step takes its path by construction, well within a mutant run's shorter bound. Each runs the fault plan, which
    /// heals before the scripted steps.
    /// </summary>
    private sealed record Workload(int WritesPerWriter, int ReadsPerReader, int Transitions, int Scans, TimeSpan Quiescence)
    {
        public static readonly Workload Corpus = new(12, 8, 4, 8, TimeSpan.FromMinutes(1));
        public static readonly Workload Replay = new(3, 2, 1, 2, TimeSpan.FromMinutes(1));
        public static readonly Workload Mutant = new(0, 0, 0, 0, TimeSpan.FromSeconds(2));
    }

    /// <summary>The faults the fault plan fires, each once, in an order the seed draws (verification.md §7.3).</summary>
    private enum Fault
    {
        Crash,
        Partition,
        Pause,
        LostMessages,
        Damage,
        UnknownOutcomes,
        DuplicateCommitters,
        InterruptedExecutor,
    }

    /// <summary>The scripted steps a run takes: every one, except that a mutant's run takes only the step which forces its path.</summary>
    [Flags]
    private enum Steps
    {
        TakeOverDuringARead = 1,
        AcrossASegmentsEnd = 2,
        Conflict = 4,
        AppendToTheLimit = 8,
        Rebase = 16,
        DeleteAndExpire = 32,
        SplitAndMerge = 64,
        EveryReplicaOutOfReach = 128,
        All = 255,
    }

    /// <summary>The mutants the processes' stores, their loads and reads, and their admissions run as.</summary>
    private sealed record Mutation
    {
        public LedgerAdmission.ChainMutant Chain { get; init; }

        public LedgerAdmission.RejectionMutant Rejection { get; init; }

        public LedgerAdmission.ReloadMutant Reload { get; init; }

        public LedgerAdmission.ReplaceRequiredMutant ReplaceRequired { get; init; }

        public JournalLoadMutant Load { get; init; }

        public JournalLoadMutant Read { get; init; }

        public Steps Steps { get; init; } = Steps.All;

        /// <summary>Gets a mutant of the oracle, not of Journals: every rejection is recorded deposed, so the model would judge each only as a hint.</summary>
        public bool DeposedAlways { get; init; }
    }

    /// <summary>A decided row of a journal, as the reference holds it: its journal, tag, kind, version, token, segment, and body.</summary>
    private sealed record ReferenceRow(string Journal, Tag Tag, RowKind Kind, RowVersion Version, RowToken Token, LedgerSegmentId Segment, byte[] Body)
    {
        public ChainRow Chain => new(Tag, Segment, Kind, Body.Length, Kind == RowKind.Delete ? Body : ReadOnlyMemory<byte>.Empty, Token, Version, Held: false);

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind}@{Tag}");
    }

    /// <summary>A ledger's decided rows, as its drain or the end of the run read them, and the end it read them to.</summary>
    private sealed record DecidedLedger(ReferenceRow[] Rows, Slot End);

    /// <summary>
    /// An answered owner read: its journal, its cut, its answer, where in the run's order it was invoked and answered, how
    /// many times it was routed again after <c>Moved</c>, and the process which answered it.
    /// </summary>
    private sealed record ReadRecord(string Journal, Dictionary<LedgerId, Slot> Through, JournalVersion Version, bool Exists, byte[] Content, long InvokedAt, long AnsweredAt, int Hops, Process ServedBy);

    /// <summary>
    /// A write's answer: its journal, kind, payload (none for a <c>Delete</c> or an <c>Expired</c>), its outcome and the
    /// version it answered, and where in the run's order it was invoked and answered.
    /// </summary>
    private sealed record WriteRecord(string Journal, JournalEntryKind Kind, byte[]? Payload, object Outcome, JournalVersion? Version, long InvokedAt, long AnsweredAt);

    /// <summary>A rejection, with the ledger of the admission which decided it, whether that owner was deposed, and where in the run's order it was decided.</summary>
    private sealed record RejectionRecord(LedgerId Ledger, JournalRejection Rejection, bool Deposed, long DecidedAt);

    /// <summary>A <c>ReplaceRequired</c>, with the ledger of the admission which decided it and whether that owner was deposed.</summary>
    private sealed record ReplaceRequiredRecord(LedgerId Ledger, JournalReplaceRequired Decision, bool Deposed);

    /// <summary>A process's transport: each replica it opens is the process's own, which the node serves as the process's.</summary>
    private sealed class ClientTransport(SimulatedLedgerReplicaTransport replicas, string client) : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) => replicas.Open(node, ledger, options, client);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            replicas.HeartbeatAsync(node, ledgers, cancellationToken);
    }

    /// <summary>
    /// A process: its map view and reader; a primary-capable one also has a registry, an executor, and a Journals store. A
    /// primary-capable process crashes with its node: from then on it answers nothing, its executions stop, and its memory
    /// is released, and the node's restart starts a new process in its place.
    /// </summary>
    private sealed class Process
    {
        private readonly CancellationTokenSource _lifetime;

        public Process(string name, NodeId node, LedgerMapView view, ILedgerReplicaTransport transport, IArchiveStore store, Mutation mutation, Action<LedgerId, JournalRejection>? rejected, Action<LedgerId, JournalReplaceRequired>? replaceRequired, LedgerTrace trace, CancellationToken cancellationToken)
        {
            Name = name;
            Node = node;
            View = view;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Lifetime = _lifetime.Token;
            Reader = new LedgerReader(view, transport, SimulationEnvironment.Clock, node);
            if (rejected is null)
            {
                return;
            }

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
                ChainMutation = mutation.Chain,
                RejectionMutation = mutation.Rejection,
                ReloadMutation = mutation.Reload,
                ReplaceRequiredMutation = mutation.ReplaceRequired,
                LoadMutation = mutation.Load,
                ReadMutation = mutation.Read,
                OnRejected = rejected,
                OnReplaceRequired = replaceRequired,

                // The ledger trace bounds each replica's contiguous tail by its writer's completed prefix, from its first grant.
                OnAdmission = (ledger, writer) => trace.TraceWriter(writer, name, ledger),
            };
        }

        public string Name { get; }

        public NodeId Node { get; }

        public LedgerMapView View { get; }

        public LedgerReader Reader { get; }

        public LedgerWriterRegistry? Registry { get; }

        public TransitionExecutor? Executor { get; }

        public JournalStore? Journals { get; }

        /// <summary>Gets the token the process's own executions run under, which its crash cancels.</summary>
        public CancellationToken Lifetime { get; }

        /// <summary>Gets a value indicating whether the process crashed: it answers nothing more, and a call in flight on it ends unanswered.</summary>
        public bool Crashed { get; private set; }

        /// <summary>Gets or sets the number of client calls in flight on the process.</summary>
        public int InFlight { get; set; }

        /// <summary>
        /// Crashes the process: it answers nothing more, and its executions stop. Cancels on the simulation's thread, since
        /// <see cref="CancellationTokenSource.CancelAsync"/> would run the callbacks the code under test registered on a real
        /// thread (test/Durable.Storage/docs/spec/verification.md §7.1).
        /// </summary>
        public void Crash()
        {
            Crashed = true;
            _lifetime.Cancel();
        }

        public async ValueTask DisposeAsync()
        {
            // The store first, so its writers decide from the view what the registry's disposal would leave Ambiguous.
            if (Journals is not null)
            {
                await Journals.DisposeAsync();
            }

            if (Registry is not null)
            {
                await Registry.DisposeAsync();
            }

            await Reader.DisposeAsync();
            _lifetime.Dispose();
        }
    }

    private sealed class Scenario(
        SimulationEnvironment environment,
        SimulatedLedgerReplicaTransport replicas,
        SimulatedConditionalRegister register,
        LedgerStoreNode[] nodes,
        LedgerMapCommitter[] committers,
        SimulatedArchiveStore store,
        Workload workload,
        Mutation mutation,
        CancellationToken cancellationToken)
    {
        // The client whose calls the fault plan makes: a write a crash cuts, and the reads and writes whose messages and
        // answers are lost.
        private const int FaultClient = Writers + 12;

        private readonly SimulationRandom _random = environment.Random;
        private readonly HistoryRecorder<JournalInput, JournalOutput> _history = new(environment.TimeProvider);
        private readonly VersionChecker _versions = new();
        private readonly List<Process> _processes = [];
        private readonly List<ReadRecord> _reads = [];
        private readonly List<WriteRecord> _writes = [];
        private readonly List<RejectionRecord> _rejections = [];
        private readonly List<ReplaceRequiredRecord> _replaceRequireds = [];
        private readonly HashSet<int> _rejectionsAnswered = [];
        private readonly HashSet<int> _replaceRequiredsAnswered = [];
        private readonly Dictionary<LedgerId, DecidedLedger> _decided = [];
        private readonly Dictionary<LedgerId, long> _dropped = [];
        private readonly HashSet<LedgerId> _versionsChecked = [];
        private readonly Dictionary<string, string> _journalsByKey = [];
        private readonly Queue<LedgerId> _drainedUnchecked = new();
        private readonly List<string> _unmet = [];
        private readonly int[] _incarnations = [1, 1];

        // The release of each crashed process and the write a crash cut, which the fault plan awaits before it ends.
        private readonly List<Task> _settling = [];
        private string[] _journals = [];
        private LedgerMapValue _previous = LedgerMapValue.Empty;
        private string? _violation;
        private bool _running = true;
        private bool _faulting;
        private double _answerLossRate;
        private int _checked;
        private int _nextId;
        private long _order;
        private int _accepted;
        private int _rejected;
        private int _replaceRequired;
        private int _moved;
        private int _expired;
        private int _rebased;
        private int _splits;
        private int _merges;
        private int _lostAnswers;

        private Process Drainer => _processes[2];

        private IEnumerable<Process> Primaries => _processes.Take(2);

        /// <summary>Gets the primary-capable processes which run.</summary>
        private Process[] Live => [.. Primaries.Where(static process => !process.Crashed)];

        public async Task RunAsync()
        {
            environment.Invariants.Add("ledger-map", CheckMap);
            environment.Invariants.Add("journal-chains", CheckChainsAndVersions);
            try
            {
                _journals = ChooseJournals();
                _processes.Add(await StartAsync("process-0", nodes[0].NodeId, primary: true));
                _processes.Add(await StartAsync("process-1", nodes[1].NodeId, primary: true));
                _processes.Add(await StartAsync("drainer", nodes[2].NodeId, primary: false));
                while (await ExecuteAsync(_processes[0], view => new Transition.Bootstrap(Choice(_processes[0], view.Epoch))) is not LedgerMapCommitOutcome.Applied)
                {
                    await DelayAsync(5);
                }

                var clients = SimulationTasks.WhenAll(
                [
                    .. Enumerable.Range(0, Writers).Select(WriteAsync),
                    .. Primaries.Select((_, index) => ReadAsync(Writers + index)),
                    ScanAsync(),
                ]);
                var draining = DrainAsync();
                var serving = ServeUnservedLedgersAsync();
                var faults = InjectFaultsAsync();
                await ControlAsync();

                // Once the faults stop and every open range is served again, every call is answered (progress).
                await WithinQuiescenceAsync(faults, "the faults and their healing", FaultBound);
                await WithinQuiescenceAsync(ServeEveryRangeAsync(), "the service of every open range once the faults healed");
                await WithinQuiescenceAsync(clients, "the writers, readers, and expiry scan");
                await TakeAsync(Steps.TakeOverDuringARead, () => WithinQuiescenceAsync(TakeOverDuringAReadAsync(Writers + 3), "the takeover during a read"));
                await TakeAsync(Steps.AcrossASegmentsEnd, () => WithinQuiescenceAsync(WriteAcrossASegmentsEndAsync(Writers + 4), "the write across a segment's end"));
                await TakeAsync(Steps.Conflict, () => ConflictWithAnEntryInFlightAsync(Writers + 5));
                await TakeAsync(Steps.AppendToTheLimit, () => WithinQuiescenceAsync(AppendToTheStoresLimitAsync(Writers + 7), "the appends to the store's limit"));
                await TakeAsync(Steps.Rebase, () => WithinQuiescenceAsync(RebaseTwiceAsync(Writers + 9), "the rebases"));
                await TakeAsync(Steps.DeleteAndExpire, () => WithinQuiescenceAsync(DeleteAndExpireAsync(Writers + 10), "the delete and its expiry"));
                await TakeAsync(Steps.SplitAndMerge, () => WithinQuiescenceAsync(SplitAndMergeAsync(), "the split and the merge"));
                await TakeAsync(Steps.EveryReplicaOutOfReach, () => WithinQuiescenceAsync(ReadWithEveryReplicaOutOfReachAsync(Writers + 12), "the read with every replica out of reach"));
                _running = false;
                await draining;
                await serving;
                await CollectDecidedAsync();
                await WithinQuiescenceAsync(ReadEveryJournalAsync(Writers + 11), "the final reads");

                // A violation an answer's check found during the final reads, after the last step's invariant ran.
                Assert.Null(_violation);
                VerifyNothingWaits();
                VerifyFinalChains();
                VerifyRejections();
                VerifyReplaceRequireds();
                VerifyReads();
                VerifyWrites();
                VerifyNames();
                VerifyLinearizable();
                Assert.True(_unmet.Count == 0, Invariant($"A scripted step did not take its path: {string.Join("; ", _unmet)}"));
                RecordCounts();
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
        /// Chooses four journals whose row keys fall in the four quarters of the keyspace, so splits and merges move them
        /// between ledgers.
        /// </summary>
        private string[] ChooseJournals()
        {
            var chosen = new string?[4];
            for (var index = 0; chosen.Any(static name => name is null); index++)
            {
                var name = Invariant($"j{index}");
                var quarter = JournalRowKey.Encode(name).Span[0] >> 6;
                if (chosen[quarter] is null)
                {
                    chosen[quarter] = name;
                    _journalsByKey.Add(Convert.ToHexString(JournalRowKey.Encode(name).Span), name);
                }
            }

            return [.. chosen.Select(static name => name!)];
        }

        /// <summary>
        /// Passes each value the register applied since the last step to the ledger trace, and stamps each ledger it no longer
        /// lists as dropped there, in the run's order: the register's apply is the drop, which a primary may learn of, load,
        /// and answer from before the drainer's commit returns.
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
                foreach (var ledger in _previous.Ledgers.Where(ledger => value.Get(ledger.Id) is null))
                {
                    _dropped[ledger.Id] = ++_order;
                }

                _previous = value;
            }

            return null;
        }

        /// <summary>
        /// The chain checker and the version checker after every step (verification.md §3.2): each ledger drained since the
        /// last step, with each of its journals' whole chain from <c>Origin</c>, a rebase only after a dropped ledger's row,
        /// and every version of a journal unique (HI-I4, JS-I10); and the first violation an answer's check found since.
        /// </summary>
        private string? CheckChainsAndVersions()
        {
            while (_drainedUnchecked.TryDequeue(out var ledger))
            {
                foreach (var journal in _decided[ledger].Rows.Select(static row => row.Journal).Distinct())
                {
                    if (CheckChain(journal) is { } violation)
                    {
                        return violation;
                    }
                }

                if (CheckVersions(ledger) is { } repeated)
                {
                    return repeated;
                }
            }

            return _violation;
        }

        /// <summary>Checks a journal's chain over its reference so far, a rebase allowed after a dropped ledger's row.</summary>
        private string? CheckChain(string journal) =>
            TokenChainChecker.Check(store, default, [.. Reference(journal).Select(static row => row.Chain)], dropped: _dropped.Keys.ToHashSet()) is { } violation
                ? Invariant($"the chain checker found journal {journal}'s rows break their chain: {violation}")
                : null;

        /// <summary>Records each version of a ledger's rows with the version checker, once.</summary>
        private string? CheckVersions(LedgerId ledger)
        {
            if (!_versionsChecked.Add(ledger))
            {
                return null;
            }

            foreach (var row in _decided[ledger].Rows)
            {
                if (_versions.Version(JournalRowKey.Encode(row.Journal).Span, row.Version) is { } repeated)
                {
                    return Invariant($"HI-I4: the version checker found {repeated}");
                }
            }

            return null;
        }

        private async Task<Process> StartAsync(string name, NodeId node, bool primary)
        {
            var view = await LedgerMapView.OpenAsync(register, cancellationToken);
            var process = new Process(name, node, view, new ClientTransport(replicas, name), store, mutation, primary ? Rejected : null, primary ? ReplaceRequired : null, replicas.LedgerTrace, cancellationToken);
            if (process.Journals is { } journals)
            {
                // Every process started, a crashed one's successor among them, records its scan's proposals.
                journals.OnExpiryProposed = (key, expected) => ExpiryProposed(process, key, expected);
            }

            return process;
        }

        /// <summary>
        /// Records each <c>Expired</c> a process's expiry scan proposes as a write of its own client, invoked before the
        /// proposal, and its answer as a write's; a proposal which failed, or whose process crashed before it answered, as an
        /// unknown write (<c>Ambiguous</c>), unbounded, as <see cref="RouteAsync"/> records a write it never heard. A failure
        /// is admitted only on a crashed process or for a proposal made while the faults ran; once they heal, any fails the
        /// run.
        /// </summary>
        private Action<JournalWriteOutcome?> ExpiryProposed(Process process, JournalRowKey key, JournalVersion expected)
        {
            var journal = _journalsByKey[Convert.ToHexString(key.Span)];
            uint crc;
            using (var record = JournalEntryRecord.Expired(key, expected))
            {
                crc = record.Crc32C;
            }

            var faulting = _faulting;
            var invokedAt = ++_order;
            var invocation = _history.Invoke(JournalInput.Expire(journal, expected.ToString()));
            return outcome =>
            {
                if (outcome is { } answered && !process.Crashed)
                {
                    _expired += answered.Value is JournalWriteOutcome.Accepted ? 1 : 0;
                    _ = Complete(invocation, answered, journal, JournalEntryKind.Expired, null, crc, invokedAt);
                    return;
                }

                if (outcome is null && !process.Crashed && !faulting)
                {
                    _violation ??= Invariant($"the expiry scan's Expired of journal {journal} at {expected} by {process.Name} failed once the faults had healed");
                }

                Record($"the expiry of journal {journal} at {expected} by {process.Name} ended unanswered: {outcome?.Value}");
                ClaimUnheard(outcome, crc, $"journal {journal}");
                _ = Complete(invocation, JournalWriteOutcome.Ambiguous.Instance, journal, JournalEntryKind.Expired, null, crc, invokedAt);
            };
        }

        /// <summary>
        /// Records a rejection, under its admission's lock, for the white-box oracle, with whether its owner was deposed: the
        /// register's latest value no longer lists its ledger open, so a successor may have moved the journal on.
        /// </summary>
        private void Rejected(LedgerId ledger, JournalRejection rejection)
        {
            _rejections.Add(new RejectionRecord(ledger, rejection, IsDeposed(ledger), ++_order));
        }

        /// <summary>Records a <c>ReplaceRequired</c>, under its admission's lock, for the white-box oracle, with whether its owner was deposed.</summary>
        private void ReplaceRequired(LedgerId ledger, JournalReplaceRequired decision)
        {
            _replaceRequireds.Add(new ReplaceRequiredRecord(ledger, decision, IsDeposed(ledger)));
        }

        /// <summary>Returns whether the register's latest value lists <paramref name="ledger"/> other than open.</summary>
        private bool IsDeposed(LedgerId ledger) =>
            mutation.DeposedAlways
            || register.AppliedWrites.Count > 0
            && LedgerMapValue.Decode(register.AppliedWrites[^1].AsSpan()).Value is LedgerMapValue latest
            && latest.Get(ledger) is not { IsOpen: true };
        private TransitionChoice Choice(Process executor, ulong epoch) =>
            new(executor.Node, [executor.Node, .. nodes.Select(static node => node.NodeId).Where(node => node != executor.Node)], Window, 0, epoch);

        /// <summary>
        /// Executes the transition <paramref name="choose"/> picks from the process's view, through
        /// <paramref name="committer"/>; an <paramref name="interruption"/> is cancelled as soon as the executor submits
        /// its change, so the executor stops waiting while the change may apply. Returns the commit's outcome or, while
        /// the faults run or once the process crashed, the failure a fault explains: an interruption or a crash, an
        /// unknown register outcome, a missed deadline, or a quorum's failure the faults explain. After the faults heal,
        /// any failure fails the run.
        /// </summary>
        private async Task<object?> ExecuteAsync(Process process, Func<LedgerMapValue, Transition> choose, int committer = 0, CancellationTokenSource? interruption = null)
        {
            var faulting = _faulting;
            var kind = "transition";
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(process.Lifetime, interruption?.Token ?? CancellationToken.None);
            try
            {
                var view = await process.View.ReadAsync(cancellationToken);
                var transition = choose(view);
                kind = transition.Value?.GetType().Name ?? kind;
                var outcome = await process.Executor!.ExecuteAsync(transition, (change, token) => CommitAsync(committers[committer], change, token, interruption), linked.Token);
                Record($"{kind} by {process.Name}: {outcome.Value}");
                if (outcome.Value is LedgerMapCommitOutcome.Applied)
                {
                    _splits += transition.Value is Transition.Split ? 1 : 0;
                    _merges += transition.Value is Transition.Merge ? 1 : 0;
                }

                return outcome.Value;
            }
            catch (Exception failure) when (process.Crashed && failure is ObjectDisposedException or OperationCanceledException
                || faulting && (failure is OperationCanceledException or IOException or TimeoutException
                    || failure is AggregateException aggregate && QuorumFailures.IsAdmitted(aggregate, mediaDamage: true)))
            {
                Record($"{kind} by {process.Name} failed: {failure.GetType().Name}");
                return failure;
            }
        }

        /// <summary>Submits a change to <paramref name="committer"/>, and cancels <paramref name="interruption"/> once it is submitted.</summary>
        private static Task<LedgerMapCommitOutcome> CommitAsync(LedgerMapCommitter committer, LedgerMapChange change, CancellationToken cancellationToken, CancellationTokenSource? interruption)
        {
            var commit = committer.CommitAsync(change, cancellationToken);
            interruption?.Cancel();
            return commit;
        }

        /// <summary>
        /// Chooses a replace, split, or merge of a random open ledger each round, which a random running primary-capable
        /// process executes, becoming the primary of the ledgers it opens.
        /// </summary>
        private async Task ControlAsync()
        {
            for (var round = 0; round < workload.Transitions; round++)
            {
                await DelayAsync(60);
                var process = _random.Choose(Live);
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
        /// The fault plan (verification.md §7.3): fires each <see cref="Fault"/> once while the clients, the control rounds,
        /// and the drainer run, in an order the seed draws, a short random gap apart. Each fault heals before the next and, once it took its path, records the evidence
        /// <see cref="TheCorpusFiresEveryFault"/> requires. While the plan runs, a failure a fault explains is admitted;
        /// after it, any failure fails the run.
        /// </summary>
        private async Task InjectFaultsAsync()
        {
            var plan = Enum.GetValues<Fault>();
            for (var index = plan.Length - 1; index > 0; index--)
            {
                var other = _random.Next(index + 1);
                (plan[index], plan[other]) = (plan[other], plan[index]);
            }

            _faulting = true;
            try
            {
                foreach (var fault in plan)
                {
                    await DelayAsync(20);
                    Record($"fault starts: {fault}");
                    var evidence = fault switch
                    {
                        Fault.Crash => await CrashAndRestartAsync(),
                        Fault.Partition => await PartitionAsync(),
                        Fault.Pause => await PauseAsync(),
                        Fault.LostMessages => await LoseMessagesAsync(),
                        Fault.Damage => await DamageAsync(),
                        Fault.UnknownOutcomes => await LeaveOutcomesUnknownAsync(),
                        Fault.DuplicateCommitters => await RaceCommittersAsync(),
                        Fault.InterruptedExecutor => await InterruptAnExecutorAsync(),
                        _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, null),
                    };
                    if (evidence is null)
                    {
                        Record($"fault missed its path: {fault}");
                    }
                    else
                    {
                        Record($"{FaultEntry}{fault}: {evidence}");
                    }
                }

                await SimulationTasks.WhenAll(_settling);
            }
            finally
            {
                _faulting = false;
            }
        }

        /// <summary>
        /// Crashes the process which serves a random journal, with its node, as a process or an operating system, while a
        /// write the fault client sent it is in flight, which ends unanswered: the process answers nothing more and its
        /// memory is lost, and the node keeps what its disk kept. After a short downtime the node restarts from its WAL,
        /// whose recovery checks run (LS-I5), and a new process starts in its place as a process starts in production: it
        /// opens the map, reconciles its node's replicas, and replaces each open ledger the map names its node the primary
        /// of under the crashed process's claim, loading each key again from the ledgers and the store as it admits it.
        /// </summary>
        private async Task<string?> CrashAndRestartAsync()
        {
            // The crash waits for a write the fault client sends the process to be in flight there: one the process answers
            // at once, as with Moved, is answered before the crash and sent again.
            Process? process = null;
            Task<(JournalWriteOutcome Outcome, bool Deposed)>? cut = null;
            for (var attempt = 0; attempt < 10 && !(cut is { IsCompleted: false } && process is { InFlight: > 0 }); attempt++)
            {
                if (cut is not null)
                {
                    _ = await cut;
                }

                var journal = _random.Choose(_journals);
                var version = (await ReadOnceAsync(FaultClient, journal)).Version;
                process = OwnerOf(JournalRowKey.Encode(journal));
                cut = WriteAnsweredAsync(FaultClient + 1, journal, JournalEntryKind.Replace, version);
            }

            // The write in flight ends unanswered once the crashed process's proposals are decided, which may need the
            // close of a ledger the restarted process replaces, so the plan collects it with the crashed process's release.
            var node = nodes.Single(node => node.NodeId == process!.Node);
            var inFlight = cut!.IsCompleted ? 0 : process!.InFlight;
            _settling.Add(cut);
            var operatingSystem = _random.Chance(0.5);
            var (restarted, replaced) = await RestartAsync(node, operatingSystem);
            return inFlight > 0
                ? Invariant($"{process!.Name} and {node.Address} crashed ({(operatingSystem ? "operating system" : "process")}) with {inFlight} calls in flight, which end unanswered, and {restarted!.Name} replaced {replaced} unserved ledgers")
                : null;
        }

        /// <summary>
        /// Crashes <paramref name="node"/>, as a process or an operating system, and the journal process it hosts, if any,
        /// which answers nothing more and whose memory is released; restarts the node after a short downtime from what its
        /// disk kept, and starts a new journal process in the old one's place, which reconciles the node's replicas and
        /// replaces the open ledgers it leaves unserved. Returns the new process, or none, and how many ledgers it replaced.
        /// </summary>
        private async Task<(Process? Restarted, int Replaced)> RestartAsync(LedgerStoreNode node, bool operatingSystem)
        {
            var index = _processes.FindIndex(process => process.Node == node.NodeId && process.Journals is not null);
            var crashed = index < 0 ? null : _processes[index];
            crashed?.Crash();
            await node.CrashAsync(operatingSystem);
            if (crashed is not null)
            {
                // A crash waits for nothing: the crashed process's memory is released once its proposals in flight are
                // decided, which may need the close of a ledger its successor replaces (LP-I16).
                _settling.Add(crashed.DisposeAsync().AsTask());
            }

            await DelayAsync(30);
            await node.StartAsync(cancellationToken);
            if (crashed is null)
            {
                await ReconcileReplicasAsync(node, Drainer.View);
                return (null, 0);
            }

            var restarted = await StartAsync(Invariant($"process-{index}.{++_incarnations[index]}"), node.NodeId, primary: true);
            _processes[index] = restarted;
            await ReconcileReplicasAsync(node, restarted.View);
            return (restarted, await ServeUnservedAsync(restarted));
        }

        /// <summary>Partitions a random node from every other, and from the processes, while the fault client writes, then heals it.</summary>
        private async Task<string?> PartitionAsync()
        {
            var node = _random.Choose(nodes);
            var window = TimeSpan.FromMilliseconds(10 + _random.Next(40));
            node.Isolate();
            try
            {
                await SimulationTasks.WhenAll(TrafficAsync(), Task.Delay(window, environment.TimeProvider, cancellationToken));
            }
            finally
            {
                node.Reconnect();
            }

            return Invariant($"{node.Address} isolated for {window.TotalMilliseconds} ms");
        }

        /// <summary>Suspends a random node, so it serves nothing and its disk completes no write, while the fault client writes.</summary>
        private async Task<string?> PauseAsync()
        {
            var node = _random.Choose(nodes);
            var window = TimeSpan.FromMilliseconds(5 + _random.Next(30));
            node.Pause(window);
            await SimulationTasks.WhenAll(TrafficAsync(), Task.Delay(window + TimeSpan.FromMilliseconds(1), environment.TimeProvider, cancellationToken));
            return Invariant($"{node.Address} paused for {window.TotalMilliseconds} ms");
        }

        /// <summary>
        /// Loses messages, duplicates requests, and loses journal answers, the first write's by construction, while the fault
        /// client reads and writes, until the network has lost a message and duplicated a request and a journal answer was
        /// lost; then stops.
        /// </summary>
        private async Task<string?> LoseMessagesAsync()
        {
            var (lost, duplicated, answers) = (environment.Rpc.Lost, environment.Rpc.Duplicates, _lostAnswers);
            var duplication = environment.Rpc.DuplicationRate;
            environment.Network.MessageDropRate = 0.05;
            environment.Rpc.DuplicationRate = 0.25;
            _answerLossRate = 0.2;
            try
            {
                for (var call = 0; call < 50 && (environment.Rpc.Lost == lost || environment.Rpc.Duplicates == duplicated || _lostAnswers == answers); call++)
                {
                    await TrafficAsync(loseAnswer: call == 0);
                }
            }
            finally
            {
                environment.Network.MessageDropRate = 0;
                environment.Rpc.DuplicationRate = duplication;
                _answerLossRate = 0;
            }

            return environment.Rpc.Lost > lost && environment.Rpc.Duplicates > duplicated && _lostAnswers > answers
                ? Invariant($"{environment.Rpc.Lost - lost} messages lost, {environment.Rpc.Duplicates - duplicated} requests duplicated, and {_lostAnswers - answers} journal answers lost")
                : null;
        }

        /// <summary>
        /// Restarts a random node, crashing its journal process with it, and damages a page of a WAL file its recovery
        /// classified, after a catalog checkpoint past that WAL, so the damage faults only the records on the page and never
        /// hides a create or drop from a later open (LS-I5): the one node whose media the faults damage, so at most Q − 1
        /// replicas of a ledger answer corrupt (system model §3).
        /// </summary>
        private async Task<string?> DamageAsync()
        {
            const int waits = 1_000;
            var node = _random.Choose(nodes);
            for (var restart = 0; restart < 3; restart++)
            {
                // A write first, so the WAL the restart recovers holds records, and a page of it holds one.
                await TrafficAsync();
                _ = await RestartAsync(node, operatingSystem: false);
                var store = node.Store;
                var frontier = store.Wal.DurableFrontier;
                store.RequestCatalogCheckpoint();
                for (var wait = 0; wait < waits && !(store.CatalogCut >= frontier && store.CatalogCheckpointCurrent && store.MaintenanceIdle); wait++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1), environment.TimeProvider, cancellationToken);
                }

                for (var attempt = 0; attempt < 5 && store.CatalogCut >= frontier; attempt++)
                {
                    if (node.Damage())
                    {
                        return Invariant($"{node.Address} restarted and its media damaged");
                    }
                }

                Record($"{node.Address} held no page its recovery classified to damage: catalog cut {store.CatalogCut}, frontier {frontier}");
            }

            return null;
        }

        /// <summary>
        /// Makes the ledger map register leave writes' outcomes unknown while transitions commit through it: the first
        /// write's request is lost, so it never applies, and others apply or not, or land later, or apply and answer a
        /// mismatch, until a write whose outcome was unknown applied; then stops. Each executor's lifetimes are then the map
        /// view's to decide, and the committers decide each change from a read (ledgers.md §2.6).
        /// </summary>
        private async Task<string?> LeaveOutcomesUnknownAsync()
        {
            var (unknown, applied) = (register.UnknownOutcomes, register.AppliedUnknownOutcomes);
            register.LoseNextWrites = 1;
            register.UnknownOutcomeRate = 0.6;
            register.AppliedMismatchRate = 0.3;
            bool tookPath;
            try
            {
                for (var attempt = 0; attempt < 20 && (register.LoseNextWrites > 0 || register.AppliedUnknownOutcomes == applied); attempt++)
                {
                    var process = _random.Choose(Live);
                    _ = await ExecuteAsync(process, map => new Transition.Replace(RandomOpen(map), Choice(process, map.Epoch)));
                }

                tookPath = register.LoseNextWrites == 0 && register.AppliedUnknownOutcomes > applied;
            }
            finally
            {
                register.LoseNextWrites = 0;
                register.UnknownOutcomeRate = 0;
                register.AppliedMismatchRate = 0;
            }

            return tookPath
                ? Invariant($"{register.UnknownOutcomes - unknown} writes' outcomes unknown, of which {register.AppliedUnknownOutcomes - applied} applied")
                : null;
        }

        /// <summary>
        /// Has both processes replace the same open ledger at once, each through its own committer (ledgers.md §2.6): both
        /// recover it, and one change applies while the other finds its rule failed, superseded.
        /// </summary>
        private async Task<string?> RaceCommittersAsync()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var target = RandomOpen(await _processes[0].View.ReadAsync(cancellationToken));
                var (first, second) = (_processes[0], _processes[1]);
                IEnumerable<Task<object?>> racing =
                [
                    ExecuteAsync(first, map => new Transition.Replace(target, Choice(first, map.Epoch)), committer: 0),
                    ExecuteAsync(second, map => new Transition.Replace(target, Choice(second, map.Epoch)), committer: 1),
                ];
                var outcomes = await SimulationTasks.WhenAll(racing);
                if (outcomes.Count(static outcome => outcome is LedgerMapCommitOutcome.Applied) == 1 && outcomes.Count(static outcome => outcome is LedgerMapCommitOutcome.Superseded) == 1)
                {
                    return Invariant($"replaces of s{target} by {first.Name} and {second.Name} through two committers answered {outcomes[0]} and {outcomes[1]}");
                }
            }

            return null;
        }

        /// <summary>
        /// Interrupts a process's executor as soon as it submits its replace of an open ledger, so it stops waiting while its
        /// change may apply and its lifetime is the map view's to decide; the other process then replaces the same ledger,
        /// which applies or finds the interrupted change applied, superseded.
        /// </summary>
        private async Task<string?> InterruptAnExecutorAsync()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var target = RandomOpen(await _processes[0].View.ReadAsync(cancellationToken));
                var (interrupted, other) = _random.Chance(0.5) ? (_processes[0], _processes[1]) : (_processes[1], _processes[0]);
                using var interruption = new CancellationTokenSource();
                var first = await ExecuteAsync(interrupted, map => new Transition.Replace(target, Choice(interrupted, map.Epoch)), committer: 0, interruption);
                var second = await ExecuteAsync(other, map => new Transition.Replace(target, Choice(other, map.Epoch)), committer: 1);
                if (first is OperationCanceledException && second is LedgerMapCommitOutcome.Applied or LedgerMapCommitOutcome.Superseded)
                {
                    return Invariant($"{interrupted.Name}'s replace of s{target} was interrupted once submitted, and {other.Name}'s answered {second}");
                }
            }

            return null;
        }

        /// <summary>Returns a random open ledger of <paramref name="map"/>.</summary>
        private LedgerId RandomOpen(LedgerMapValue map)
        {
            LedgerMetadata[] open = [.. map.Ledgers.Where(static ledger => ledger.IsOpen)];
            return _random.Choose(open).Id;
        }

        /// <summary>
        /// The fault client reads a random journal and replaces it at the version it read, so a fault meets calls in flight:
        /// a <c>Replace</c>, which the store's limits always admit, so the fault client never leaves a journal at its limit.
        /// </summary>
        private async Task TrafficAsync(bool loseAnswer = false)
        {
            var journal = _random.Choose(_journals);
            var version = (await ReadOnceAsync(FaultClient, journal)).Version;
            _ = await WriteAnsweredAsync(FaultClient, journal, JournalEntryKind.Replace, version, loseAnswer);
        }

        /// <summary>
        /// The controller's service of unserved ledgers, until the run ends: each running process replaces the open ledgers
        /// its registry lists unserved, as production's controller replaces those a node reports (ledgers.md §8.3), such as
        /// a ledger an interrupted or failed executor sealed and never closed in the map, and a crashed process's.
        /// </summary>
        private async Task ServeUnservedLedgersAsync()
        {
            while (_running)
            {
                await DelayAsync(10);
                foreach (var process in Live)
                {
                    if (!process.Registry!.Unserved.IsEmpty)
                    {
                        _ = await ServeUnservedAsync(process);
                    }
                }
            }
        }

        /// <summary>
        /// Replaces, by <paramref name="process"/>, each open ledger its registry lists unserved once reconciled with the map:
        /// one the map names its node the primary of under a claim it does not hold, after its predecessor's crash or a
        /// lifetime which stopped, as production's controller replaces a node's unserved ledgers (ledgers.md §8.3). Returns
        /// how many it replaced.
        /// </summary>
        private async Task<int> ServeUnservedAsync(Process process)
        {
            await process.Registry!.ReconciledAsync((await process.View.ReadAsync(cancellationToken)).Epoch, cancellationToken);
            var replaced = 0;
            foreach (var ledger in process.Registry.Unserved)
            {
                object? outcome = null;
                for (var attempt = 0; attempt < 10 && outcome is not (LedgerMapCommitOutcome.Applied or LedgerMapCommitOutcome.Superseded) && process.View.Current.Get(ledger) is { IsOpen: true }; attempt++)
                {
                    outcome = await ExecuteAsync(process, map => new Transition.Replace(ledger, Choice(process, map.Epoch)));
                    if (outcome is Exception)
                    {
                        await DelayAsync(5);
                    }
                }

                replaced += outcome is LedgerMapCommitOutcome.Applied ? 1 : 0;
            }

            return replaced;
        }

        /// <summary>
        /// Once the faults heal, has each process replace the open ledgers it leaves unserved until the process the map
        /// names each open ledger's primary of serves it, as every range is served once faults stop (progress).
        /// </summary>
        private async Task ServeEveryRangeAsync()
        {
            while (true)
            {
                foreach (var process in Primaries)
                {
                    _ = await ServeUnservedAsync(process);
                }

                var map = await _processes[0].View.ReadAsync(cancellationToken);
                foreach (var process in Primaries)
                {
                    _ = await process.View.ReadAsync(cancellationToken);
                    await process.Registry!.ReconciledAsync(map.Epoch, cancellationToken);
                }

                if (map.Ledgers.Where(static ledger => ledger.IsOpen).All(ledger => Primaries.Any(process => process.Node == ledger.Primary.Node && process.Registry!.TryGet(ledger.Id) is not null)))
                {
                    Record($"every open range served at epoch {map.Epoch}");
                    return;
                }

                await DelayAsync(10);
            }
        }

        /// <summary>
        /// Writes random journals at the versions this writer last saw: appends, replaces, and deletes, and a replace once
        /// the store asked for one or refused an append; a rejection gives the writer the version to write at next, and an
        /// ambiguous write a read. A writer writes only at a version an answer bound (verification.md §3.4): a deposed
        /// owner's rejection is a hint which binds nothing, so the writer reads for its next version as after an ambiguous
        /// write.
        /// </summary>
        private async Task WriteAsync(int client)
        {
            var known = _journals.ToDictionary(static journal => journal, static _ => JournalVersion.Origin, StringComparer.Ordinal);
            var latched = new HashSet<string>(StringComparer.Ordinal);
            for (var count = 0; count < workload.WritesPerWriter; count++)
            {
                await DelayAsync(20);
                var journal = _random.Choose(_journals);
                var kind = latched.Contains(journal) ? JournalEntryKind.Replace : _random.Next(10) switch
                {
                    < 6 => JournalEntryKind.Append,
                    < 8 => JournalEntryKind.Replace,
                    _ => JournalEntryKind.Delete,
                };
                var (outcome, deposed) = await WriteAnsweredAsync(client, journal, kind, known[journal]);
                switch (outcome.Value)
                {
                    case JournalWriteOutcome.Accepted accepted:
                        known[journal] = accepted.Version;
                        _ = accepted.ReplaceRequested && kind != JournalEntryKind.Replace ? latched.Add(journal) : latched.Remove(journal);
                        break;
                    case JournalWriteOutcome.Rejected rejected when !deposed:
                        known[journal] = rejected.CurrentVersion;
                        break;
                    case JournalWriteOutcome.ReplaceRequired:
                        _ = latched.Add(journal);
                        break;
                    case JournalWriteOutcome.Rejected or JournalWriteOutcome.Ambiguous:
                        known[journal] = (await ReadOnceAsync(client, journal)).Version;
                        break;
                    case var other:
                        throw new InvalidOperationException(Invariant($"A routed write answered {other}."));
                }
            }
        }

        /// <summary>
        /// Encodes one entry of a unique payload once, writes it through the primary the map names until an answer other
        /// than <c>Moved</c> or <c>Fenced</c>, and records it in the history.
        /// </summary>
        private async Task<JournalWriteOutcome> WriteOnceAsync(int client, string journal, JournalEntryKind kind, JournalVersion expected) =>
            (await WriteAnsweredAsync(client, journal, kind, expected)).Outcome;

        /// <summary>Writes as <see cref="WriteOnceAsync"/> does; returns the answer and whether a deposed owner decided it.</summary>
        private async Task<(JournalWriteOutcome Outcome, bool Deposed)> WriteAnsweredAsync(int client, string journal, JournalEntryKind kind, JournalVersion expected, bool loseAnswer = false)
        {
            var key = JournalRowKey.Encode(journal);
            var bytes = BitConverter.GetBytes(++_nextId);
            using var payload = ArcBuffer.Wrap(bytes);
            var encoder = Live[0].Journals!;
            using var record = kind switch
            {
                JournalEntryKind.Append => encoder.EncodeAppend(key, expected, payload),
                JournalEntryKind.Replace => encoder.EncodeReplace(key, expected, payload),
                JournalEntryKind.Delete => encoder.EncodeDelete(key, expected),
                JournalEntryKind.Expired => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only the expiry scan writes an Expired."),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            var input = kind switch
            {
                JournalEntryKind.Append => JournalInput.Append(journal, expected.ToString(), bytes),
                JournalEntryKind.Replace => JournalInput.Replace(journal, expected.ToString(), bytes),
                JournalEntryKind.Delete => JournalInput.Delete(journal, expected.ToString()),
                JournalEntryKind.Expired => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only the expiry scan writes an Expired."),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            var invokedAt = ++_order;
            var invocation = _history.Invoke(client, input);
            var crc = record.Crc32C;
            var outcome = await RouteAsync(key, crc, process => process.Journals!.WriteAsync(record, cancellationToken), loseAnswer);
            var deposed = Complete(invocation, outcome, journal, kind, kind == JournalEntryKind.Delete ? null : bytes, crc, invokedAt);
            return (outcome, deposed);
        }

        /// <summary>
        /// Records a write's or an expiry's answer in the history, in the writes the decided history is checked against, and
        /// in the counts; returns whether a deposed owner decided it. A rejection or a <c>ReplaceRequired</c> is the client's
        /// view of a decision its admission recorded, matched by the record's CRC-32C and, for a rejection, the version it
        /// reports, which takes its deposed flag from that record. A <c>Moved</c> or <c>Fenced</c> answer, which only a write
        /// sent once gives, has no effect.
        /// </summary>
        private bool Complete(HistoryRecorder<JournalInput, JournalOutput>.Invocation invocation, JournalWriteOutcome outcome, string journal, JournalEntryKind kind, byte[]? payload, uint crc, long invokedAt)
        {
            var deposed = false;
            JournalVersion? version = null;
            switch (outcome.Value)
            {
                case JournalWriteOutcome.Accepted accepted:
                    _accepted++;
                    version = accepted.Version;
                    _history.Complete(invocation, JournalOutput.Accepted(accepted.Version.ToString()));
                    break;
                case JournalWriteOutcome.Rejected rejected:
                    _rejected++;
                    version = rejected.CurrentVersion;
                    // A cold Expired's rejection reports its own expected version, a hint decided from no state (journals.md §8.6).
                    var decided = Answered(_rejections, _rejectionsAnswered, record => record.Rejection.RecordCrc == crc && record.Rejection.Applied == rejected.CurrentVersion, $"rejection of journal {journal} with {rejected.CurrentVersion}");
                    deposed = decided.Deposed || decided.Rejection.Cold;
                    _history.Complete(invocation, JournalOutput.Rejected(rejected.CurrentVersion.ToString(), deposed));
                    break;
                case JournalWriteOutcome.ReplaceRequired:
                    _replaceRequired++;
                    deposed = Answered(_replaceRequireds, _replaceRequiredsAnswered, record => record.Decision.RecordCrc == crc, $"ReplaceRequired of journal {journal}").Deposed;
                    _history.Complete(invocation, JournalOutput.ReplaceRequiredBy(deposed));
                    break;
                case JournalWriteOutcome.Ambiguous:
                    _history.CompleteUnbounded(invocation, JournalOutput.Ambiguous);
                    break;
                case JournalMoved or JournalWriteOutcome.Fenced:
                    _history.Complete(invocation, JournalOutput.Moved);
                    break;
                case var other:
                    throw new InvalidOperationException(Invariant($"A write answered {other}."));
            }

            _writes.Add(new WriteRecord(journal, kind, payload, outcome.Value, version, invokedAt, ++_order));
            return deposed;
        }

        /// <summary>
        /// Returns the earliest recorded decision <paramref name="matches"/> accepts which no answer has claimed, and claims
        /// it, so two identical records' answers each take their own; fails when the admission recorded none.
        /// </summary>
        private static T Answered<T>(List<T> records, HashSet<int> answered, Func<T, bool> matches, string what)
        {
            for (var index = 0; index < records.Count; index++)
            {
                if (!answered.Contains(index) && matches(records[index]))
                {
                    _ = answered.Add(index);
                    return records[index];
                }
            }

            throw new InvalidOperationException(Invariant($"JS-I4: the client heard a {what} which no admission recorded deciding"));
        }

        /// <summary>
        /// Claims the decision an answer the client never heard reports, a rejection or a <c>ReplaceRequired</c> its
        /// admission recorded, matched by the record's <paramref name="crc"/>, so no later answer claims it.
        /// </summary>
        private void ClaimUnheard(JournalWriteOutcome? outcome, uint crc, string subject)
        {
            if (outcome?.Value is JournalWriteOutcome.Rejected rejected)
            {
                _ = Answered(_rejections, _rejectionsAnswered, record => record.Rejection.RecordCrc == crc && record.Rejection.Applied == rejected.CurrentVersion, $"rejection of {subject} with {rejected.CurrentVersion}");
            }
            else if (outcome?.Value is JournalWriteOutcome.ReplaceRequired)
            {
                _ = Answered(_replaceRequireds, _replaceRequiredsAnswered, record => record.Decision.RecordCrc == crc, $"ReplaceRequired of {subject}");
            }
        }

        /// <summary>
        /// Writes through the primary the map names, and sends the same record again after <c>Moved</c>, to the owner it
        /// names or, naming none, once the map is read again, and after <c>Fenced</c>: neither has an effect (JS-I4). A
        /// crashed process takes no call, so the client routes again; a call in flight on a process which crashes, and a
        /// final answer the faults lose (<paramref name="loseAnswer"/>, or at the fault plan's rate), end
        /// <c>Ambiguous</c>, the client's view of an answer it never heard. A lost rejection or <c>ReplaceRequired</c> is
        /// still the decision its admission recorded, matched by the record's <paramref name="crc"/>, so no later answer
        /// claims it.
        /// </summary>
        private async Task<JournalWriteOutcome> RouteAsync(JournalRowKey key, uint crc, Func<Process, Task<JournalWriteOutcome>> write, bool loseAnswer = false)
        {
            var target = OwnerOf(key);
            while (true)
            {
                if (target.Crashed)
                {
                    await DelayAsync(5);
                    target = OwnerOf(key);
                    continue;
                }

                var (outcome, heard) = await CallAsync(target, write);
                if (heard && (loseAnswer || _answerLossRate > 0 && _random.Chance(_answerLossRate)) && outcome?.Value is not (JournalMoved or JournalWriteOutcome.Fenced))
                {
                    heard = false;
                    Record($"the answer {outcome?.Value} of a write of {key} to {target.Name} was lost");
                }

                if (!heard)
                {
                    _lostAnswers++;
                    ClaimUnheard(outcome, crc, Invariant($"{key}"));
                    return JournalWriteOutcome.Ambiguous.Instance;
                }

                switch (outcome!.Value.Value)
                {
                    case JournalMoved moved:
                        _moved++;
                        target = await NextOwnerAsync(key, moved.Owner);
                        break;
                    case JournalWriteOutcome.Fenced:
                        await DelayAsync(5);
                        target = OwnerOf(key);
                        break;
                    default:
                        return outcome.Value;
                }
            }
        }

        /// <summary>
        /// Calls <paramref name="target"/>, counting the call in flight on it; returns the answer, if any, and whether the
        /// client heard it: not when the process crashed with the call in flight, since it answers nothing more, nor when,
        /// while the faults run, the call failed as the faults explain, the quorum it needed out of reach, which leaves its
        /// outcome unknown.
        /// </summary>
        private async Task<(JournalWriteOutcome? Outcome, bool Heard)> CallAsync(Process target, Func<Process, Task<JournalWriteOutcome>> write)
        {
            var faulting = _faulting;
            target.InFlight++;
            try
            {
                var outcome = await write(target);
                return (outcome, !target.Crashed);
            }
            catch (Exception failure) when (target.Crashed || faulting && IsUnavailable(failure))
            {
                Record($"a write to {target.Name} failed: {failure.GetType().Name}");
                return (null, false);
            }
            finally
            {
                target.InFlight--;
            }
        }

        /// <summary>
        /// Returns whether <paramref name="failure"/> is a call's failure the faults explain: a missed deadline, or the
        /// store's unavailability (journals.md §6), a quorum whose replicas each missed theirs or, with the damaged node's
        /// media, answered corrupt. The quorum's aggregate itself is no answer of the store's.
        /// </summary>
        private static bool IsUnavailable(Exception failure) =>
            failure is TimeoutException
            || failure is JournalStoreUnavailableException { InnerException: AggregateException quorum } && QuorumFailures.IsAdmitted(quorum, mediaDamage: true);

        /// <summary>
        /// Returns the process a <c>Moved</c> names, or, naming none, the one the map names once read again. Each hop waits,
        /// since two primaries whose views of the map differ name each other until the stale one catches up.
        /// </summary>
        private async Task<Process> NextOwnerAsync(JournalRowKey key, NodeId? owner)
        {
            if (owner is { } node && Primaries.FirstOrDefault(process => process.Node == node) is { } named)
            {
                await DelayAsync(1);
                return named;
            }

            await DelayAsync(5);
            _ = await _processes[0].View.ReadAsync(cancellationToken);
            return OwnerOf(key);
        }

        /// <summary>Returns the process whose node the map names the primary of the journal's open ledger, or the first.</summary>
        private Process OwnerOf(JournalRowKey key) =>
            _processes[0].View.FindOpen(key.Span)?.Primary.Node is { } node && Primaries.FirstOrDefault(process => process.Node == node) is { } owner
                ? owner
                : _processes[0];

        /// <summary>Reads random journals through the primary the map names, recording each.</summary>
        private async Task ReadAsync(int client)
        {
            for (var count = 0; count < workload.ReadsPerReader; count++)
            {
                await DelayAsync(25);
                _ = await ReadOnceAsync(client, _random.Choose(_journals));
            }
        }

        /// <summary>
        /// Reads a journal through the primary the map names, routed again after <c>Moved</c>, and records the read in the
        /// history and with its cut. As it answers, its cut lies below each ledger's decided end (JS-I3), and a rebase it
        /// answers is checked at once against the rows dropped so far (JS-I10).
        /// </summary>
        private async Task<ReadRecord> ReadOnceAsync(int client, string journal, Process? first = null)
        {
            var key = JournalRowKey.Encode(journal);
            var invokedAt = ++_order;
            var invocation = _history.Invoke(client, JournalInput.Read(journal));
            var target = first ?? OwnerOf(key);
            var hops = 0;
            while (true)
            {
                // A read has no effect, so a read the crash of its process cut is sent again, as a client resends it.
                if (target.Crashed)
                {
                    await DelayAsync(5);
                    target = OwnerOf(key);
                    continue;
                }

                JournalReadOutcome outcome;
                var faulting = _faulting;
                target.InFlight++;
                try
                {
                    outcome = await target.Journals!.ReadAsync(key, cancellationToken);
                }
                catch (Exception failure) when (target.Crashed || faulting && IsUnavailable(failure))
                {
                    // A read has no effect, so a client sends a read which failed as the faults explain again.
                    Record($"a read of journal {journal} from {target.Name} failed: {failure.GetType().Name}");
                    await DelayAsync(5);
                    continue;
                }
                finally
                {
                    target.InFlight--;
                }

                if (target.Crashed)
                {
                    (outcome.Value as IDisposable)?.Dispose();
                    continue;
                }

                if (outcome.Value is JournalMoved moved)
                {
                    _moved++;
                    hops++;
                    target = await NextOwnerAsync(key, moved.Owner);
                    continue;
                }

                using var result = (JournalReadResult)outcome.Value!;

                // The upper bound holds as the read answers, before its content streams, while decided ends only grow.
                foreach (var (ledger, end) in result.Through)
                {
                    var decided = replicas.LedgerTrace.DecidedEnd(ledger);
                    if (end.Value > decided)
                    {
                        _violation ??= Invariant($"JS-I3: a read of journal {journal} cut s{ledger} at {end}, past its decided end {decided} when it answered");
                    }
                }

                var content = new List<byte>();
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
                catch (Exception failure) when (target.Crashed || faulting && IsUnavailable(failure))
                {
                    // The process crashed, or the quorum was out of reach, while the answer's content streamed, so the
                    // client sends the read again.
                    continue;
                }

                var read = new ReadRecord(journal, result.Through.ToDictionary(), result.CurrentVersion, result.Exists, [.. content], invokedAt, ++_order, hops, target);
                if (read.Version.Count == 0 && read.Version != JournalVersion.Origin)
                {
                    _rebased++;
                    CheckRebase(read);
                }

                _reads.Add(read);
                _history.Complete(invocation, JournalOutput.Read(read.Exists, read.Version.ToString(), read.Content));
                return read;
            }
        }

        /// <summary>
        /// Checks a read's rebase against the rows drained so far (journals.md §8.3, JS-I10), at once, so the version checker
        /// sees its name before later answers; <see cref="VerifyReads"/> checks it again over the whole reference.
        /// </summary>
        private void CheckRebase(ReadRecord read) => _violation ??= RebaseViolation(read);

        /// <summary>
        /// Returns how a read's rebase breaks JS-I10 over the reference as it stands, or null: each row of its journal in a
        /// ledger below its highest cut which its cut does not list dropped before the read answered, and the rebase's base
        /// is the store's version base for them, the token <c>Plan</c> replays over their last write, which the version
        /// checker judges with the state they leave. A ledger above its highest cut, a successor written after the read, is
        /// not the read's.
        /// </summary>
        private string? RebaseViolation(ReadRecord read)
        {
            if (read.Through.Count == 0)
            {
                return Invariant($"JS-I10: a read of journal {read.Journal} rebased to {read.Version} with no cut");
            }

            var highest = read.Through.Keys.Max();
            ReferenceRow[] earlier = [.. Reference(read.Journal).Where(row => row.Tag.Ledger < highest && !read.Through.ContainsKey(row.Tag.Ledger))];
            if (earlier.FirstOrDefault(row => !_dropped.TryGetValue(row.Tag.Ledger, out var droppedAt) || droppedAt > read.AnsweredAt) is { } live)
            {
                return Invariant($"JS-I10: a read of journal {read.Journal} rebased to {read.Version} while its row {live} had not dropped");
            }

            return Checked(_versions.Rebased(JournalRowKey.Encode(read.Journal).Span, read.Version.Base, StoreBase(earlier), StateOf(earlier)));
        }
        /// <summary>
        /// The expiry scan (journals.md §8.6): runs each primary-capable process's scan, which proposes an <c>Expired</c> of
        /// each journal its warm state holds deleted with the retention passed, each recorded as it is proposed
        /// (<see cref="ExpiryProposed"/>), and admission judges by the warm state.
        /// </summary>
        private async Task ScanAsync()
        {
            for (var count = 0; count < workload.Scans; count++)
            {
                await DelayAsync(40);
                await ExpireTombstonesAsync();
            }
        }

        /// <summary>
        /// Runs one expiry scan in each running primary-capable process, one after the other, under the process's lifetime: a
        /// crash stops its scan, whose proposals in flight then end unanswered (<see cref="ExpiryProposed"/>).
        /// </summary>
        private async Task ExpireTombstonesAsync()
        {
            foreach (var process in Live)
            {
                if (process.Crashed)
                {
                    continue;
                }

                try
                {
                    _ = await process.Journals!.ExpireTombstonesAsync(process.Lifetime);
                }
                catch (Exception failure) when (process.Crashed && failure is OperationCanceledException or ObjectDisposedException)
                {
                    Record($"the expiry scan of {process.Name} ended with its crash: {failure.GetType().Name}");
                }
            }
        }

        /// <summary>
        /// Moves a journal's primary to the other process while the first reads it (journals.md §8.2, JS-I3): the first
        /// serves the journal's open ledger, and the second replaces it while the read's store read is answered and not yet
        /// heard, so the read either answers from the first with a cut below the seal or is routed again to the second. A
        /// write then sent to the first, once its registry has reconciled, is <c>Moved</c> to the second.
        /// </summary>
        private async Task TakeOverDuringAReadAsync(int client)
        {
            var journal = _random.Choose(_journals);
            var key = JournalRowKey.Encode(journal);
            var (first, second) = (_processes[0], _processes[1]);
            object? opened = null;
            for (var attempt = 0; attempt < 5 && opened is not LedgerMapCommitOutcome.Applied; attempt++)
            {
                opened = await ExecuteAsync(first, map => new Transition.Replace(map.FindOpen(key.Span)!.Value.Id, Choice(first, map.Epoch)));
            }

            Expect(opened is LedgerMapCommitOutcome.Applied, $"the first process's replace of journal {journal}'s ledger answered {opened}");
            var replaced = (await first.View.ReadAsync(cancellationToken)).FindOpen(key.Span)!.Value.Id;
            await first.Registry!.ReconciledAsync(first.View.Current.Epoch, cancellationToken);
            object? takeover = null;
            var takenOverAt = 0L;
            store.AfterNextReadOf = key.Memory;
            store.AfterNextRead = async () =>
            {
                takenOverAt = ++_order;
                takeover = await ExecuteAsync(second, map => new Transition.Replace(map.FindOpen(key.Span)!.Value.Id, Choice(second, map.Epoch)));
            };
            var read = await ReadOnceAsync(client, journal, first);
            var sealedAt = second.View.Current.Get(replaced) is { Status.Value: LedgerStatus.Closed closed } ? closed.T : (Slot?)null;
            Expect(takeover is LedgerMapCommitOutcome.Applied && takenOverAt > read.InvokedAt, $"the takeover during a read of journal {journal} answered {takeover}, at {takenOverAt} against the read's {read.InvokedAt}");
            Expect(
                read.Hops > 0 || read.ServedBy == first && sealedAt is { } end && read.Through.TryGetValue(replaced, out var cut) && cut <= end,
                $"the read of journal {journal} during the takeover of s{replaced} neither was moved nor answered from the first process below the seal");
            Record($"takeover during a read of journal {journal}: version {read.Version}, {read.Hops} hops");

            // Once the first process's registry has reconciled with the takeover, a write sent to it there is Moved to the second.
            await first.Registry!.ReconciledAsync((await first.View.ReadAsync(cancellationToken)).Epoch, cancellationToken);
            var payload = BitConverter.GetBytes(++_nextId);
            using var buffer = ArcBuffer.Wrap(payload);
            using var record = first.Journals!.EncodeAppend(key, read.Version, buffer);
            var invokedAt = ++_order;
            var invocation = _history.Invoke(client, JournalInput.Append(journal, read.Version.ToString(), payload));
            var moved = await first.Journals.WriteAsync(record, cancellationToken);
            _moved += moved.Value is JournalMoved ? 1 : 0;
            Complete(invocation, moved, journal, JournalEntryKind.Append, payload, record.Crc32C, invokedAt);
            Expect(moved.Value is JournalMoved { Owner: var owner } && owner == second.Node, $"a write of journal {journal} sent to the first process after the takeover answered {moved.Value}, not Moved to the second");
        }

        /// <summary>
        /// Partitions every LedgerStore node while the process which serves a journal reads it (journals.md §2.3, §6): the
        /// read's seal check reaches no replica, so the read fails with <see cref="JournalStoreUnavailableException"/>
        /// carrying the ledger's aggregate of its replicas' missed deadlines, never the aggregate itself; once the nodes
        /// reconnect, the journal reads again. The step waits until the map lists no closed ledger, so no drain scans while
        /// every replica is out of reach.
        /// </summary>
        private async Task ReadWithEveryReplicaOutOfReachAsync(int client)
        {
            var journal = _random.Choose(_journals);
            var key = JournalRowKey.Encode(journal);
            while ((await Drainer.View.ReadAsync(cancellationToken)).Ledgers.Any(static ledger => !ledger.IsOpen))
            {
                await DelayAsync(5);
            }

            var owner = (await ReadOnceAsync(client, journal)).ServedBy;
            Exception? failure = null;
            foreach (var node in nodes)
            {
                node.Isolate();
            }

            try
            {
                ((await owner.Journals!.ReadAsync(key, cancellationToken)).Value as IDisposable)?.Dispose();
            }
            catch (Exception thrown)
            {
                failure = thrown;
            }
            finally
            {
                foreach (var node in nodes)
                {
                    node.Reconnect();
                }
            }

            var answered = failure?.GetType().Name ?? "without failing";
            Expect(
                failure is JournalStoreUnavailableException { InnerException: AggregateException quorum } && QuorumFailures.IsAdmitted(quorum, mediaDamage: false),
                Invariant($"a read of journal {journal} from {owner.Name} with every replica out of reach answered {answered}, not the store's unavailability"));
            Record($"a read of journal {journal} with every replica out of reach answered {answered}");
            _ = await ReadOnceAsync(client, journal);
        }

        /// <summary>
        /// Writes a journal on both sides of its segment's end, and closes its ledger, which the drainer drains: its write
        /// rolls its token at the defining row of the later segment (JS-I10), which the chain mutants break. A
        /// <c>Replace</c> first opens a generation, so the store's limits admit both appends whatever the writers and the
        /// faults' lost answers left in the last.
        /// </summary>
        private async Task WriteAcrossASegmentsEndAsync(int client)
        {
            var journal = _random.Choose(_journals);
            var key = JournalRowKey.Encode(journal);
            var version = (await ReadOnceAsync(client, journal)).Version;
            var opened = await WriteOnceAsync(client, journal, JournalEntryKind.Replace, version);
            Expect(opened.Value is JournalWriteOutcome.Accepted, $"the replace which opens journal {journal}'s generation before its segment's end answered {opened.Value}");
            version = opened.Value is JournalWriteOutcome.Accepted replaced ? replaced.Version : version;
            var ended = false;
            for (var write = 0; write < 2; write++)
            {
                var outcome = await WriteOnceAsync(client, journal, JournalEntryKind.Append, version);
                Expect(outcome.Value is JournalWriteOutcome.Accepted, $"an append of journal {journal} across a segment's end answered {outcome.Value}");
                version = outcome.Value is JournalWriteOutcome.Accepted accepted ? accepted.Version : (await ReadOnceAsync(client, journal)).Version;
                if (write == 0)
                {
                    var owner = OwnerOf(key);
                    if (owner.View.FindOpen(key.Span) is { } open && owner.Registry!.TryGet(open.Id) is { } writer)
                    {
                        _ = await writer.EndSegmentAsync(cancellationToken);
                        ended = true;
                    }
                }
            }

            Expect(ended, $"the segment of journal {journal}'s first append was not ended");
            await ReplaceOpenAsync(key);
        }

        /// <summary>
        /// Replaces the open ledger over <paramref name="key"/> by its primary's process, again while a concurrent change,
        /// as a drain's drop, wins the map first, and waits for each primary's registry to reconcile with it.
        /// </summary>
        private async Task ReplaceOpenAsync(JournalRowKey key)
        {
            object? outcome = null;
            for (var attempt = 0; attempt < 5 && outcome is not LedgerMapCommitOutcome.Applied; attempt++)
            {
                var closer = OwnerOf(key);
                outcome = await ExecuteAsync(closer, map => new Transition.Replace(map.FindOpen(key.Span)!.Value.Id, Choice(closer, map.Epoch)));
            }

            Expect(outcome is LedgerMapCommitOutcome.Applied, $"the replace of the ledger over {key} answered {outcome}");
            await ReconcileAsync();
        }

        /// <summary>
        /// Writes a journal three times at once through its primary, once nothing else writes it (journals.md §8.1 step 3):
        /// at the version of a <c>Replace</c> the step writes first, at a count past it, which applies to no state, and at the
        /// <c>Replace</c>'s version again. The <c>Replace</c> opens a generation, so the store's limits admit the first
        /// append whatever the random writers left in the last. The first is admitted and in flight when the others are
        /// decided, in order: the second is rejected at once, with the first in flight, and the third, which applies only to
        /// the state before the first, waits and is rejected once the first lands, its key's in-flight entries then none. Both
        /// rejections come from an owner the map lists open, so the model judges them strictly, which the step checks of the
        /// deposed flag. A drain's drop between the read and the <c>Replace</c> may rebase the key, so the <c>Replace</c> is
        /// rejected with the rebased version, and the step writes it again at that version; once it lies in the open ledger, no
        /// drop rebases the key.
        /// </summary>
        private async Task ConflictWithAnEntryInFlightAsync(int client)
        {
            var journal = _random.Choose(_journals);
            await ReconcileAsync();
            var version = (await ReadOnceAsync(client, journal)).Version;
            JournalWriteOutcome? opened = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                bool deposed;
                (opened, deposed) = await WriteAnsweredAsync(client, journal, JournalEntryKind.Replace, version);
                if (opened.Value is not JournalWriteOutcome.Rejected rebased)
                {
                    break;
                }

                version = deposed ? (await ReadOnceAsync(client, journal)).Version : rebased.CurrentVersion;
            }

            Expect(opened?.Value is JournalWriteOutcome.Accepted, $"the replace before three writes of journal {journal} answered {opened?.Value}");
            version = opened?.Value is JournalWriteOutcome.Accepted replaced ? replaced.Version : version;
            var (outcomes, crcs, _) = await WriteAtOnceAsync(client, journal, [version, new JournalVersion(version.Base, version.Count + 5), version], Invariant($"three writes of journal {journal} at {version}"));
            var second = _rejections.SingleOrDefault(record => record.Rejection.RecordCrc == crcs[1]);
            var third = _rejections.SingleOrDefault(record => record.Rejection.RecordCrc == crcs[2]);
            Expect(
                outcomes[0].Value is JournalWriteOutcome.Accepted accepted
                    && outcomes[1].Value is JournalWriteOutcome.Rejected
                    && outcomes[2].Value is JournalWriteOutcome.Rejected { CurrentVersion: var after } && after == accepted.Version
                    && second?.Rejection.InFlight.Length == 1
                    && third is { Rejection.InFlight.IsEmpty: true },
                $"three writes of journal {journal} at {version} answered {outcomes[0].Value}, {outcomes[1].Value}, and {outcomes[2].Value}, not Accepted, then Rejected while it was in flight, then Rejected with its version once it landed");
            foreach (var rejection in (RejectionRecord?[])[second, third])
            {
                Assert.False(
                    rejection is { Deposed: true },
                    Invariant($"a rejection of journal {journal} by the owner the map lists open was recorded as deposed, so the model would judge it only as a hint"));
            }

            Record($"three writes of journal {journal} at {version}: {outcomes[0].Value}, {outcomes[1].Value}, {outcomes[2].Value}");
        }
        /// <summary>
        /// Opens a journal's generation with a <c>Replace</c> and appends to the store's per-generation limit, once nothing
        /// else writes (journals.md §8.1 step 4): <see cref="MaxAppends"/> appends are accepted, the last of them together
        /// with one more, which applies after it and which the limit refuses, so it waits while the other is in flight and is
        /// then answered <c>ReplaceRequired</c> from the applied state.
        /// </summary>
        private async Task AppendToTheStoresLimitAsync(int client)
        {
            var journal = _random.Choose(_journals);
            await ReconcileAsync();
            var version = (await ReadOnceAsync(client, journal)).Version;
            var opened = await WriteOnceAsync(client, journal, JournalEntryKind.Replace, version);
            Expect(opened.Value is JournalWriteOutcome.Accepted, $"the replace which opens journal {journal}'s generation answered {opened.Value}");
            version = opened.Value is JournalWriteOutcome.Accepted replaced ? replaced.Version : version;
            for (var append = 1; append < MaxAppends; append++)
            {
                var outcome = await WriteOnceAsync(client, journal, JournalEntryKind.Append, version);
                Expect(outcome.Value is JournalWriteOutcome.Accepted, $"append {append} of journal {journal}'s generation answered {outcome.Value}");
                version = outcome.Value is JournalWriteOutcome.Accepted accepted ? accepted.Version : version;
            }

            var (last, _, _) = await WriteAtOnceAsync(client, journal, [version, new JournalVersion(version.Base, version.Count + 1)], Invariant($"the appends at journal {journal}'s limit"));
            Expect(
                last[0].Value is JournalWriteOutcome.Accepted && last[1].Value is JournalWriteOutcome.ReplaceRequired,
                $"the appends at journal {journal}'s limit answered {last[0].Value} and {last[1].Value}, not Accepted and ReplaceRequired");
        }

        /// <summary>
        /// Rebases a journal twice (journals.md §8.3): each time its open ledger is replaced, a stale write makes the key warm
        /// in the successor, which holds no row of it, every ledger holding its rows drains and drops, a read answers the
        /// rebased version, count 0, and a <c>Replace</c> at it, which the store's limits always admit, is accepted, after a
        /// reload of the warm key.
        /// </summary>
        private async Task RebaseTwiceAsync(int client)
        {
            var journal = _random.Choose(_journals);
            var key = JournalRowKey.Encode(journal);
            var version = (await ReadOnceAsync(client, journal)).Version;
            if (version.Count == 0)
            {
                // A version at count 0, Origin's or a rebase's, names a state no live row holds, so an append gives the key
                // a row in the ledger the first rebase replaces, which the warm key's reload must then see dropped.
                var first = await WriteOnceAsync(client, journal, JournalEntryKind.Append, version);
                version = first.Value is JournalWriteOutcome.Accepted accepted ? accepted.Version : version;
            }

            for (var rebase = 0; rebase < 2; rebase++)
            {
                await ReplaceOpenAsync(key);
                var warming = await WriteOnceAsync(client, journal, JournalEntryKind.Append, JournalVersion.Origin);
                Expect(warming.Value is JournalWriteOutcome.Rejected, $"the stale write which warms journal {journal} answered {warming.Value}");
                while ((await Drainer.View.ReadAsync(cancellationToken)).Ledgers.Any(ledger => !ledger.IsOpen && ledger.Range.Contains(key.Span)))
                {
                    await DelayAsync(10);
                }

                // A primary whose view still lists a dropped ledger reads its rows live, so each reads the drop first.
                await ReconcileAsync();
                var read = await ReadOnceAsync(client, journal);
                Expect(read.Version.Count == 0 && read.Version != JournalVersion.Origin, $"rebase {rebase} of journal {journal} read the version {read.Version}");
                var written = await WriteOnceAsync(client, journal, JournalEntryKind.Replace, read.Version);
                Expect(written.Value is JournalWriteOutcome.Accepted, $"the write of journal {journal} at its rebased version {read.Version} answered {written.Value}");
            }
        }

        /// <summary>Deletes a journal and, once its tombstone's retention has passed, expires it (journals.md §8.6): both are accepted.</summary>
        private async Task DeleteAndExpireAsync(int client)
        {
            var journal = _random.Choose(_journals);
            var version = (await ReadOnceAsync(client, journal)).Version;
            var deleted = await WriteOnceAsync(client, journal, JournalEntryKind.Delete, version);
            Expect(deleted.Value is JournalWriteOutcome.Accepted, $"the delete of journal {journal} answered {deleted.Value}");
            if (deleted.Value is not JournalWriteOutcome.Accepted accepted)
            {
                return;
            }

            await Task.Delay(Retention + TimeSpan.FromMilliseconds(1), environment.TimeProvider, cancellationToken);
            await ExpireTombstonesAsync();
            var read = await ReadOnceAsync(client, journal);
            Expect(!read.Exists && read.Version == accepted.Version.Next(), $"the expiry scan left journal {journal}, deleted at {accepted.Version}, at {read.Version}");
        }

        /// <summary>
        /// Splits an open ledger with room to split and merges its two children, or, with none, merges two open siblings and
        /// splits their parent: each is applied.
        /// </summary>
        private async Task SplitAndMergeAsync()
        {
            var process = _processes[0];
            LedgerMetadata[] open = [.. (await process.View.ReadAsync(cancellationToken)).Ledgers.Where(static ledger => ledger.IsOpen)];
            LedgerMetadata[] splittable = [.. open.Where(static ledger => ledger.Range.Depth < MaxDepth)];
            if (splittable.Length > 0)
            {
                // Execute the split on the ledger's primary (matching ClusterController), so the local writer surrenders
                // its lease before sealing instead of racing ServeUnservedLedgersAsync during a remote lease wait.
                var owner = Primaries.FirstOrDefault(candidate => candidate.Node == splittable[0].Primary.Node) ?? process;
                await SplitAsync(owner, splittable[0]);
                await MergeAsync(owner, splittable[0].Range.Children);
                return;
            }

            var lower = open.First(ledger => ledger.Range.Depth > 0 && open.Any(sibling => sibling.Range == ledger.Range.Sibling));
            var merger = Primaries.FirstOrDefault(candidate => candidate.Node == lower.Primary.Node) ?? process;
            await MergeAsync(merger, (lower.Range, lower.Range.Sibling));
            await SplitAsync(merger, (await merger.View.ReadAsync(cancellationToken)).Ledgers.First(ledger => ledger.IsOpen && ledger.Range == lower.Range.Parent));
        }

        /// <summary>Splits <paramref name="ledger"/>, which must be applied.</summary>
        private async Task SplitAsync(Process process, LedgerMetadata ledger) =>
            Expect(await ExecuteAsync(process, view => new Transition.Split(ledger.Id, Choice(process, view.Epoch))) is LedgerMapCommitOutcome.Applied, $"the split of s{ledger.Id}");

        /// <summary>Merges the open ledgers over <paramref name="halves"/>, which must both be open and be merged.</summary>
        private async Task MergeAsync(Process process, (KeyRange Lower, KeyRange Upper) halves)
        {
            LedgerMetadata[] open = [.. (await process.View.ReadAsync(cancellationToken)).Ledgers.Where(static ledger => ledger.IsOpen)];
            LedgerMetadata[] lower = [.. open.Where(ledger => ledger.Range == halves.Lower)];
            LedgerMetadata[] upper = [.. open.Where(ledger => ledger.Range == halves.Upper)];
            Expect(lower.Length == 1 && upper.Length == 1, $"{halves.Lower} and {halves.Upper} are not both open to merge");
            if (lower.Length == 1 && upper.Length == 1)
            {
                Expect(await ExecuteAsync(process, view => new Transition.Merge(lower[0].Id, upper[0].Id, Choice(process, view.Epoch))) is LedgerMapCommitOutcome.Applied, $"the merge of s{lower[0].Id} and s{upper[0].Id}");
            }
        }
        /// <summary>
        /// Writes appends of a journal at once, at <paramref name="versions"/>, through its primary, and records them: the
        /// first is admitted before the others are decided, so they meet it in flight.
        /// </summary>
        private async Task<(JournalWriteOutcome[] Outcomes, uint[] Crcs, bool[] Deposed)> WriteAtOnceAsync(int client, string journal, JournalVersion[] versions, string what)
        {
            var key = JournalRowKey.Encode(journal);
            var owner = OwnerOf(key);
            var payloads = versions.Select(_ => BitConverter.GetBytes(++_nextId)).ToArray();
            var buffers = payloads.Select(static payload => ArcBuffer.Wrap(payload)).ToArray();
            var records = versions.Select((version, index) => owner.Journals!.EncodeAppend(key, version, buffers[index])).ToArray();
            try
            {
                var invocations = versions.Select((version, index) => (At: ++_order, Invocation: _history.Invoke(client + index, JournalInput.Append(journal, version.ToString(), payloads[index])))).ToArray();
                var outcomes = await WithinQuiescenceAsync(SimulationTasks.WhenAll(records.Select(record => owner.Journals!.WriteAsync(record, cancellationToken))), what);
                var deposed = new bool[outcomes.Length];
                for (var index = 0; index < outcomes.Length; index++)
                {
                    deposed[index] = Complete(invocations[index].Invocation, outcomes[index], journal, JournalEntryKind.Append, payloads[index], records[index].Crc32C, invocations[index].At);
                }

                return (outcomes, [.. records.Select(static record => record.Crc32C)], deposed);
            }
            finally
            {
                foreach (var record in records)
                {
                    record.Dispose();
                }

                foreach (var buffer in buffers)
                {
                    buffer.Dispose();
                }
            }
        }

        /// <summary>Waits until each primary's registry has reconciled with the map as it is now, so routes find their writers.</summary>
        private async Task ReconcileAsync()
        {
            foreach (var process in Primaries)
            {
                await process.Registry!.ReconciledAsync((await process.View.ReadAsync(cancellationToken)).Epoch, cancellationToken);
            }
        }

        /// <summary>Reads every journal once the run is quiet: each read is then the fold of its whole reference.</summary>
        private async Task ReadEveryJournalAsync(int client)
        {
            await ReconcileAsync();
            foreach (var journal in _journals)
            {
                var read = await ReadOnceAsync(client, journal);
                var (exists, content) = Fold(Reference(journal));
                Assert.True(
                    exists == read.Exists && content.AsSpan().SequenceEqual(read.Content),
                    Invariant($"JS-I3: the final read of journal {journal} answers {(read.Exists ? "exists" : "absent")} with {read.Content.Length} bytes, not its whole reference's fold"));
            }
        }

        /// <summary>Records that a scripted step did not take its path, which the run reports once the oracles have passed.</summary>
        private void Expect(bool condition, string what)
        {
            if (!condition)
            {
                _unmet.Add(what);
            }
        }
        /// <summary>Takes a scripted step if the run takes <paramref name="step"/>.</summary>
        private Task TakeAsync(Steps step, Func<Task> take) => mutation.Steps.HasFlag(step) ? take() : Task.CompletedTask;

        /// <summary>
        /// The progress check: waits for <paramref name="work"/> within the bound, the workload's unless
        /// <paramref name="bound"/> names one, or fails naming what still waits.
        /// </summary>
        private async Task WithinQuiescenceAsync(Task work, string what, TimeSpan? bound = null)
        {
            var within = bound ?? workload.Quiescence;
            try
            {
                await work.WaitAsync(within, environment.TimeProvider, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(Invariant($"progress: {what} were not all answered within {within} once nothing else moved."));
            }
        }

        private async Task<T[]> WithinQuiescenceAsync<T>(Task<T[]> work, string what)
        {
            await WithinQuiescenceAsync((Task)work, what);
            return await work;
        }

        /// <summary>
        /// Drains each closed ledger whose lineage predecessors dropped into the store, recording its rows as decided, drops
        /// it, and has every node drop its replicas, until the run ends and no closed ledger remains.
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
                    var end = ((LedgerStatus.Closed)ledger.Status.Value).T;
                    var segments = await ScanAsync(Drainer, ledger, end);
                    try
                    {
                        _decided[ledger.Id] = new DecidedLedger([.. segments.SelectMany(segment => RowsOf(segment))], end);
                        _drainedUnchecked.Enqueue(ledger.Id);
                        foreach (var segment in segments)
                        {
                            KeyedRow.DrainByKey(CollectionsMarshal.AsSpan(segment), (key, rows) => store.Drain(key, rows));
                        }
                    }
                    finally
                    {
                        foreach (var segment in segments)
                        {
                            KeyedRow.Release(segment);
                        }
                    }

                    await DropAsync(ledger.Id);
                    Record($"s{ledger.Id} drained below {end} and dropped");
                }

                if (droppable.Length > 0)
                {
                    _ = await Drainer.View.ReadAsync(cancellationToken);
                    foreach (var node in nodes)
                    {
                        await ReconcileReplicasAsync(node, Drainer.View);
                    }
                }
            }
        }

        /// <summary>
        /// Commits a ledger's drop through the drainer's committer, submitting the same change again while the register
        /// leaves its outcome unknown, which decides it (ledgers.md §2.6).
        /// </summary>
        private async Task DropAsync(LedgerId ledger)
        {
            while (true)
            {
                var faulting = _faulting;
                try
                {
                    Assert.IsType<LedgerMapCommitOutcome.Applied>((await committers[1].CommitAsync(new LedgerMapChange.Drop(ledger), cancellationToken)).Value);
                    return;
                }
                catch (Exception failure) when (faulting && failure is IOException or TimeoutException)
                {
                    Record($"the drop of s{ledger} failed: {failure.GetType().Name}");
                    await DelayAsync(5);
                }
            }
        }

        /// <summary>
        /// Drops the replicas of <paramref name="node"/> which <paramref name="view"/> revokes, as the node's reconciliation
        /// does, unless the node is down: its restart reconciles it.
        /// </summary>
        private async Task ReconcileReplicasAsync(LedgerStoreNode node, LedgerMapView view)
        {
            if (!node.IsUp)
            {
                return;
            }

            try
            {
                _ = await new ReplicaReconciler(replicas.Node(node.NodeId), view).ReconcileAsync(cancellationToken);
            }
            catch (NodeDownException) when (!node.IsUp)
            {
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
                _decided[ledger.Id] = new DecidedLedger([.. segments.SelectMany(segment => RowsOf(segment))], end);
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
        /// The model's verdict on every client history, per journal (verification.md §3.4), once the white-box oracles have
        /// passed. The model always permits its forget step after an <c>Expired</c>; the memory store never forgets a key, and
        /// the version checker, which this run never tells to forget one, keeps its versions unique across every lifetime.
        /// </summary>
        private void VerifyLinearizable() =>
            LinearizabilityAssert.Linearizable(JournalModel.Instance, _history.History(), cancellationToken: cancellationToken);

        /// <summary>The progress check's end: once the run is quiet, no admission holds a waiting entry.</summary>
        private void VerifyNothingWaits()
        {
            foreach (var process in Primaries)
            {
                var waiting = process.Journals!.WaitingEntries;
                Assert.True(waiting == 0, Invariant($"progress: {process.Name}'s admissions hold {waiting} waiting entries once the run is quiet"));
            }
        }

        /// <summary>The chain checker and the version checker over each journal's whole reference, the ledgers no drain read among it (HI-I4, JS-I10).</summary>
        private void VerifyFinalChains()
        {
            foreach (var journal in _journals)
            {
                Assert.Null(CheckChain(journal));
            }

            foreach (var ledger in _decided.Keys.ToArray())
            {
                Assert.Null(CheckVersions(ledger));
            }
        }

        /// <summary>
        /// The white-box rejection oracle (journals.md §10; JS-I1): each rejection reports the version of the reference's
        /// fold through the position its admission decided from, its ledger's rows below its applied slot after every earlier
        /// ledger's; or, with no row of its ledger applied, the version of the earlier rows the key loaded, or their rebase
        /// once every one of them dropped before the decision, to the store's version base. A rejection of a key with no
        /// recorded segment in its ledger and nothing in flight, at a version other than the one it reports, loaded the key
        /// again after the entry arrived (§8.3), unless it is an <c>Expired</c>, which never loads its key; a cold
        /// <c>Expired</c>'s, decided from no state, is only checked to be one (§8.6). Its entry applies to none of the states its key's applied state and each
        /// prefix of its in-flight entries leave; an <c>Expired</c> is rejected also for entries in flight or its retention.
        /// </summary>
        private void VerifyRejections()
        {
            foreach (var (ledger, rejection, _, decidedAt) in _rejections)
            {
                if (rejection.Cold)
                {
                    Assert.True(rejection.Kind == JournalEntryKind.Expired, Invariant($"JS-I1: a {rejection.Kind} was rejected as cold, which only an Expired is"));
                    continue;
                }

                var journal = _journalsByKey[Convert.ToHexString(rejection.Key.Span)];
                var reference = Reference(journal);
                ReferenceRow[] own = [.. reference.Where(row => row.Tag.Ledger == ledger && row.Tag.Slot < rejection.AppliedBelow)];
                ReferenceRow[] earlier = [.. reference.Where(row => row.Tag.Ledger < ledger)];
                var rebased = own.Length == 0 && earlier.Length > 0 && rejection.Applied.Count == 0;
                var expected = own.Length > 0 ? JournalVersion.FromRowVersion(own[^1].Version)
                    : rebased ? rejection.Applied
                    : earlier.Length > 0 ? JournalVersion.FromRowVersion(earlier[^1].Version)
                    : JournalVersion.Origin;
                Assert.True(
                    rejection.Applied == expected,
                    Invariant($"JS-I1: a rejection of journal {journal} in s{ledger} reports {rejection.Applied}, not {expected}, which the reference gives below s{ledger}:{rejection.AppliedBelow}"));
                if (rebased)
                {
                    var live = earlier.FirstOrDefault(row => !_dropped.TryGetValue(row.Tag.Ledger, out var droppedAt) || droppedAt > decidedAt);
                    Assert.True(live is null, Invariant($"JS-I1: a rejection of journal {journal} in s{ledger} reports the rebase {rejection.Applied} while its row {live} had not dropped"));
                    Assert.Null(Checked(_versions.Rebased(rejection.Key.Span, rejection.Applied.Base, StoreBase(earlier), StateOf(earlier))));
                }

                Assert.False(
                    rejection.Kind != JournalEntryKind.Expired && rejection.Expected != rejection.Applied && !rejection.RecordedHere && rejection.InFlight.IsEmpty && rejection.LoadStartedAt < rejection.AcceptedAt,
                    Invariant($"JS-I1: a {rejection.Kind} of journal {journal} at {rejection.Expected} was rejected with {rejection.Applied} without loading it again, though its key had no recorded segment in s{ledger} and nothing in flight"));
                Assert.Null(Checked(_versions.Named(rejection.Key.Span, rejection.Applied.ToRowVersion(), StateOf([.. earlier, .. own]))));

                var (version, deleted) = (rejection.Applied, rejection.AppliedDeletedAt is not null);
                var possible = new List<JournalVersion> { version };
                for (var index = 0; index < rejection.InFlight.Length; index++)
                {
                    version = new JournalVersion(version.Base, version.Count + 1);
                    possible.Add(version);
                }

                var applies = rejection.Kind == JournalEntryKind.Expired
                    ? rejection.InFlight.IsEmpty && deleted && rejection.Expected == rejection.Applied && rejection.AppliedDeletedAt!.Value + rejection.TombstoneRetention <= rejection.DecidedAt
                    : possible.Contains(rejection.Expected);
                Assert.False(
                    applies,
                    Invariant($"JS-I1: a {rejection.Kind} of journal {journal} at {rejection.Expected} was rejected, though it applies to a state its applied {rejection.Applied} and {rejection.InFlight.Length} in-flight entries leave"));
            }
        }

        /// <summary>
        /// The white-box <c>ReplaceRequired</c> oracle (journals.md §8.1 step 4, §10; JS-I4): each was decided with nothing of
        /// its key in flight, and the store's limits refuse its <c>Append</c> in one of its two candidate writes, from the
        /// reference through its decision: the key's write in the segment of its last decided row grown by it, and an empty
        /// write from that write's <c>Plan</c> grown by it.
        /// </summary>
        private void VerifyReplaceRequireds()
        {
            foreach (var (ledger, decision, _) in _replaceRequireds)
            {
                var journal = _journalsByKey[Convert.ToHexString(decision.Key.Span)];
                Assert.True(decision.InFlight == 0, Invariant($"JS-I4: a ReplaceRequired of journal {journal} in s{ledger} was decided while {decision.InFlight} entries of its key were in flight"));
                ReferenceRow[] decided = [.. Reference(journal).Where(row => row.Tag.Ledger < ledger || row.Tag.Ledger == ledger && row.Tag.Slot < decision.AppliedBelow)];
                ChainRow[] segment = [.. decided.Where(row => row.Segment == decided[^1].Segment).Select(static row => row.Chain)];
                var fold = store.Begin(segment.Length == 0 ? default : segment[0].Token);
                if (segment.Length > 0)
                {
                    foreach (var command in TokenChainChecker.WriteOf(segment).Commands)
                    {
                        fold.Add(command);
                    }
                }

                var append = ArchiveCommand.Append((int)decision.PayloadLength);
                Assert.False(
                    fold.Admits(append) && store.Begin(fold.End()).Admits(append),
                    Invariant($"JS-I4: a ReplaceRequired of journal {journal} in s{ledger} for an Append both candidate writes admit"));
            }
        }

        /// <summary>
        /// The read-bound oracle (JS-I3): each read answers the fold of the reference over its cut, the rows of the ledgers it
        /// read below their ends and every row of an earlier ledger, which the store covered: its content and existence, and
        /// the version of the newest row of a ledger it read, or, with none, a rebase's count 0. Its cut holds the row of
        /// every write acknowledged before the read was invoked, and the version checker finds its version names its cut's
        /// state alone.
        /// </summary>
        private void VerifyReads()
        {
            var rows = AcceptedRows();
            foreach (var read in _reads)
            {
                var reference = Reference(read.Journal);
                var highest = read.Through.Count == 0 ? (LedgerId?)null : read.Through.Keys.Max();
                ReferenceRow[] cut = [.. reference.Where(row => read.Through.TryGetValue(row.Tag.Ledger, out var end) ? row.Tag.Slot < end : row.Tag.Ledger < highest)];
                var (exists, content) = Fold(cut);
                Assert.True(
                    exists == read.Exists && content.AsSpan().SequenceEqual(read.Content),
                    Invariant($"JS-I3: a read of journal {read.Journal} answers {(read.Exists ? "exists" : "absent")} with {read.Content.Length} bytes, not the fold of its cut [{string.Join(", ", cut.AsEnumerable())}]"));
                var live = cut.Where(row => read.Through.ContainsKey(row.Tag.Ledger)).ToArray();
                Assert.True(
                    live.Length == 0 ? read.Version.Count == 0 : read.Version == JournalVersion.FromRowVersion(live[^1].Version),
                    Invariant($"JS-I3: a read of journal {read.Journal} answers version {read.Version}, not its newest live row's of [{string.Join(", ", live.AsEnumerable())}]"));
                foreach (var (write, row) in rows.Where(pair => pair.Key.Journal == read.Journal && pair.Key.AnsweredAt < read.InvokedAt))
                {
                    Assert.True(
                        cut.Contains(row),
                        Invariant($"JS-I3: a read of journal {read.Journal} invoked after the write answered {write.Version} was acknowledged misses its row {row}"));
                }

                if (read.Version.Count == 0 && read.Version != JournalVersion.Origin)
                {
                    // The reference now holds every ledger, so a rebase while a closed ledger not yet drained held the
                    // journal's rows, which the check as the read answered could not see, fails here.
                    Assert.Null(RebaseViolation(read));
                }

                Assert.Null(Checked(_versions.Named(JournalRowKey.Encode(read.Journal).Span, read.Version.ToRowVersion(), StateOf(cut))));
            }
        }

        /// <summary>
        /// Each write against the decided history (JS-I2, JS-I4): an accepted write is exactly one decided row of its kind and
        /// payload, or, for a <c>Delete</c> or an <c>Expired</c>, of its kind at its version, stored at the version it answered,
        /// which names that row's state alone; a write answered with no effect is no row; an ambiguous one is at most one. A
        /// <c>Delete</c> or an <c>Expired</c> carries no payload of its writer's, so each journal's rows of each are counted
        /// instead: at least its accepted writes of that kind, and at most those and its ambiguous ones.
        /// </summary>
        private void VerifyWrites()
        {
            foreach (var journal in _journals)
            {
                var reference = Reference(journal);
                VerifyCount(journal, JournalEntryKind.Delete, reference.Count(static row => row.Kind == RowKind.Delete && row.Body.Length > 0));
                VerifyCount(journal, JournalEntryKind.Expired, reference.Count(static row => row.Kind == RowKind.Delete && row.Body.Length == 0));
            }

            var rows = AcceptedRows();
            foreach (var write in _writes)
            {
                var matches = RowsOf(write);
                switch (write.Outcome)
                {
                    case JournalWriteOutcome.Accepted:
                        var row = rows[write];
                        Assert.True(
                            matches.Length == 1 && JournalVersion.FromRowVersion(row.Version) == write.Version,
                            Invariant($"JS-I2: the {write.Kind} of journal {write.Journal} accepted at {write.Version} is not one decided row at that version: [{string.Join(", ", matches.AsEnumerable())}]"));
                        Assert.Null(Checked(_versions.Named(JournalRowKey.Encode(write.Journal).Span, write.Version.Value.ToRowVersion(), StateOf([row]))));
                        break;
                    case JournalWriteOutcome.Ambiguous:
                        Assert.True(matches.Length <= 1, Invariant($"JS-I4: the ambiguous {write.Kind} of journal {write.Journal} is {matches.Length} decided rows"));
                        break;
                    default:
                        Assert.True(write.Payload is null || matches.Length == 0, Invariant($"JS-I4: the {write.Kind} of journal {write.Journal} answered {write.Outcome} is decided rows [{string.Join(", ", matches.AsEnumerable())}]"));
                        break;
                }
            }
        }

        /// <summary>Checks a journal's <paramref name="rows"/> of <paramref name="kind"/> against its writes of that kind (JS-I2, JS-I4).</summary>
        private void VerifyCount(string journal, JournalEntryKind kind, int rows)
        {
            var accepted = _writes.Count(write => write.Journal == journal && write.Kind == kind && write.Outcome is JournalWriteOutcome.Accepted);
            var ambiguous = _writes.Count(write => write.Journal == journal && write.Kind == kind && write.Outcome is JournalWriteOutcome.Ambiguous);
            Assert.True(
                rows >= accepted && rows <= accepted + ambiguous,
                Invariant($"JS-I4: journal {journal} holds {rows} rows of its {kind}s, not between its {accepted} accepted ones and those and its {ambiguous} ambiguous ones"));
        }

        /// <summary>
        /// The version checker over every rebase a defining row stores (JS-I10): its version before it, at count 0, names
        /// the state of the key's row before it alone.
        /// </summary>
        private void VerifyNames()
        {
            foreach (var journal in _journals)
            {
                var reference = Reference(journal);
                for (var index = 1; index < reference.Count; index++)
                {
                    var (previous, row) = (reference[index - 1], reference[index]);
                    if (row.Version.Count == 1 && row.Version.Base != previous.Version.Base)
                    {
                        Assert.Null(Checked(_versions.Named(JournalRowKey.Encode(journal).Span, new RowVersion(row.Version.Base, 0), StateOf([previous]))));
                    }
                }
            }
        }

        /// <summary>Returns each accepted write's decided row, which <see cref="VerifyWrites"/> requires to be its only one.</summary>
        private Dictionary<WriteRecord, ReferenceRow> AcceptedRows()
        {
            var rows = new Dictionary<WriteRecord, ReferenceRow>();
            foreach (var write in _writes.Where(static write => write.Outcome is JournalWriteOutcome.Accepted))
            {
                var matches = RowsOf(write);
                Assert.True(matches.Length > 0, Invariant($"JS-I2: the {write.Kind} of journal {write.Journal} accepted at {write.Version} is no decided row"));
                rows[write] = matches[0];
            }

            return rows;
        }

        /// <summary>
        /// Returns a write's decided rows: those of its kind and payload, or, for a <c>Delete</c> or an <c>Expired</c>, which
        /// carry no payload of the writer's, those of its kind at the version it answered.
        /// </summary>
        private ReferenceRow[] RowsOf(WriteRecord write)
        {
            var kind = write.Kind switch
            {
                JournalEntryKind.Append => RowKind.Append,
                JournalEntryKind.Replace => RowKind.Replace,
                JournalEntryKind.Delete or JournalEntryKind.Expired => RowKind.Delete,
                _ => throw new ArgumentOutOfRangeException(nameof(write)),
            };
            return write.Payload is { } payload
                ? [.. Reference(write.Journal).Where(row => row.Kind == kind && row.Body.AsSpan().SequenceEqual(payload))]
                : write.Version is { } version
                    ? [.. Reference(write.Journal).Where(row => row.Kind == kind && row.Body.Length == 0 == (write.Kind == JournalEntryKind.Expired) && JournalVersion.FromRowVersion(row.Version) == version)]
                    : [];
        }

        /// <summary>Returns the version base the memory store holds for a key once it holds every one of <paramref name="rows"/>: the token <c>Plan</c> replays over their last write.</summary>
        private RowToken StoreBase(IReadOnlyList<ReferenceRow> rows) =>
            rows.Count == 0 ? default : store.Plan(TokenChainChecker.WriteOf([.. rows.Where(row => row.Segment == rows[^1].Segment).Select(static row => row.Chain)]));

        /// <summary>Identifies the state a key's rows leave exactly: by its newest row, or <c>origin</c> with none.</summary>
        private static string StateOf(IReadOnlyList<ReferenceRow> rows) => rows.Count == 0 ? "origin" : Invariant($"row {rows[^1].Tag}");

        /// <summary>Returns a version checker's violation as the oracle reports it.</summary>
        private static string? Checked(string? violation) => violation is null ? null : Invariant($"JS-I10: the version checker found {violation}");

        private void RecordCounts()
        {
            var inFlight = _rejections.Count(static record => !record.Rejection.InFlight.IsEmpty);
            Record($"{CountsEntry}{_accepted} {_rejected} {_replaceRequired} {_moved} {_expired} {_rebased} {inFlight} {_splits} {_merges}");
            var deposed = _rejections.Count(static record => record.Deposed);
            Record($"journals hold their contract: {_history.History().Length} operations, {_reads.Count} reads, {_lostAnswers} writes whose answers were lost, and {_rejections.Count} rejections, {_rejections.Count - deposed} judged strictly and {deposed} from deposed owners, over {_decided.Count} ledgers");
        }
        /// <summary>Folds a journal's rows by journals.md §2.1: its existence and its content.</summary>
        private static (bool Exists, byte[] Content) Fold(IEnumerable<ReferenceRow> rows)
        {
            var exists = false;
            var content = new List<byte>();
            foreach (var row in rows)
            {
                switch (row.Kind)
                {
                    case RowKind.Append:
                        exists = true;
                        content.AddRange(row.Body);
                        break;
                    case RowKind.Replace:
                        exists = true;
                        content.Clear();
                        content.AddRange(row.Body);
                        break;
                    case RowKind.Delete:
                        exists = false;
                        content.Clear();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(rows));
                }
            }

            return (exists, [.. content]);
        }

        private List<ReferenceRow> Reference(string journal) =>
            [.. _decided.Values.SelectMany(static ledger => ledger.Rows).Where(row => row.Journal == journal).OrderBy(static row => row.Tag)];

        private ReferenceRow[] RowsOf(List<KeyedRow> rows) =>
            [.. rows.Select(row => new ReferenceRow(_journalsByKey[Convert.ToHexString(row.Key)], row.Row.Tag, row.Row.Kind, row.Row.Version, row.Row.Token, row.Row.Segment, row.Row.Body.ToArray()))];

        /// <summary>Waits between 1 ms and <paramref name="maximumMilliseconds"/>: virtual time always passes, so a retry loop never spins within one step.</summary>
        private Task DelayAsync(int maximumMilliseconds) =>
            Task.Delay(
                _random.NextTimeSpan(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(maximumMilliseconds)),
                environment.TimeProvider,
                cancellationToken);

        private void Record(FormattableString entry) => environment.Trace.Record(Invariant(entry));
    }
}
