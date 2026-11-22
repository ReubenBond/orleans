using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Drains into archive logs, drops, transitions, sweeps, and history reads interleaved in deterministic simulation
/// (archive-logs.md §2.3–§2.5, §6, §8.1, §8.5, §10). One node's writer process proposes rows of four keys, of every kind,
/// storing each key's token chain of a <see cref="LogArchiveStore"/> in its defining rows (JS-I10), and now and then ends a
/// segment with a marker and replaces, splits, or merges a key's ledger, over a ledger map register which leaves outcomes
/// unknown; two drainer processes, each with its own lagging map view, drain every ledger they list with
/// <see cref="ArchiveLogDrainWorker"/>, racing over the same segments, through stores which delay their requests, now and
/// then long enough to land as a straggler's, while drains are cancelled mid-segment as crashes, their workers restart and
/// resume from the markers, and on <c>Complete</c> they submit the ledger's drop; a sweeper process sweeps alongside; and
/// two reader processes read histories through <see cref="ArchiveReader"/>. The memory archive-log store fails requests
/// before and after they land, and answers a landed write's condition failed, as a retry which meets its own landing does;
/// the object store of the markers leaves creates' outcomes unknown; replica reads answer backpressure at random. Scripted
/// steps force, with the faults off until their path is reached, a straggler which lands after its ledger dropped, a crash
/// between a segment's writes, a key's write which fails on every attempt, and a key the store refuses, which blocks its
/// ledger with <c>Refused</c> until an operator lifts the refusal; each step's drain then completes once the faults stop,
/// as a node's next drain does. A final phase, with every fault off, completes and drops every closed ledger.
/// </summary>
/// <remarks>
/// <para>
/// The oracles: every history read folds to the reference history over its <c>Through</c>, holds no row proposed after it
/// ended, and misses no row of a ledger closed before it began nor, linearizable, one answered durable before it began
/// (HI-I1); the chain checker judges every read's rows against its prefix's token, and the version checker finds every
/// version base a read answers naming one state of its key (HI-I4); every drop is submitted while the store holds every
/// write of the ledger's segments (LOG-I3, HI-I2); after every step, no log's position decreases and no generation is
/// created twice (LOG-I1, LOG-I2), and every generation an opener created has its base (LOG-I2); no drain finds a log
/// corrupt or a predecessor missing (LOG-I1, HI-I1); the lineage step's drains wait on the older overlapping ledgers the map
/// lists (archive-logs.md §8.1); and the final reads fold to every key's whole history.
/// </para>
/// </remarks>
public sealed class ArchiveLogDrainSimulationTests
{
    // Every change runs two seeds, and exploration runs more through DURABLE_STORAGE_SIM_SEEDS (test/Durable.Storage/docs/spec/verification.md §7.4, §12).
    private const int FirstSeed = 1;
    private const int SeedCount = 2;
    private const int Rounds = 4;
    private const int ProposalsPerRound = 8;
    private const int Passes = 3;
    private const int ReadsPerRound = 6;
    private const int Attempts = 20;
    private const double MarkerRate = 0.3;

    // A drainer's concurrent drains share their worker's drain memory; a drain alone is the unit tests' (DrainScan's bound).
    private const long DrainMemoryBytes = 64 << 10;
    private const string ReadOracle = "read oracle: ";
    private const string DropCheck = "LOG-I3: ";
    private const string LogCheck = "LOG-I2: ";
    private const string DrainCheck = "drain: ";
    private const string LineageCheck = "lineage: ";
    private static readonly string[] Keys = ["k0", "k1", "k2", "k3"];

    // The oracle's own reads of the store, which the store's faults spare.
    private static readonly AsyncLocal<bool> OracleRead = new();

    // The ledger whose drain makes a request, which the straggler count reads.
    private static readonly AsyncLocal<LedgerId?> DrainedLedger = new();

    // The theory and the corpus check share each seed's run, so the corpus runs once.
    private static readonly ConcurrentDictionary<int, Lazy<Counts>> Runs = new();

    private enum Mutant
    {
        None,

        /// <summary><c>marks-unheld</c>: drainers whose workers mark a segment whose write threw, as if it were held.</summary>
        MarksUnheld,

        /// <summary><c>ignores-lineage</c>: the lineage step's drainer, whose worker drains a ledger while an older overlapping ledger is listed.</summary>
        IgnoresLineage,
    }

    /// <summary>Gets the seed corpus.</summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RacingDrainsDropsTransitionsAndReadsKeepTheArchiveLogsInvariants(int seed) => _ = RunSeed(seed);

    /// <summary>Across the corpus, every path the scenario counts is taken, by the random rounds or the scripted steps.</summary>
    [Fact]
    public void TheCorpusExercisesEveryPath()
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, SeedCount))
        {
            foreach (var (path, count) in RunSeed(seed).Paths)
            {
                totals[path] = totals.GetValueOrDefault(path) + count;
            }
        }

        string[] missing = [.. Paths.All.Where(path => totals.GetValueOrDefault(path) == 0)];
        Assert.True(missing.Length == 0, $"The corpus never exercised: {string.Join(", ", missing)}. It took: {string.Join(", ", totals.Select(static total => $"{total.Key} {total.Value}"))}.");
    }

    /// <summary>
    /// The <c>marks-unheld</c> mutant, whose drains mark a segment whose write threw, completes and drops a ledger whose
    /// write never landed, which the drop check (LOG-I3), the oracle's fold of the log, flags on every seed: the failed write
    /// step makes a key's write throw by construction.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheDropCheckFlagsADrainWhichMarksAnUnheldSegment(int seed)
    {
        Counts? counts = null;
        _ = SeededSimulation.Run(seed, async environment => counts = await SimulateAsync(environment, Mutant.MarksUnheld));

        Assert.True(
            counts!.Violations.Any(static violation => violation.StartsWith(DropCheck, StringComparison.Ordinal)),
            Invariant($"Seed {seed}: the marks-unheld mutant broke no drop check: [{string.Join("; ", counts.Violations)}]."));
    }

    /// <summary>
    /// The <c>ignores-lineage</c> mutant, a worker which drains a ledger while an older overlapping ledger is listed, which
    /// the lineage step's drainer restarts as, fails the step's check on every seed: its drain of the merged ledger's child
    /// answers no <c>Waiting</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheLineageStepFlagsADrainWhichIgnoresTheLineage(int seed)
    {
        Counts? counts = null;
        _ = SeededSimulation.Run(seed, async environment => counts = await SimulateAsync(environment, Mutant.IgnoresLineage));

        Assert.True(
            counts!.Violations.Any(static violation => violation.StartsWith(LineageCheck, StringComparison.Ordinal)),
            Invariant($"Seed {seed}: the ignores-lineage mutant broke no lineage check: [{string.Join("; ", counts.Violations)}]."));
    }

    /// <summary>
    /// A writer which stores a key's next token from the wrong parent breaks the chain, which a drain meets as a log
    /// contradicting the write's chain or a missing predecessor, or a read's chain checker flags (HI-I4), on every seed. A
    /// broken chain may then hold the readers in their backoff past the run's budget, which the flag already decided.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void AWriterWhichBreaksTheChainFails(int seed)
    {
        var counts = new Counts();
        try
        {
            _ = SeededSimulation.Run(seed, async environment => _ = await SimulateAsync(environment, Mutant.None, ArchiveChains.ChainMutant.WrongParent, counts));
        }
        catch (SimulationFailedException) when (counts.Violations.Count > 0)
        {
        }

        Assert.True(
            counts.Violations.Any(static violation => violation.StartsWith(DrainCheck, StringComparison.Ordinal) || violation.Contains("HI-I4", StringComparison.Ordinal)),
            Invariant($"Seed {seed}: a writer from the wrong parent broke no check: [{string.Join("; ", counts.Violations)}]."));
    }

    private static Counts RunSeed(int seed) =>
        Runs.GetOrAdd(seed, static seed => new Lazy<Counts>(() =>
        {
            Counts? counts = null;
            _ = SeededSimulation.Run(seed, async environment =>
            {
                counts = await SimulateAsync(environment, Mutant.None);
                Assert.True(counts.Violations.Count == 0, Invariant($"Seed {seed}: {string.Join(" | ", counts.Violations)}"));
            });
            return counts!;
        })).Value;

    private static async Task<Counts> SimulateAsync(SimulationEnvironment environment, Mutant mutant, ArchiveChains.ChainMutant chainMutant = ArchiveChains.ChainMutant.None, Counts? counts = null)
    {
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map") { UnknownOutcomeRate = 0.05 };
        await using var objects = new SimulatedObjectStore(environment, "clog") { UnknownOutcomeRate = 0.05, RetriedAnswerRate = 0.05, ReadFailureRate = 0.03 };
        await using var gateObjects = new SimulatedObjectStore(environment, "claim") { UnknownOutcomeRate = 0.05, RetriedAnswerRate = 0.05, ReadFailureRate = 0.03 };
        var faults = new StoreFaults(environment.CreateRandom("archive-log-faults"));
        // Pages of one to three bases, which the sweeper lists one a sweep, so the logs a slice leaves open meet new
        // generations, and deleted bases, before the next slice resumes after them (archive-logs.md §8.5).
        var paging = environment.CreateRandom("archive-log-paging");
        var logs = new MemoryArchiveLogStore(new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 16, 49_900, 40_000, MemoryArchiveLogStore.NameRule), faults.Decide) { ListPageSize = 1 + paging.Next(3) };
        var store = new LogArchiveStore(logs, objects, ArchiveLogNames.Hexadecimal);
        var chains = new ArchiveChains(store) { Mutant = chainMutant };
        await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None, SmallSegments.Options, chains: chains);
        var scenario = new Scenario(environment, register, ledgers, logs, objects, gateObjects, store, faults, mutant, counts ?? new Counts());
        await scenario.RunAsync();
        return scenario.Counts;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static class Paths
    {
        public const string Complete = "complete";
        public const string Drained = "drained";
        public const string OpenSegment = "open segments";
        public const string Waiting = "waiting";
        public const string Dropped = "dropped";
        public const string Drops = "drops";
        public const string Crashes = "crashes mid-segment";
        public const string FailedSegments = "failed segments";
        public const string Restarts = "restarts from markers";
        public const string Stragglers = "straggler requests";
        public const string HeldOnFailure = "conditions classified";
        public const string Blocked = "blocked keys";
        public const string Splits = "splits";
        public const string Merges = "merges";
        public const string Replaces = "replaces";
        public const string Sweeps = "bases swept";
        public const string MarkersSwept = "markers swept";
        public const string Reads = "history reads";
        public const string HeldRows = "held rows";
        public const string Openers = "openers";
        public const string WithoutRows = "segments without rows";
        public const string LandedAfterCrash = "writes landed after a crash";
        public const string StaleMapHeld = "stale-map reads with held rows";
        public const string Lineage = "drains waiting through the lineage";

        /// <summary>The gate passes the faults of its own store failed; counted, not required of the corpus.</summary>
        public const string GateRetries = "gate retries";

        public static readonly string[] All =
        [
            Complete, Drained, OpenSegment, Waiting, Dropped, Drops, Crashes, FailedSegments, Restarts, Stragglers, HeldOnFailure, Blocked,
            Splits, Merges, Replaces, Sweeps, MarkersSwept, Reads, HeldRows, Openers, WithoutRows, LandedAfterCrash,
            StaleMapHeld, Lineage,
        ];
    }

    private sealed class Counts
    {
        public Dictionary<string, int> Paths { get; } = [with(StringComparer.Ordinal)];

        public List<string> Violations { get; } = [];

        public void Add(string path, int count = 1) => Paths[path] = Paths.GetValueOrDefault(path) + count;
    }

    /// <summary>
    /// The archive-log store's faults: a write fails before it lands, lands and fails, or lands and answers its condition
    /// failed; a read fails; each at its rate, while <see cref="Rate"/> is on, sparing the oracle's reads; and every write
    /// of <see cref="FailingLog"/> fails before it lands, a scripted step's fault.
    /// </summary>
    private sealed class StoreFaults(SimulationRandom random)
    {
        public double Rate { get; set; } = 0.04;

        /// <summary>Gets or sets the log whose every write fails before it lands, on each of the writer's attempts; or none.</summary>
        public string? FailingLog { get; set; }

        public int Requests { get; private set; }

        public int PropertiesReads { get; private set; }

        /// <summary>Gets or sets an observer of every request, which counts stragglers.</summary>
        public Action<ArchiveLogRequest>? Seen { get; set; }

        public ArchiveLogFault Decide(ArchiveLogRequest request)
        {
            if (OracleRead.Value)
            {
                return ArchiveLogFault.None;
            }

            Requests++;
            PropertiesReads += request.Kind == ArchiveLogRequestKind.GetProperties ? 1 : 0;
            Seen?.Invoke(request);
            if (request.Log == FailingLog && request.Kind is ArchiveLogRequestKind.CreateLog or ArchiveLogRequestKind.AppendUnit or ArchiveLogRequestKind.CreateBase or ArchiveLogRequestKind.OpenGeneration)
            {
                return ArchiveLogFault.FailBefore;
            }

            if (!random.Chance(Rate))
            {
                return ArchiveLogFault.None;
            }

            return request.Kind switch
            {
                ArchiveLogRequestKind.GetProperties or ArchiveLogRequestKind.Read or ArchiveLogRequestKind.List or ArchiveLogRequestKind.DeleteBase => ArchiveLogFault.FailBefore,
                ArchiveLogRequestKind.CreateLog or ArchiveLogRequestKind.AppendUnit or ArchiveLogRequestKind.CreateBase or ArchiveLogRequestKind.OpenGeneration => random.Next(3) switch
                {
                    0 => ArchiveLogFault.FailBefore,
                    1 => ArchiveLogFault.FailAfter,
                    _ => ArchiveLogFault.RetryMeetsLanding,
                },
                _ => throw new UnreachableException(),
            };
        }
    }

    private sealed class Drainer(string name, LedgerMapView view, LedgerReader reader, SimulatedArchiveLogStore logs, Func<SimulatedArchiveLogStore, LedgerMapView, LedgerReader, ArchiveLogDrainWorker> create)
    {
        public string Name { get; } = name;

        public LedgerMapView View { get; } = view;

        public LedgerReader Reader { get; } = reader;

        public SimulatedArchiveLogStore Logs { get; } = logs;

        public ArchiveLogDrainWorker Worker { get; private set; } = create(logs, view, reader);

        /// <summary>Gets the segments without rows this drainer's workers passed without a write.</summary>
        public long WithoutRows => _withoutRows + Worker.SegmentsWithoutRows;

        private long _withoutRows;

        /// <summary>Restarts the worker, as a crashed process does: its in-memory progress is gone, and it resumes from the markers.</summary>
        public void Restart()
        {
            _withoutRows = WithoutRows;
            Worker = create(Logs, View, Reader);
        }
    }

    private sealed record Decided(Tag Tag, RowKind Kind, string Payload)
    {
        public byte[] Body => Encoding.UTF8.GetBytes(Payload);
    }

    private sealed record Proposal(Decided Row, long CalledAt, long AnsweredAt);

    /// <summary>The split and merge step's key, the first split's two children, and the ledger they merged into.</summary>
    private sealed record SplitAndMerged(string Key, LedgerMetadata First, LedgerMetadata Second, LedgerMetadata Merged);

    private sealed record HistoryReadRecord(string Reader, string Key, long CalledAt, long AnsweredAt, Tag[] Rows, ArchiveFold Fold, Dictionary<LedgerId, Slot> Through, bool Linearizable)
    {
        public string? Chain { get; init; }

        public (RowToken Base, string State)? VersionBase { get; init; }
    }

    private sealed class ReaderProcess(string name, LedgerMapView view, ArchiveReader history)
    {
        public string Name { get; } = name;

        public LedgerMapView View { get; } = view;

        public ArchiveReader History { get; } = history;
    }

    private sealed class Scenario
    {
        private readonly SimulationEnvironment _environment;
        private readonly SimulatedConditionalRegister _register;
        private readonly LiveLedgers _ledgers;
        private readonly MemoryArchiveLogStore _logs;
        private readonly SimulatedObjectStore _objects;
        private readonly GateObjectStore _gateObjects;
        private readonly LogArchiveStore _store;
        private readonly StoreFaults _faults;
        private readonly Mutant _mutant;
        private readonly SimulationRandom _random;
        private readonly List<Drainer> _drainers = [];
        private readonly List<ReaderProcess> _readers = [];
        private readonly Dictionary<string, List<Decided>> _reference = [with(StringComparer.Ordinal)];
        private readonly List<Proposal> _proposals = [];
        private readonly List<HistoryReadRecord> _reads = [];
        private readonly Dictionary<LedgerId, long> _closedAt = [];
        private readonly Dictionary<string, ArchiveLogPosition> _positions = [with(StringComparer.Ordinal)];
        private readonly VersionChecker _versions = new();
        private readonly HashSet<string> _blocked = [with(StringComparer.Ordinal)];
        private readonly Dictionary<IArchiveLogStore, ArchiveLogGate> _gates = [with(ReferenceEqualityComparer.Instance)];
        private ArchiveLogSweeper? _sweeper;

        // Whether the lineage step runs, the only one whose workers the ignores-lineage mutant replaces.
        private bool _inLineageStep;
        private int _readsChecked;
        private long _steps;

        public Scenario(SimulationEnvironment environment, SimulatedConditionalRegister register, LiveLedgers ledgers, MemoryArchiveLogStore logs, SimulatedObjectStore objects, SimulatedObjectStore gateObjects, LogArchiveStore store, StoreFaults faults, Mutant mutant, Counts counts)
        {
            Counts = counts;
            _environment = environment;
            _register = register;
            _ledgers = ledgers;
            _logs = logs;
            _objects = objects;
            _gateObjects = new GateObjectStore(objects, gateObjects);
            _store = store;
            _faults = faults;
            _mutant = mutant;
            _random = environment.CreateRandom("archive-log-scenario");
            var backpressure = environment.CreateRandom("archive-log-backpressure");
            ledgers.Transport.Saturated = (_, _) => backpressure.Chance(0.02);
            faults.Seen = CountStraggler;
            environment.Invariants.Add("archive-logs", CheckStep);
        }

        public Counts Counts { get; }

        public async Task RunAsync()
        {
            await TransitionAsync(null, () => _ledgers.BootstrapAsync(CancellationToken.None));

            _readers.Add(new ReaderProcess("writer", _ledgers.Map, new ArchiveReader(_ledgers.Reader, _store, SimulationEnvironment.Clock)));
            var readerView = await _ledgers.OpenViewAsync(CancellationToken.None);
            _readers.Add(new ReaderProcess("reader", readerView, new ArchiveReader(_ledgers.OpenReader(readerView), _store, SimulationEnvironment.Clock)));
            for (var index = 0; index < 2; index++)
            {
                var view = await _ledgers.OpenViewAsync(CancellationToken.None);
                var logs = new SimulatedArchiveLogStore(_logs, _environment, Invariant($"drainer-{index}")) { StragglerRate = 0.03 };
                _gates[logs] = await PassGateAsync(logs);
                _drainers.Add(new Drainer(Invariant($"drainer-{index}"), view, _ledgers.OpenReader(view), logs, NewWorker));
            }

            _sweeper = new ArchiveLogSweeper(await PassGateAsync(_logs), await _ledgers.OpenViewAsync(CancellationToken.None), NullLogger<ArchiveLogSweeper>.Instance) { PagesPerSweep = 1 };
            for (var round = 0; round < Rounds; round++)
            {
                await SimulationTasks.WhenAll(
                    WriteAsync(),
                    DrainAsync(_drainers[0]),
                    DrainAsync(_drainers[1]),
                    SweepAsync(),
                    ReadAsync(_readers[0]),
                    ReadAsync(_readers[1]));
            }

            if (await SplitAndMergeAsync() is { } lineage)
            {
                await WaitThroughTheLineageAsync(lineage);
            }

            await FailAKeysWriteAsync();
            await StragglerAfterTheDropAsync();
            await CrashBetweenASegmentsWritesAsync();
            await BlockAKeyAsync();
            await DrainADroppedLedgerAsync();
            await ReadHeldRowsOnAStaleMapAsync();
            await SettleAsync();
            CheckLogs();
            VerifyReads();
            Counts.Add(Paths.HeldOnFailure, _faults.PropertiesReads);
            Counts.Add(Paths.Reads, _reads.Count);
            Counts.Add(Paths.WithoutRows, (int)_drainers.Sum(static drainer => drainer.WithoutRows));
            Counts.Add(Paths.LandedAfterCrash, _drainers.Sum(static drainer => drainer.Logs.LandedAfterCrash));
            _environment.Trace.Record("archive-log paths: " + string.Join(", ", Counts.Paths.OrderBy(static path => path.Key, StringComparer.Ordinal).Select(static path => Invariant($"{path.Key} {path.Value}"))));
        }

        /// <summary>
        /// Passes a process's archive gate over <paramref name="logs"/> before its drains or sweeps, again after each failure
        /// the faults inject: its record lives in a faulty store of their own (<see cref="GateObjectStore"/>), so the
        /// scenario's other requests, and the seed corpus's schedules of the markers' store, stay as they were.
        /// </summary>
        private async Task<ArchiveLogGate> PassGateAsync(IArchiveLogStore logs)
        {
            var store = new LogArchiveStore(logs, _gateObjects, ArchiveLogNames.Hexadecimal);
            while (true)
            {
                try
                {
                    var record = await store.StoreRecordAsync(ImmutableDictionary<string, string>.Empty, CancellationToken.None);
                    return await ArchiveLogGate.PassAsync(store, new ArchiveStoreIdentity(ArchiveStoreKind.Logs, new ArchiveStorePrefix("t"), record.Id), ImmutableDictionary<string, string>.Empty, CancellationToken.None);
                }
                catch (Exception failure) when (failure is IOException or TimeoutException)
                {
                    Counts.Add(Paths.GateRetries);
                    await Task.Delay(TimeSpan.FromMilliseconds(1), _environment.TimeProvider);
                }
            }
        }

        private ArchiveLogDrainWorker NewWorker(SimulatedArchiveLogStore logs, LedgerMapView view, LedgerReader reader) =>
            new(_gates[logs], view, reader, new ArchiveLogOptions { DrainConcurrency = 2, DrainMemoryBytes = DrainMemoryBytes }, SimulationEnvironment.Clock, new ArchiveLogWriterOptions { Delay = TimeSpan.FromMilliseconds(1) })
            {
                BackpressureDelay = TimeSpan.FromMilliseconds(1),
                MarksUnheldMutant = _mutant == Mutant.MarksUnheld,
                IgnoresLineageMutant = _mutant == Mutant.IgnoresLineage && _inLineageStep,
            };

        /// <summary>
        /// LOG-I1, LOG-I2 before every request of the store, so after every one but the last, which the end of the run
        /// checks: no log's position decreases, every generation an opener created has its base, and no generation is
        /// created twice. The store's state is read inside the simulation, under its lock.
        /// </summary>
        private void CheckLogs()
        {
            foreach (var (log, position, opened, baseExists) in _logs.Snapshot())
            {
                if (_positions.TryGetValue(log, out var before) && position < before)
                {
                    Counts.Violations.Add(Invariant($"{LogCheck}the log '{log}' moved from {before} back to {position}"));
                }

                _positions[log] = position;
                if (opened && !baseExists)
                {
                    Counts.Violations.Add(Invariant($"{LogCheck}the log '{log}' at {position} names a base which does not exist"));
                }

                if (_logs.Creations(log, position.Generation) > 1)
                {
                    Counts.Violations.Add(Invariant($"LOG-I1: the generation {position.Generation} of the log '{log}' was created twice"));
                }
            }
        }

        /// <summary>The chain checker and the version checker after every step (HI-I4), over each read since the last step.</summary>
        private string? CheckStep()
        {
            for (; _readsChecked < _reads.Count; _readsChecked++)
            {
                var read = _reads[_readsChecked];
                if (read.Chain is { } chain)
                {
                    return Invariant($"HI-I4: {read.Reader}'s read of {read.Key}: the chain checker found {chain}");
                }

                if (read.VersionBase is var (versionBase, state) && _versions.State(Encoding.UTF8.GetBytes(read.Key), versionBase, state) is { } repeated)
                {
                    return Invariant($"HI-I4: {read.Reader}'s read of {read.Key}: {repeated}");
                }
            }

            return null;
        }

        /// <summary>Checks the logs, and counts each write made for a ledger the map has dropped: a straggler's.</summary>
        private void CountStraggler(ArchiveLogRequest request)
        {
            CheckLogs();
            if (request.Kind is ArchiveLogRequestKind.AppendUnit or ArchiveLogRequestKind.OpenGeneration or ArchiveLogRequestKind.CreateBase
                && DrainedLedger.Value is { } ledger && ledger < _ledgers.Map.Current.NextLedgerId && _ledgers.Map.Current.Get(ledger) is null)
            {
                Counts.Add(Paths.Stragglers);
            }
        }

        private async Task WriteAsync()
        {
            for (var count = 0; count < ProposalsPerRound; count++)
            {
                await DelayAsync(4);
                var key = _random.Choose(Keys);
                var kind = _random.Next(10) switch
                {
                    < 6 => RowKind.Append,
                    < 8 => RowKind.Replace,
                    _ => RowKind.Delete,
                };

                await ProposeAsync(key, kind);
                if (_random.Chance(MarkerRate))
                {
                    await EndSegmentAsync(key);
                }

                if (_random.Next(5) == 0)
                {
                    await CloseAsync(key);
                }
            }
        }

        /// <summary>Proposes one row of <paramref name="key"/>, a Delete's body empty or a tombstone, and records it.</summary>
        private async Task ProposeAsync(string key, RowKind kind)
        {
            var bytes = Encoding.UTF8.GetBytes(key);
            _ = _reference.TryAdd(key, []);
            var payload = kind == RowKind.Delete
                ? (_random.Chance(0.5) ? string.Empty : Invariant($"{key}tomb{_reference[key].Count % 100:D2}"))
                : Invariant($"{key}-{_reference[key].Count}");
            for (var attempt = 0; ; attempt++)
            {
                var ledger = _ledgers.OpenContaining(bytes);
                var calledAt = ++_steps;
                try
                {
                    var tags = await _ledgers.ProposeAsync(ledger.Id, [LiveRow.Of(key, kind, payload)], CancellationToken.None);
                    var row = new Decided(tags[0], kind, payload);
                    _reference[key].Add(row);
                    _proposals.Add(new Proposal(row, calledAt, ++_steps));
                    return;
                }
                catch (InvalidOperationException) when (attempt < Attempts)
                {
                    await _ledgers.ReconcileAsync(CancellationToken.None);
                }
            }
        }

        private async Task EndSegmentAsync(string key)
        {
            try
            {
                _ = await _ledgers.EndSegmentAsync(_ledgers.OpenContaining(Encoding.UTF8.GetBytes(key)).Id, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                await _ledgers.ReconcileAsync(CancellationToken.None);
            }
        }

        /// <summary>Replaces, splits, or merges the open ledger containing <paramref name="key"/>.</summary>
        private async Task CloseAsync(string key)
        {
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            var map = _ledgers.Map.Current;
            if (ledger.Range.Depth > 0 && map.Ledgers.FirstOrDefault(listed => listed.IsOpen && listed.Range == ledger.Range.Sibling) is { Location.Value: not null } sibling && _random.Chance(0.5))
            {
                Counts.Add(Paths.Merges);
                await TransitionAsync(ledger.Id, () => _ledgers.MergeAsync(ledger.Id, sibling.Id, CancellationToken.None));
            }
            else if (ledger.Range.Depth < 2 && _random.Chance(0.5))
            {
                Counts.Add(Paths.Splits);
                await TransitionAsync(ledger.Id, () => _ledgers.SplitAsync(ledger.Id, CancellationToken.None));
            }
            else
            {
                Counts.Add(Paths.Replaces);
                await TransitionAsync(ledger.Id, () => _ledgers.ReplaceAsync(ledger.Id, CancellationToken.None));
            }
        }

        /// <summary>Executes a transition until the writer's view shows it applied, retrying a commit whose outcome is unknown.</summary>
        private async Task TransitionAsync(LedgerId? closes, Func<Task> transition)
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                try
                {
                    await transition();
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
                {
                    // The outcome is unknown, or a retry met the change its first attempt landed.
                }

                await _ledgers.ReconcileAsync(CancellationToken.None);
                var map = _ledgers.Map.Current;
                if (closes is { } closed ? map.Get(closed) is not { IsOpen: true } : map.Ledgers.Length > 0)
                {
                    if (closes is { } id)
                    {
                        _closedAt[id] = ++_steps;
                    }

                    return;
                }
            }

            Counts.Violations.Add(Invariant($"A transition closing {closes} never applied."));
        }

        /// <summary>Drains, in passes, every ledger the drainer's view lists, refreshing the view now and then.</summary>
        private async Task DrainAsync(Drainer drainer)
        {
            for (var pass = 0; pass < Passes; pass++)
            {
                await DelayAsync(6);
                if (_random.Chance(0.6))
                {
                    _ = await drainer.View.ReadAsync(CancellationToken.None);
                }

                // Now and then the drainer drains its ledgers at once, as a node does, sharing its drain memory.
                if (_random.Chance(0.3))
                {
                    _ = await SimulationTasks.WhenAll(drainer.View.Current.Ledgers.Select(ledger => DrainLedgerAsync(drainer, ledger, crashes: true)));
                    continue;
                }

                foreach (var ledger in drainer.View.Current.Ledgers)
                {
                    _ = await DrainLedgerAsync(drainer, ledger, crashes: true);
                }
            }
        }

        /// <summary>
        /// Drains <paramref name="ledger"/> once below its end, a closed ledger's <c>T</c> or the tail the drainer's reader
        /// learns of an open one, or now and then short of it; with <paramref name="crashes"/>, cancels the drain now and
        /// then partway, as a crash, and restarts the worker. On <c>Complete</c>, submits the ledger's drop.
        /// </summary>
        /// <returns>The drain's answer, or <see langword="null"/> when it threw.</returns>
        private async Task<ArchiveLogDrainOutcome?> DrainLedgerAsync(Drainer drainer, LedgerMetadata ledger, bool crashes)
        {
            Slot end;
            if (ledger.Status.Value is LedgerStatus.Closed closed)
            {
                end = closed.T;
            }
            else if ((await drainer.Reader.ReadTailAsync(ledger, CancellationToken.None)).Value is LedgerTail tail)
            {
                end = tail.ContiguousTail;
            }
            else
            {
                return null;
            }

            var below = crashes && _random.Chance(0.2) ? new Slot(_random.Next((int)end.Value + 1)) : end;
            using var crash = new CancellationTokenSource();
            var crashing = crashes && _random.Chance(0.15) ? CrashAfterAsync(crash, _random.NextTimeSpan(TimeSpan.FromMilliseconds(8))) : Task.CompletedTask;

            DrainedLedger.Value = ledger.Id;
            // A concurrent drain's crash may restart the drainer's worker; this drain, and its drop, stay with the one it began on.
            var worker = drainer.Worker;
            ArchiveLogDrainOutcome outcome;
            try
            {
                outcome = await worker.DrainAsync(ledger, below, crash.Token);
            }
            catch (OperationCanceledException) when (crash.IsCancellationRequested)
            {
                Counts.Add(Paths.Crashes);
                Counts.Add(Paths.Restarts);
                drainer.Restart();
                return null;
            }
            catch (ArchiveLogDrainBlockedException exception) when (exception.Cause == ArchiveLogDrainBlockedCause.Refused && exception.Key is { } key && _blocked.Contains(Encoding.UTF8.GetString(key.Span)))
            {
                Counts.Add(Paths.Blocked);
                return null;
            }
            catch (ArchiveLogDrainBlockedException exception)
            {
                Counts.Violations.Add(Invariant($"{DrainCheck}{drainer.Name}'s drain of ledger {ledger.Id} was blocked: {exception.Message}"));
                return null;
            }
            catch (ArchiveCorruptException exception)
            {
                Counts.Violations.Add(Invariant($"{DrainCheck}{drainer.Name}'s drain of ledger {ledger.Id} found the archive corrupt: {exception.Message}"));
                return null;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                Counts.Add(Paths.FailedSegments);
                if (_random.Chance(0.5))
                {
                    Counts.Add(Paths.Restarts);
                    drainer.Restart();
                }

                return null;
            }
            finally
            {
                DrainedLedger.Value = null;
                await crashing;
            }

            switch (outcome.Value)
            {
                case ArchiveLogDrainOutcome.Complete:
                    Counts.Add(Paths.Complete);
                    await DropAsync(drainer, worker, ledger);
                    break;
                case ArchiveLogDrainOutcome.Drained:
                    Counts.Add(Paths.Drained);
                    break;
                case ArchiveLogDrainOutcome.OpenSegment:
                    Counts.Add(Paths.OpenSegment);
                    break;
                case ArchiveLogDrainOutcome.Waiting:
                    Counts.Add(Paths.Waiting);
                    break;
                case ArchiveLogDrainOutcome.Dropped:
                    Counts.Add(Paths.Dropped);
                    break;
                case null:
                default:
                    throw new UnreachableException();
            }

            return outcome;
        }

        /// <summary>
        /// Cancels a drain after <paramref name="delay"/>, as a crash of its process, from a task of the simulation, so its
        /// cancellation runs as a controlled operation.
        /// </summary>
        private async Task CrashAfterAsync(CancellationTokenSource crash, TimeSpan delay)
        {
            await Task.Delay(delay, _environment.TimeProvider);
            // Cancelled inline, on the simulation's thread: CancelAsync would run the callbacks on the thread pool.
            crash.Cancel();
        }

        /// <summary>Submits the drop of a ledger the drainer's drain answered complete, checking LOG-I3 at its submission.</summary>
        private async Task DropAsync(Drainer drainer, ArchiveLogDrainWorker worker, LedgerMetadata ledger)
        {
            try
            {
                var outcome = await worker.SubmitDropAsync(ledger.Id, (change, token) => CheckedDropAsync(drainer, ledger, change, token));
                if (outcome.Value is LedgerMapCommitOutcome.Applied)
                {
                    Counts.Add(Paths.Drops);
                }
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                // The drop's outcome is unknown; a later drain answers Complete again, or Dropped.
            }
        }

        /// <summary>
        /// LOG-I3, HI-I2 at a drop's submission: the log of every key with rows in the ledger, as a read the faults spare finds
        /// it, folds to the reference history through a cut past the ledger's last row of the key; the oracle's own fold, not
        /// the store's <c>Holds</c>.
        /// </summary>
        private async Task<LedgerMapCommitOutcome> CheckedDropAsync(Drainer drainer, LedgerMetadata ledger, LedgerMapChange change, CancellationToken cancellationToken)
        {
            if (_ledgers.Map.Current.Get(ledger.Id) is not null)
            {
                foreach (var (key, write) in await WritesOfAsync(drainer.Reader, ledger))
                {
                    if (await StoredFoldAsync(Encoding.UTF8.GetBytes(key)) is var stored && !FoldsPast(key, ledger.Id, stored))
                    {
                        Counts.Violations.Add(Invariant($"{DropCheck}ledger {ledger.Id}'s drop was submitted while the log of {key} folds to {stored}, which no cut of its reference past the ledger's rows gives."));
                    }

                    if (write.IsOpener)
                    {
                        Counts.Add(Paths.Openers);
                    }
                }
            }

            return await _ledgers.CommitAsync(change, cancellationToken);
        }

        /// <summary>Returns the fold of the log of <paramref name="key"/>, read past the faults.</summary>
        private async Task<ArchiveFold> StoredFoldAsync(byte[] key)
        {
            OracleRead.Value = true;
            try
            {
                return (await _store.ReadAsync(key, _ledgers.Map.Current, CancellationToken.None)).Value switch
                {
                    ArchiveStoreRead.Read read => Folded(read),
                    _ => ArchiveFold.Nothing,
                };
            }
            finally
            {
                OracleRead.Value = false;
            }

            static ArchiveFold Folded(ArchiveStoreRead.Read read)
            {
                using (read)
                {
                    return ArchiveFold.Of(read.Prefix);
                }
            }
        }

        /// <summary>
        /// Returns whether <paramref name="stored"/> is the fold of a prefix of the key's reference history which holds each of
        /// its rows in <paramref name="ledger"/>: a log may hold more of a successor's segments, never less of the ledger.
        /// </summary>
        private bool FoldsPast(string key, LedgerId ledger, ArchiveFold stored)
        {
            List<Decided> reference = [.. _reference.GetValueOrDefault(key, []).OrderBy(static row => row.Tag)];
            for (var count = reference.FindLastIndex(row => row.Tag.Ledger == ledger) + 1; count <= reference.Count; count++)
            {
                if (Fold(reference.Take(count)).SameAs(stored))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Returns each key's write of each segment of the closed <paramref name="ledger"/>, from a scan of its decided batches.</summary>
        private static async Task<List<(string Key, ArchiveWrite Write)>> WritesOfAsync(LedgerReader reader, LedgerMetadata ledger)
        {
            var end = ((LedgerStatus.Closed)ledger.Status.Value).T;
            var writes = new List<(string, ArchiveWrite)>();
            var segment = new List<KeyedRow>();
            using var view = new LedgerSegmentView(ledger.Id);
            using var bodies = new ArcBufferWriter();
            LedgerSegmentId? current = null;
            var next = Slot.Zero;
            try
            {
                for (var attempt = 0; attempt < 1000 && next < end; attempt++)
                {
                    await foreach (var item in reader.ScanAsync(ledger, next, end, CancellationToken.None))
                    {
                        if (!item.Read.TryGetValue(out LedgerRead.Data data))
                        {
                            break;
                        }

                        using (data)
                        {
                            if (data.View.IsSegmentStart)
                            {
                                Flush();
                                current = new LedgerSegmentId(ledger.Id, data.Slot);
                            }

                            foreach (var row in item.Rows)
                            {
                                segment.Add(new KeyedRow(row.Key.ToArray(), ArchiveRow.Of(row, current ?? new LedgerSegmentId(ledger.Id, data.Slot), bodies)));
                            }

                            next = data.Slot + 1;
                        }
                    }

                    if (next < end)
                    {
                        // A scan which met backpressure starts again at its segment's start, so the view resolves its rows.
                        KeyedRow.Release(segment);
                        segment.Clear();
                        next = current?.Start ?? Slot.Zero;
                    }
                }

                Flush();
                return writes;
            }
            finally
            {
                KeyedRow.Release(segment);
            }

            void Flush()
            {
                KeyedRow.DrainByKey(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(segment), (key, rows) => writes.Add((Encoding.UTF8.GetString(key), ArchiveWrite.OfRows(rows))));
                KeyedRow.Release(segment);
                segment.Clear();
            }
        }

        private async Task SweepAsync()
        {
            for (var pass = 0; pass < Passes; pass++)
            {
                await DelayAsync(10);
                try
                {
                    var result = await _sweeper!.SweepAsync(CancellationToken.None);
                    Counts.Add(Paths.Sweeps, result.BasesDeleted);
                    Counts.Add(Paths.MarkersSwept, result.MarkersDeleted);
                    if (result.Unreadable > 0)
                    {
                        Counts.Violations.Add(Invariant($"{LogCheck}the sweeper found {result.Unreadable} logs unreadable."));
                    }
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    // A listing, read, or deletion failed; the next sweep lists its slice again from its start.
                }
            }
        }

        private async Task ReadAsync(ReaderProcess process)
        {
            for (var count = 0; count < ReadsPerRound; count++)
            {
                await DelayAsync(5);
                await ReadHistoryAsync(process, _random.Choose(Keys));
            }
        }

        private async Task ReadHistoryAsync(ReaderProcess process, string key)
        {
            var bytes = Encoding.UTF8.GetBytes(key);
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                var calledAt = ++_steps;
                try
                {
                    using var read = await process.History.ReadAsync(bytes, CancellationToken.None);
                    var rows = read.Rows;
                    var held = 0;
                    var tags = new Tag[rows.Length];
                    for (var index = 0; index < rows.Length; index++)
                    {
                        tags[index] = rows[index].Tag;
                        held += rows[index].IsHeld ? 1 : 0;
                    }

                    Counts.Add(Paths.HeldRows, held);
                    Counts.Add(Paths.StaleMapHeld, held > 0 && process.View.Current.Epoch < _ledgers.Map.Current.Epoch ? 1 : 0);
                    _reads.Add(new HistoryReadRecord(process.Name, key, calledAt, ++_steps, tags, ArchiveOracle.Fold(read.Prefix, read.Rows), read.Through.ToDictionary(), read.Linearizable)
                    {
                        Chain = TokenChainChecker.Check(_store, read.Prefix?.Token ?? default, ChainRow.Of(read.Rows)),
                        VersionBase = read.Prefix is { } prefix ? (prefix.VersionBase, ArchiveFold.Of(prefix).ToString()) : null,
                    });
                    return;
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    // A store read failed; the reader reads again.
                }
                catch (ArchiveCorruptException exception)
                {
                    Counts.Violations.Add(Invariant($"{ReadOracle}{process.Name}'s read of {key} found the store corrupt: {exception.Message}"));
                    return;
                }
            }
        }

        /// <summary>
        /// A split and a merge, by construction: a key's ledger splits, each child gains a row, the children merge, and the
        /// merged ledger splits again, so the merged ledger's drains wait on both children, whose drains wait on the split
        /// ledger, and its children's drains wait on the merged ledger.
        /// </summary>
        /// <returns>The key, the first split's children, and the merged ledger; or none when the merge found no sibling, a violation.</returns>
        private async Task<SplitAndMerged?> SplitAndMergeAsync()
        {
            var key = _random.Choose(Keys);
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            Counts.Add(Paths.Splits);
            await TransitionAsync(ledger.Id, () => _ledgers.SplitAsync(ledger.Id, CancellationToken.None));
            foreach (var other in Keys)
            {
                await ProposeAsync(other, RowKind.Append);
            }

            var child = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            if (_ledgers.Map.Current.Ledgers.FirstOrDefault(listed => listed.IsOpen && listed.Range == child.Range.Sibling) is not { Location.Value: not null } sibling)
            {
                Counts.Violations.Add(Invariant($"The split and merge step found no sibling of ledger {child.Id} to merge it with."));
                return null;
            }

            Counts.Add(Paths.Merges);
            await TransitionAsync(child.Id, () => _ledgers.MergeAsync(child.Id, sibling.Id, CancellationToken.None));
            foreach (var other in Keys)
            {
                await ProposeAsync(other, RowKind.Append);
            }

            var merged = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            Counts.Add(Paths.Splits);
            await TransitionAsync(merged.Id, () => _ledgers.SplitAsync(merged.Id, CancellationToken.None));
            foreach (var other in Keys)
            {
                await ProposeAsync(other, RowKind.Append);
            }

            return new SplitAndMerged(key, child, sibling, merged);
        }

        /// <summary>
        /// The lineage check, by construction and with the faults off (archive-logs.md §8.1, step 1): the merged ledger's
        /// child over <c>key</c> closes, and the first split's child of the same range drops with every ledger it waits on,
        /// so the child's drain answers <c>Waiting</c> on the merged ledger, the only older ledger listed which overlaps it,
        /// and the merged ledger's drain answers <c>Waiting</c> on a ledger which does not overlap the child: the first
        /// split's other child, or one it waits on. The <c>ignores-lineage</c> mutant replaces the step's drainer's worker,
        /// as a restart, and only for the step, so the ledgers it waits on drop first.
        /// </summary>
        private async Task WaitThroughTheLineageAsync(SplitAndMerged lineage)
        {
            var child = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(lineage.Key));
            await TransitionAsync(child.Id, () => _ledgers.ReplaceAsync(child.Id, CancellationToken.None));
            var (overlapping, other) = lineage.First.Range == child.Range ? (lineage.First, lineage.Second) : (lineage.Second, lineage.First);
            var drainer = _drainers[0];
            using var faultsOff = new FaultsOff(this);
            await CompleteAndDropClosedAsync(older: overlapping, andItself: true);
            var map = await drainer.View.ReadAsync(CancellationToken.None);
            if (map.Get(overlapping.Id) is not null || map.Get(lineage.Merged.Id) is not { } merged || map.Get(other.Id) is null || map.Get(child.Id) is not { } closed)
            {
                Counts.Violations.Add(Invariant($"The lineage step could not drop ledger {overlapping.Id} while ledgers {lineage.Merged.Id} and {other.Id} stay listed."));
                return;
            }

            if (_mutant == Mutant.IgnoresLineage)
            {
                _inLineageStep = true;
                drainer.Restart();
            }

            try
            {
                await CheckLineageAsync(drainer, map, closed, merged);
            }
            finally
            {
                if (_inLineageStep)
                {
                    _inLineageStep = false;
                    drainer.Restart();
                }
            }
        }

        /// <summary>
        /// The lineage step's checks: the drain of <paramref name="closed"/> waits on <paramref name="merged"/>, and the
        /// drain of <paramref name="merged"/> on an older ledger <paramref name="map"/> lists which does not overlap
        /// <paramref name="closed"/>.
        /// </summary>
        private async Task CheckLineageAsync(Drainer drainer, LedgerMapValue map, LedgerMetadata closed, LedgerMetadata merged)
        {
            if (await DrainLedgerAsync(drainer, closed, crashes: false) is not { Value: ArchiveLogDrainOutcome.Waiting { Older: var awaited } } || awaited != merged.Id)
            {
                Counts.Violations.Add(Invariant($"{LineageCheck}the drain of ledger {closed.Id} did not wait on ledger {merged.Id}, the older ledger listed which overlaps it."));
                return;
            }

            if (await DrainLedgerAsync(drainer, merged, crashes: false) is not { Value: ArchiveLogDrainOutcome.Waiting { Older: var older } }
                || older >= merged.Id || map.Get(older) is not { } listed || !listed.Range.Overlaps(merged.Range) || listed.Range.Overlaps(closed.Range))
            {
                Counts.Violations.Add(Invariant($"{LineageCheck}the drain of ledger {merged.Id} did not wait on an older ledger which overlaps it but not ledger {closed.Id}."));
                return;
            }

            Counts.Add(Paths.Lineage);
        }

        /// <summary>
        /// A write which fails, by construction and with the other faults off: every attempt of a key's write fails before
        /// it lands, so its segment's drain throws the failure and leaves the segment unmarked, and, once the store accepts
        /// the key's writes again, the next drain completes the segment and drops the ledger. The <c>marks-unheld</c> mutant
        /// marks the segment as if the write were held instead, and its drop, submitted while the key's log lacks the write,
        /// breaks the drop check (LOG-I3); its second drain then finds the ledger dropped.
        /// </summary>
        private async Task FailAKeysWriteAsync()
        {
            var key = _random.Choose(Keys);
            await ProposeAsync(key, RowKind.Append);
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            await DrainOlderAsync(ledger);
            await TransitionAsync(ledger.Id, () => _ledgers.ReplaceAsync(ledger.Id, CancellationToken.None));
            var drainer = _drainers[0];
            using var faultsOff = new FaultsOff(this);
            var metadata = (await drainer.View.ReadAsync(CancellationToken.None)).Get(ledger.Id)!.Value;
            _faults.FailingLog = ArchiveLogNames.Hexadecimal.Of(Encoding.UTF8.GetBytes(key)).Value;
            bool completed;
            try
            {
                completed = await DrainLedgerAsync(drainer, metadata, crashes: false) is { Value: ArchiveLogDrainOutcome.Complete };
            }
            finally
            {
                _faults.FailingLog = null;
            }

            var outcome = await DrainLedgerAsync(drainer, metadata, crashes: false);
            if (!(outcome is { Value: ArchiveLogDrainOutcome.Complete } || completed && outcome is { Value: ArchiveLogDrainOutcome.Dropped })
                || (await drainer.View.ReadAsync(CancellationToken.None)).Get(ledger.Id) is not null)
            {
                Counts.Violations.Add(Invariant($"The failed write step's drain of ledger {ledger.Id} did not complete and drop it once its key's writes landed."));
            }
        }
        /// <summary>
        /// A straggler, by construction: one drainer's requests wait at its gate while it drains a closed ledger's segment,
        /// the other drainer completes the ledger, drops it, and drains its successor, and then the first's requests land,
        /// each classified held or fenced off, after which every oracle still holds.
        /// </summary>
        private async Task StragglerAfterTheDropAsync()
        {
            var key = _random.Choose(Keys);
            await ProposeAsync(key, RowKind.Append);
            await ProposeAsync(key, RowKind.Append);
            var closing = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            await DrainOlderAsync(closing);
            await TransitionAsync(closing.Id, () => _ledgers.ReplaceAsync(closing.Id, CancellationToken.None));
            await ProposeAsync(key, RowKind.Append);
            var (slow, fast) = (_drainers[0], _drainers[1]);
            _ = await slow.View.ReadAsync(CancellationToken.None);
            _ = await fast.View.ReadAsync(CancellationToken.None);
            var held = slow.Logs.Hold();
            Task<ArchiveLogDrainOutcome?> straggling;
            using (new FaultsOff(this))
            {
                // With the faults off, the slow drain reaches its first write by construction, which waits at the gate.
                straggling = DrainLedgerAsync(slow, slow.View.Current.Get(closing.Id)!.Value, crashes: false);
                _ = await SimulationTasks.WhenAny(held, straggling);
            }

            if (!await CompletesOnceFaultsStopAsync(fast, fast.View.Current.Get(closing.Id)!.Value))
            {
                Counts.Violations.Add(Invariant($"The straggler step's drain of ledger {closing.Id} did not complete and drop it once the faults stopped."));
            }

            // The writer's view reads the drop, so the straggler count, which reads it, sees the slow drain's requests land after it.
            _ = await _ledgers.Map.ReadAsync(CancellationToken.None);
            await ProposeAsync(key, RowKind.Append);
            var successor = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            await EndSegmentAsync(key);
            _ = await DrainLedgerAsync(fast, fast.View.Current.Get(successor.Id) ?? successor, crashes: false);
            slow.Logs.Release();
            _ = await straggling;
            await ReadHistoryAsync(_readers[0], key);
        }

        /// <summary>
        /// A history read on a stale map which answers held rows, by construction and with the faults off: a key's open
        /// ledger holds a closed segment a drain copied, so the store holds its rows while the map still lists the ledger;
        /// the reader's view reads the map, and then a ledger over another range, which holds none of the keys, is
        /// replaced, so the map moves past the view without changing a ledger the read scans. The read takes the view's
        /// value, which lists every ledger it scans as the map does, and answers the segment's rows held.
        /// </summary>
        private async Task ReadHeldRowsOnAStaleMapAsync()
        {
            var key = _random.Choose(Keys);
            using (new FaultsOff(this))
            {
                var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
                if (!_ledgers.Map.Current.Ledgers.Any(listed => listed.IsOpen && listed.Id != ledger.Id))
                {
                    Counts.Add(Paths.Splits);
                    await TransitionAsync(ledger.Id, () => _ledgers.SplitAsync(ledger.Id, CancellationToken.None));
                    ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
                }

                // Another key's row opens the next segment and carries the tail past the marker which closed the key's.
                await ProposeAsync(key, RowKind.Append);
                await EndSegmentAsync(key);
                await ProposeAsync(Keys.First(other => other != key), RowKind.Append);
                await CompleteAndDropClosedAsync();
                var drainer = _drainers[0];
                _ = await drainer.View.ReadAsync(CancellationToken.None);
                _ = await DrainLedgerAsync(drainer, drainer.View.Current.Get(ledger.Id)!.Value, crashes: false);

                var reader = _readers[1];
                _ = await reader.View.ReadAsync(CancellationToken.None);
                var other = _ledgers.Map.Current.Ledgers.First(listed => listed.IsOpen && listed.Id != ledger.Id);
                Counts.Add(Paths.Replaces);
                await TransitionAsync(other.Id, () => _ledgers.ReplaceAsync(other.Id, CancellationToken.None));
                if (reader.View.Current.Epoch >= _ledgers.Map.Current.Epoch)
                {
                    Counts.Violations.Add(Invariant($"The stale-map step's map did not move past the reader's view."));
                }

                var before = Counts.Paths.GetValueOrDefault(Paths.StaleMapHeld);
                await ReadHistoryAsync(reader, key);
                if (Counts.Paths.GetValueOrDefault(Paths.StaleMapHeld) == before)
                {
                    Counts.Violations.Add(Invariant($"The stale-map step's read of {key} answered no held row on its stale map."));
                }
            }
        }

        /// <summary>
        /// A crash between a segment's writes, by construction: a drain's requests wait at its gate, the drain is cancelled
        /// while they wait, the worker restarts, and the next drain resumes from the markers and completes the segment, while
        /// the crashed drain's writes, sent as stragglers', land after the crash.
        /// </summary>
        private async Task CrashBetweenASegmentsWritesAsync()
        {
            var key = _random.Choose(Keys);
            await ProposeAsync(key, RowKind.Replace);
            await ProposeAsync(Keys.First(other => other != key), RowKind.Append);
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            await DrainOlderAsync(ledger);
            await TransitionAsync(ledger.Id, () => _ledgers.ReplaceAsync(ledger.Id, CancellationToken.None));
            var drainer = _drainers[0];
            _ = await drainer.View.ReadAsync(CancellationToken.None);
            var metadata = drainer.View.Current.Get(ledger.Id)!.Value;
            using (new FaultsOff(this))
            {
                // With the faults off, the drain reaches its first write by construction, which waits at the gate.
                using var crash = new CancellationTokenSource();
                var held = drainer.Logs.Hold();
                drainer.Logs.StragglerRate = 1;
                var draining = drainer.Worker.DrainAsync(metadata, ((LedgerStatus.Closed)metadata.Status.Value).T, crash.Token);
                _ = await SimulationTasks.WhenAny(held, draining);
                drainer.Logs.StragglerRate = 0;
                crash.Cancel();
                drainer.Logs.Release();
                try
                {
                    _ = await draining;
                }
                catch (OperationCanceledException)
                {
                    Counts.Add(Paths.Crashes);
                }
            }

            drainer.Restart();
            Counts.Add(Paths.Restarts);
            if (!await CompletesOnceFaultsStopAsync(drainer, metadata))
            {
                Counts.Violations.Add(Invariant($"The crash step's drain of ledger {ledger.Id} did not complete and drop it after its restart once the faults stopped."));
            }
        }

        /// <summary>
        /// A key the store refuses blocks its ledger's drain with cause <c>Refused</c>, the key, and its log, while the
        /// segment's other writes land; once an operator lifts the refusal, the next drain completes the segment.
        /// </summary>
        private async Task BlockAKeyAsync()
        {
            var key = _random.Choose(Keys);
            await ProposeAsync(key, RowKind.Append);
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            await DrainOlderAsync(ledger);
            await TransitionAsync(ledger.Id, () => _ledgers.ReplaceAsync(ledger.Id, CancellationToken.None));
            var drainer = _drainers[1];
            _ = await drainer.View.ReadAsync(CancellationToken.None);
            var metadata = drainer.View.Current.Get(ledger.Id)!.Value;
            var name = ArchiveLogNames.Hexadecimal.Of(Encoding.UTF8.GetBytes(key)).Value;
            _ = _blocked.Add(key);
            _ = drainer.Logs.Refused.Add(name);
            using (new FaultsOff(this))
            {
                // With the faults off, the drain reaches the refused write by construction.
                _ = await DrainLedgerAsync(drainer, metadata, crashes: false);
            }

            _ = drainer.Logs.Refused.Remove(name);
            _ = _blocked.Remove(key);
            if (!await CompletesOnceFaultsStopAsync(drainer, metadata))
            {
                Counts.Violations.Add(Invariant($"The blocked step's drain of ledger {ledger.Id} did not complete and drop it once its key was accepted and the faults stopped."));
            }
        }

        /// <summary>
        /// A drain of a ledger another drainer dropped, by construction and with the faults off: one drainer holds the closed
        /// ledger's entry from its view, as a drainer walking its view's ledgers does, the other completes and drops the
        /// ledger, and the first's view then reads the drop, so its drain of the entry it holds answers <c>Dropped</c>.
        /// </summary>
        private async Task DrainADroppedLedgerAsync()
        {
            var key = _random.Choose(Keys);
            await ProposeAsync(key, RowKind.Append);
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            await DrainOlderAsync(ledger);
            await TransitionAsync(ledger.Id, () => _ledgers.ReplaceAsync(ledger.Id, CancellationToken.None));
            var (stale, dropper) = (_drainers[1], _drainers[0]);
            using var faultsOff = new FaultsOff(this);
            var metadata = (await stale.View.ReadAsync(CancellationToken.None)).Get(ledger.Id)!.Value;
            var listed = (await dropper.View.ReadAsync(CancellationToken.None)).Get(ledger.Id)!.Value;
            if (await DrainLedgerAsync(dropper, listed, crashes: false) is not { Value: ArchiveLogDrainOutcome.Complete }
                || (await dropper.View.ReadAsync(CancellationToken.None)).Get(ledger.Id) is not null)
            {
                Counts.Violations.Add(Invariant($"The dropped step's drain of ledger {ledger.Id} did not complete and drop it."));
                return;
            }

            _ = await stale.View.ReadAsync(CancellationToken.None);
            if (await DrainLedgerAsync(stale, metadata, crashes: false) is not { Value: ArchiveLogDrainOutcome.Dropped })
            {
                Counts.Violations.Add(Invariant($"The dropped step's drain of ledger {ledger.Id} from a stale entry did not answer Dropped."));
            }
        }

        /// <summary>
        /// Completes and drops, with the faults off, every closed ledger a drain of <paramref name="ledger"/> waits on, so it
        /// does not wait.
        /// </summary>
        private async Task DrainOlderAsync(LedgerMetadata ledger)
        {
            using var faultsOff = new FaultsOff(this);
            await CompleteAndDropClosedAsync(older: ledger);
        }

        /// <summary>
        /// A step's liveness: a drain of <paramref name="ledger"/> completes, and its drop applies, once the faults stop. A drain
        /// which meets a fault throws it, or answers <c>Drained</c> at backpressure, and a drop's outcome may be unknown, and
        /// the node drains again (archive-logs.md §8.1, §2.4), so the drain runs once under the faults and, unless it completed
        /// and the drainer's view then shows the drop, again with every fault off.
        /// </summary>
        /// <returns>Whether a drain answered <c>Complete</c> and the drainer's view then no longer lists the ledger.</returns>
        private async Task<bool> CompletesOnceFaultsStopAsync(Drainer drainer, LedgerMetadata ledger)
        {
            var completed = await DrainLedgerAsync(drainer, ledger, crashes: false) is { Value: ArchiveLogDrainOutcome.Complete };
            if (completed && (await drainer.View.ReadAsync(CancellationToken.None)).Get(ledger.Id) is null)
            {
                return true;
            }

            using var faultsOff = new FaultsOff(this);
            var outcome = await DrainLedgerAsync(drainer, ledger, crashes: false);

            // A drop whose outcome was unknown may land after the first drain, which the second then finds Dropped.
            return (outcome is { Value: ArchiveLogDrainOutcome.Complete } || completed && outcome is { Value: ArchiveLogDrainOutcome.Dropped })
                && (await drainer.View.ReadAsync(CancellationToken.None)).Get(ledger.Id) is null;
        }

        /// <summary>
        /// Drains and drops, oldest first, every closed ledger the map lists, or only those a drain of
        /// <paramref name="older"/> waits on, and with <paramref name="andItself"/> it too, until none is left.
        /// </summary>
        private async Task CompleteAndDropClosedAsync(LedgerMetadata? older = null, bool andItself = false)
        {
            var drainer = _drainers[0];
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var map = await drainer.View.ReadAsync(CancellationToken.None);
                var awaited = older is { } bound ? Awaited(map, bound) : null;
                if (andItself && older is { } itself)
                {
                    _ = awaited!.Add(itself.Id);
                }

                var closed = map.Ledgers.Where(listed => !listed.IsOpen && (awaited is null || awaited.Contains(listed.Id))).ToArray();
                if (closed.Length == 0 || Counts.Violations.Count > 0)
                {
                    // A violation already fails the run; a blocked drain would only be retried until the budget runs out.
                    return;
                }

                foreach (var ledger in closed)
                {
                    _ = await DrainLedgerAsync(drainer, ledger, crashes: false);
                }
            }

            Counts.Violations.Add("The closed ledgers never all dropped once the faults stopped.");
        }

        /// <summary>
        /// Returns the listed ledgers a drain of <paramref name="ledger"/> waits on: each with a lower ID which overlaps it,
        /// and, since a drain of each waits in turn, each with a lower ID which overlaps one of them (archive-logs.md §8.1).
        /// </summary>
        private static HashSet<LedgerId> Awaited(LedgerMapValue map, LedgerMetadata ledger)
        {
            var awaited = new HashSet<LedgerId>();
            var waiting = new Stack<LedgerMetadata>([ledger]);
            while (waiting.TryPop(out var next))
            {
                foreach (var listed in map.Ledgers)
                {
                    if (listed.Id < next.Id && listed.Range.Overlaps(next.Range) && awaited.Add(listed.Id))
                    {
                        waiting.Push(listed);
                    }
                }
            }

            return awaited;
        }

        private async Task SettleAsync()
        {
            _faults.Rate = 0;
            (_objects.UnknownOutcomeRate, _objects.RetriedAnswerRate, _objects.ReadFailureRate) = (0, 0, 0);
            _register.UnknownOutcomeRate = 0;
            _ledgers.Transport.Saturated = static (_, _) => false;
            foreach (var drainer in _drainers)
            {
                drainer.Logs.StragglerRate = 0;
            }

            await Task.Delay(_objects.MaxLandingDelay + TimeSpan.FromMilliseconds(100), _environment.TimeProvider);
            await CompleteAndDropClosedAsync();
            await _ledgers.ReconcileAsync(CancellationToken.None);
            foreach (var key in _reference.Keys)
            {
                await ReadHistoryAsync(_readers[0], key);
                if (_reads.LastOrDefault(read => read.Reader == "writer" && read.Key == key) is not { } final)
                {
                    Counts.Violations.Add(Invariant($"{ReadOracle}the writer never read {key}."));
                    continue;
                }

                var expected = Fold(_reference[key].OrderBy(static row => row.Tag));
                if (!final.Linearizable || !expected.SameAs(final.Fold))
                {
                    Counts.Violations.Add(Invariant($"{ReadOracle}the final read of {key} (linearizable {final.Linearizable}) folds to {final.Fold}, not its whole history's {expected}."));
                }
            }
        }

        /// <summary>
        /// HI-I1: each read's live rows are the reference's rows of the ledgers it read below their ends, and its prefix with
        /// the rows not held folds to the reference over its <c>Through</c>, or, when the read is not linearizable, over a
        /// longer prefix of rows proposed before it ended; over that cut, no row proposed after the read
        /// ended, every row of a ledger closed before it began, and, for a linearizable read, every row answered durable
        /// before it began.
        /// </summary>
        private void VerifyReads()
        {
            var proposals = _proposals.ToDictionary(static proposal => proposal.Row.Tag);
            foreach (var read in _reads)
            {
                List<Decided> reference = [.. _reference.GetValueOrDefault(read.Key, []).OrderBy(static row => row.Tag)];
                var highest = read.Through.Count == 0 ? (LedgerId?)null : read.Through.Keys.Max();
                List<Decided> cut = [.. reference.Where(row => read.Through.TryGetValue(row.Tag.Ledger, out var end) ? row.Tag.Slot < end : row.Tag.Ledger < highest)];
                var expected = Fold(cut);
                var live = cut.Where(row => read.Through.ContainsKey(row.Tag.Ledger)).Select(static row => row.Tag).ToArray();
                if (!(expected.SameAs(read.Fold) || !read.Linearizable && RunsAhead(reference, cut.Count, read, proposals)) || !live.SequenceEqual(read.Rows))
                {
                    Counts.Violations.Add(Invariant($"{ReadOracle}{read.Reader}'s read of {read.Key} between steps {read.CalledAt} and {read.AnsweredAt} folds to {read.Fold}, not {expected}, which the reference over its Through gives."));
                }

                if (cut.Any(row => proposals[row.Tag].CalledAt > read.AnsweredAt))
                {
                    Counts.Violations.Add(Invariant($"{ReadOracle}{read.Reader}'s read of {read.Key} holds rows proposed after it ended."));
                }

                var missed = reference.Where(row => !cut.Contains(row)
                    && (_closedAt.TryGetValue(row.Tag.Ledger, out var closedAt) && closedAt < read.CalledAt
                        || read.Linearizable && proposals[row.Tag].AnsweredAt < read.CalledAt)).ToArray();
                if (missed.Length > 0)
                {
                    Counts.Violations.Add(Invariant($"{ReadOracle}{read.Reader}'s read of {read.Key} (linearizable {read.Linearizable}) misses rows decided before it began: [{string.Join(", ", missed.Select(static row => row.Tag))}]."));
                }
            }
        }

        /// <summary>
        /// Whether a read which is not linearizable folds to a longer prefix of its key's history than its <c>Through</c>
        /// names, of rows proposed before it ended: its <c>Through</c> is a lower bound, since a drain from a later cut may
        /// already be held (archive.md §2.3).
        /// </summary>
        private static bool RunsAhead(List<Decided> reference, int cut, HistoryReadRecord read, Dictionary<Tag, Proposal> proposals)
        {
            for (var count = cut + 1; count <= reference.Count && proposals[reference[count - 1].Tag].CalledAt <= read.AnsweredAt; count++)
            {
                if (Fold(reference.Take(count)).SameAs(read.Fold))
                {
                    return true;
                }
            }

            return false;
        }

        private static ArchiveFold Fold(IEnumerable<Decided> rows) => ArchiveOracle.Fold(rows, static row => row.Kind, static row => row.Body);

        private Task DelayAsync(int maximumMilliseconds) =>
            Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(maximumMilliseconds)), _environment.TimeProvider);

        /// <summary>
        /// Stops every fault the scenario injects until disposed, which restores each: the archive-log store's, the markers'
        /// store's, and the ledger map register's faults, replica backpressure, and the drainers' stragglers.
        /// </summary>
        private sealed class FaultsOff : IDisposable
        {
            private readonly Scenario _scenario;
            private readonly (double Logs, double Register, double Unknown, double Retried, double ReadFailure) _rates;
            private readonly double[] _straggling;
            private readonly Func<LedgerId, Slot, bool> _saturated;

            public FaultsOff(Scenario scenario)
            {
                _scenario = scenario;
                _rates = (scenario._faults.Rate, scenario._register.UnknownOutcomeRate, scenario._objects.UnknownOutcomeRate, scenario._objects.RetriedAnswerRate, scenario._objects.ReadFailureRate);
                _straggling = [.. scenario._drainers.Select(static drainer => drainer.Logs.StragglerRate)];
                _saturated = scenario._ledgers.Transport.Saturated;
                (scenario._faults.Rate, scenario._register.UnknownOutcomeRate, scenario._objects.UnknownOutcomeRate, scenario._objects.RetriedAnswerRate, scenario._objects.ReadFailureRate) = (0, 0, 0, 0, 0);
                foreach (var drainer in scenario._drainers)
                {
                    drainer.Logs.StragglerRate = 0;
                }

                scenario._ledgers.Transport.Saturated = static (_, _) => false;
            }

            public void Dispose()
            {
                (_scenario._faults.Rate, _scenario._register.UnknownOutcomeRate, _scenario._objects.UnknownOutcomeRate, _scenario._objects.RetriedAnswerRate, _scenario._objects.ReadFailureRate) = _rates;
                for (var index = 0; index < _straggling.Length; index++)
                {
                    _scenario._drainers[index].Logs.StragglerRate = _straggling[index];
                }

                _scenario._ledgers.Transport.Saturated = _saturated;
            }
        }
    }

    /// <summary>
    /// The scenario's object store with the archive gate's object, the store's record <c>durable-storage/store</c>, in a faulty store
    /// of its own: every other request reaches the markers' store unchanged.
    /// </summary>
    private sealed class GateObjectStore(IObjectStore inner, IObjectStore gate) : IObjectStore
    {
        private static readonly ObjectName Record = new(ArchiveLogNames.StoreRecord);

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            Of(name).CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            Of(name).ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            Of(name).GetPropertiesAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) => Of(name).DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            Of(name).ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            Of(name).ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);

        private IObjectStore Of(ObjectName name) => name == Record ? gate : inner;
    }
}
