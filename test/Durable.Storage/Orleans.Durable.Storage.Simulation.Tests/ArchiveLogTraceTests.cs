using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// The drain simulation's traces for validation against <c>specs/archive-logs/ArchiveLogs.qnt</c> (docs/spec/verification.md
/// §5.1; archive-logs.md §10). Each seed runs one key's history, <c>k</c>, beside another key's, <c>o</c>, over a closed
/// ledger and its successor, which a writer extends while two drainers race to drain both, crash by cancellation, meet
/// requests which fail before they land, and restart from the markers, and a sweeper sweeps; the successor then closes,
/// and both ledgers complete and drop. Each request of <c>k</c>'s log which reaches the store, each segment a worker starts
/// and completes, each crash, sweep, drop, decision, and close is recorded as the model's action, and the run's decided
/// ledgers give the model's constants. With <c>CLOG_ARCHIVE_LOG_TRACES</c> set, each seed writes the trace as a Quint
/// module whose <c>traceReplay</c> run applies every action in order and checks the model's invariants after each, and
/// the model's position against the store's after each request, which <c>eng/archive-log-traces.ps1</c> runs with
/// <c>quint test</c>: an action the model disables, a state which breaks an invariant, or a position the store does not
/// reach fails the trace. The second ledger's decisions are taken from its writer as each proposal is decided, and its
/// close from the replace which closes it. A drain which blocks is a violation. A run of the <c>marks-unheld</c> mutant,
/// whose every write of the key fails, writes a trace which must fail, as a negative control.
/// </summary>
/// <remarks>
/// The mapping: a worker's <c>StartedSegment</c> is <c>drainStart</c>, preceded by the decisions and drops its drain shows;
/// <c>Create</c> is <c>createStep</c>, each block <c>unitStep</c>, a checkpoint's <c>Put Blob</c> <c>baseStep</c>, the
/// write-ahead object's <c>walStep</c>, a properties read <c>readRun</c> or <c>readOpen</c>, and <c>CompletedSegment</c>
/// <c>markStep</c>; a create or checkpoint re-issued after a <c>Resume(0)</c> or a <c>Retry</c> is the model's stutter. A
/// request which fails before it lands is no action. Faults never fail a block, so a run's call lands whole or not at
/// all, as each block is one <c>Append</c> of the key, 8 bytes in 8-byte blocks; the model checks the cuts inside a run.
/// </remarks>
public sealed class ArchiveLogTraceTests
{
    private const int FirstSeed = 1;
    private const int SeedCount = 3;
    private const int BodyLength = 8;
    private static readonly AsyncLocal<string?> Actor = new();

    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    /// <summary>The drains of a seed's history make a trace of the model's actions, which writes when asked.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void ADrainSimulationRecordsItsTrace(int seed)
    {
        Trace? trace = null;
        _ = SeededSimulation.Run(seed, async environment => trace = await SimulateAsync(environment));

        Assert.True(trace!.Violations.Count == 0, string.Join(" | ", trace.Violations));
        Assert.Contains(trace.Actions, static action => action.StartsWith("drainStart(", StringComparison.Ordinal));
        Assert.Contains(trace.Actions, static action => action.StartsWith("unitStep(", StringComparison.Ordinal) || action.StartsWith("walStep(", StringComparison.Ordinal));
        Assert.Contains(trace.Actions, static action => action.StartsWith("#position:", StringComparison.Ordinal));
        Assert.Contains("drop(2)", trace.Actions);
        Write(trace, string.Create(CultureInfo.InvariantCulture, $"ArchiveLogsTrace{seed}"));
    }

    /// <summary>
    /// The negative control: a <c>marks-unheld</c> worker, whose every write of the key fails, marks the segment anyway, so
    /// its trace takes <c>markStep</c> while the model's drainer still makes the write, which trace validation must refuse.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void AMarksUnheldDrainRecordsATraceTheModelRefuses(int seed)
    {
        Trace? trace = null;
        _ = SeededSimulation.Run(seed, async environment => trace = await SimulateAsync(environment, marksUnheld: true));

        Assert.True(trace!.Actions.Any(static action => action.StartsWith("markStep(", StringComparison.Ordinal)), string.Join(" ", trace.Actions) + " | " + string.Join(" | ", trace.Violations));
        Write(trace, string.Create(CultureInfo.InvariantCulture, $"ArchiveLogsTraceUnheld{seed}"));
    }

    private static void Write(Trace trace, string module)
    {
        if (Environment.GetEnvironmentVariable("CLOG_ARCHIVE_LOG_TRACES") is { Length: > 0 } directory)
        {
            _ = Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, module + ".qnt"), trace.Module(module));
        }
    }

    private static async Task<Trace> SimulateAsync(SimulationEnvironment environment, bool marksUnheld = false)
    {
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
        var objects = new InMemoryObjectStore();
        var trace = new Trace { FailsWrites = marksUnheld };
        var random = environment.CreateRandom("archive-log-trace");
        var logs = new MemoryArchiveLogStore(
            new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, BodyLength, 49_900, 40_000, MemoryArchiveLogStore.NameRule),
            request => trace.Request(request, random));
        trace.Logs = logs;
        var store = new LogArchiveStore(logs, objects, ArchiveLogNames.Hexadecimal);
        var options = new LedgerWriterOptions(1 << 20, 1 << 10, TimeSpan.FromDays(1), LedgerWriterOptions.DefaultMaxProposalBytes);
        await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None, options, chains: new ArchiveChains(store));
        await new Scenario(environment, ledgers, logs, objects, store, trace, random, marksUnheld).RunAsync();
        return trace;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The recorded actions, and each drainer's place in its segment: whether it is in one, whether it has made a request
    /// of the key's log in it yet, and whether that write is an opener; and the sweeper's phase.
    /// </summary>
    private sealed class Trace
    {
        private static readonly string TracedLog = ArchiveLogNames.Hexadecimal.Of("k"u8).Value;
        private readonly bool[] _inSegment = new bool[3];
        private readonly bool[] _requested = new bool[3];
        private readonly bool[] _opener = new bool[3];
        private int _sweeperPhase;

        public List<string> Actions { get; } = [];

        public List<string> Violations { get; } = [];

        public double FaultRate { get; set; } = 0.05;

        /// <summary>Gets or sets a value indicating whether every create and checkpoint of the key's log fails before it lands.</summary>
        public bool FailsWrites { get; set; }

        /// <summary>Gets or sets the store whose positions the trace expects of the model.</summary>
        public MemoryArchiveLogStore? Logs { get; set; }

        public string Ledger1 { get; set; } = "[]";

        public string Ledger2 { get; set; } = "[]";

        public string Starts1 { get; set; } = "Set()";

        public string Starts2 { get; set; } = "Set()";

        /// <summary>Gets or sets the second ledger's end and its segments' starts, which expand its decisions.</summary>
        public (long End, long[] Starts) Second { get; set; } = (0, []);

        /// <summary>Decides a request's fault, a failure before it lands at the fault rate except for a block, and records it unless it fails.</summary>
        public ArchiveLogFault Request(ArchiveLogRequest request, SimulationRandom random)
        {
            var fails = FailsWrites
                ? request.Log == TracedLog && request.Kind is ArchiveLogRequestKind.CreateLog or ArchiveLogRequestKind.CreateBase
                : request.Kind != ArchiveLogRequestKind.AppendUnit && random.Chance(FaultRate);
            if (fails)
            {
                return ArchiveLogFault.FailBefore;
            }

            if (Actor.Value is "sweeper")
            {
                Sweep(request);
            }
            else if (Actor.Value is { } actor && request.Log == TracedLog)
            {
                Drain(int.Parse(actor, CultureInfo.InvariantCulture), request);
            }

            return ArchiveLogFault.None;
        }

        public void StartedSegment(int drainer, LedgerSegmentId segment, Slot end)
        {
            Add(Invariant($"drainStart({drainer}, {segment.Ledger}, {segment.Start}, {end})"));
            (_inSegment[drainer], _requested[drainer], _opener[drainer]) = (true, false, false);
        }

        public void CompletedSegment(int drainer)
        {
            Add(Invariant($"markStep({drainer})"));
            _inSegment[drainer] = false;
        }

        /// <summary>A drain which threw or was cancelled inside a segment: the model's crash of its drainer.</summary>
        public void Crashed(int drainer)
        {
            if (_inSegment[drainer])
            {
                Add(Invariant($"crash({drainer})"));
                _inSegment[drainer] = false;
            }
        }

        /// <summary>Ends a sweep, reading the log's properties first in the model when the sweep read none of the key's.</summary>
        public void SweepEnded()
        {
            if (_sweeperPhase == 1)
            {
                Add("sweepRead");
                _sweeperPhase = 2;
            }

            if (_sweeperPhase == 2)
            {
                Add("sweepDone");
            }

            _sweeperPhase = 0;
        }

        /// <summary>
        /// Records an action, after the position of the key's log which the actions before it left, which the model must
        /// reach: the store has run every request recorded so far, and the one this action records has not yet landed.
        /// </summary>
        public void Add(string action)
        {
            if (!action.StartsWith('#') && Logs is { } logs && Actions.Count > 0 && !Actions[^1].StartsWith("#position:", StringComparison.Ordinal))
            {
                var position = logs.Snapshot().Where(static log => log.Log == TracedLog).Select(static log => log.Position).FirstOrDefault();
                Actions.Add(Invariant($"#position:{position.Generation},{position.BaseRows},{position.Units}"));
            }

            Actions.Add(action);
        }

        public string Module(string name)
        {
            var text = new StringBuilder()
                .AppendLine(Invariant($"// The trace of a seed of {nameof(ArchiveLogTraceTests)}, generated; trace validation replays it."))
                .AppendLine(Invariant($"module {name} {{"))
                .AppendLine(Invariant($"  import ArchiveLogs(Ledger1 = {Ledger1}, Ledger2 = {Ledger2}, Starts1 = {Starts1}, Starts2 = {Starts2}, Drainers = 2, Sweeping = true, Loading = true, Stragglers = false, Mutation = \"none\").* from \"../../specs/archive-logs/ArchiveLogs\""))
                .AppendLine("  val Invariants = and { NoLostCommand, StaleReaders, ReadsAtTheirCut, NoUndecidedCommand, HoldsSound, ReferencedExist, VersionsUnique, NoFalseCorruption }");
            var lines = new List<string> { "  run traceReplay = init.expect(Invariants)" };
            foreach (var action in Expanded())
            {
                if (action.StartsWith("#position:", StringComparison.Ordinal))
                {
                    // The position the actions so far reached in the store, which the model's state after them must be.
                    var fields = action["#position:".Length..].Split(',');
                    lines[^1] = lines[^1].Replace(".expect(Invariants)", Invariant($".expect(and {{ Invariants, position == {{ g: {fields[0]}, r: {fields[1]}, p: {fields[2]} }} }})"), StringComparison.Ordinal);
                    continue;
                }

                lines.Add(Invariant($"    .then({action}).expect(Invariants)"));
            }

            return text.AppendLine(string.Join(Environment.NewLine, lines)).AppendLine("}").ToString();
        }

        /// <summary>
        /// Returns the actions with the second ledger's decisions expanded, which its constants give only once it closed:
        /// <c>#decide:b</c> decides through the batch at <c>b</c>, each <c>decideStart</c> through the next segment's start
        /// batch, and <c>#close</c> decides every batch and closes it once.
        /// </summary>
        private List<string> Expanded()
        {
            var (end, starts) = Second;
            var decided = end > 0 ? 1L : 0L;
            var closed = false;
            var actions = new List<string>();
            foreach (var action in Actions)
            {
                if (action.StartsWith("#decide:", StringComparison.Ordinal) || action == "#close")
                {
                    var through = action == "#close" ? end : Math.Min(end, long.Parse(action["#decide:".Length..], CultureInfo.InvariantCulture) + 1);
                    while (decided < through)
                    {
                        var next = starts.Where(start => start > decided - 1).DefaultIfEmpty(long.MaxValue).Min();
                        decided = next < end ? next + 1 : end;
                        actions.Add("decideStart");
                    }

                    if (action == "#close" && !closed)
                    {
                        closed = true;
                        actions.Add("close2");
                    }

                    continue;
                }

                actions.Add(action);
            }

            return actions;
        }

        private void Drain(int drainer, ArchiveLogRequest request)
        {
            if (!_inSegment[drainer])
            {
                Violations.Add(Invariant($"drainer {drainer} made a {request.Kind} request outside a segment"));
                return;
            }

            var fresh = !_requested[drainer];
            _requested[drainer] = true;
            switch (request.Kind)
            {
                case ArchiveLogRequestKind.CreateLog:
                    if (fresh)
                    {
                        Add(Invariant($"createStep({drainer})"));
                    }

                    break;
                case ArchiveLogRequestKind.AppendUnit:
                    Add(Invariant($"unitStep({drainer})"));
                    break;
                case ArchiveLogRequestKind.CreateBase:
                    if (fresh)
                    {
                        _opener[drainer] = true;
                        Add(Invariant($"baseStep({drainer})"));
                    }

                    break;
                case ArchiveLogRequestKind.OpenGeneration:
                    _opener[drainer] = true;
                    Add(Invariant($"walStep({drainer})"));
                    break;
                case ArchiveLogRequestKind.GetProperties:
                    Add(_opener[drainer] ? Invariant($"readOpen({drainer})") : Invariant($"readRun({drainer})"));
                    break;
                case ArchiveLogRequestKind.Read:
                case ArchiveLogRequestKind.List:
                case ArchiveLogRequestKind.DeleteBase:
                default:
                    Violations.Add(Invariant($"drainer {drainer} made a {request.Kind} request"));
                    break;
            }
        }

        private void Sweep(ArchiveLogRequest request)
        {
            switch (request.Kind)
            {
                case ArchiveLogRequestKind.List:
                    Add("sweepList");
                    _sweeperPhase = 1;
                    break;
                case ArchiveLogRequestKind.GetProperties when request.Log == TracedLog:
                    Add("sweepRead");
                    _sweeperPhase = 2;
                    break;
                case ArchiveLogRequestKind.DeleteBase when request.Log == TracedLog:
                    Add(Invariant($"sweepDelete({request.Generation})"));
                    break;
                case ArchiveLogRequestKind.GetProperties:
                case ArchiveLogRequestKind.DeleteBase:
                case ArchiveLogRequestKind.CreateLog:
                case ArchiveLogRequestKind.AppendUnit:
                case ArchiveLogRequestKind.CreateBase:
                case ArchiveLogRequestKind.OpenGeneration:
                case ArchiveLogRequestKind.Read:
                default:
                    break;
            }
        }
    }

    private sealed class Scenario(SimulationEnvironment environment, LiveLedgers ledgers, MemoryArchiveLogStore logs, InMemoryObjectStore objects, LogArchiveStore store, Trace trace, SimulationRandom random, bool marksUnheld)
    {
        private readonly List<SimulatedArchiveLogStore> _views = [];
        private readonly List<LedgerReader> _readers = [];
        private readonly List<ArchiveLogDrainWorker> _workers = [];
        private readonly List<LedgerMapView> _maps = [];
        private readonly List<(string Key, RowKind Kind, string Body)> _proposed = [];
        private LedgerId _first;
        private LedgerId _second;
        private Slot _t2;
        private bool _secondClosed;
        private bool _firstDropped;
        private bool _secondDropped;
        private readonly List<ArchiveLogGate> _gates = [];

        public async Task RunAsync()
        {
            await ledgers.BootstrapAsync(CancellationToken.None);
            _first = ledgers.OpenContaining("k"u8.ToArray()).Id;
            await WriteAsync(_first);
            await ledgers.ReplaceAsync(_first, CancellationToken.None);
            var t1 = Assert.IsType<LedgerStatus.Closed>(ledgers.Ledger(_first).Status.Value).T;
            (trace.Ledger1, trace.Starts1) = await ConstantsAsync(ledgers.Ledger(_first), t1);
            _second = ledgers.OpenContaining("k"u8.ToArray()).Id;
            if (marksUnheld)
            {
                await DrainUnheldAsync();
                return;
            }

            for (var index = 0; index < 2; index++)
            {
                var map = await ledgers.OpenViewAsync(CancellationToken.None);
                _maps.Add(map);
                _readers.Add(ledgers.OpenReader(map));
                _views.Add(new SimulatedArchiveLogStore(logs, environment, Invariant($"trace-drainer-{index + 1}")) { StragglerRate = 0.05 });
                _gates.Add(await PassGateAsync(_views[^1]));
                _workers.Add(NewWorker(index + 1, map));
            }

            var sweeper = new ArchiveLogSweeper(await PassGateAsync(logs), await ledgers.OpenViewAsync(CancellationToken.None), NullLogger<ArchiveLogSweeper>.Instance);
            var writing = WriteAsync(_second);
            await SimulationTasks.WhenAll(writing, DrainAsync(1), DrainAsync(2), SweepAsync(sweeper));
            await ledgers.ReplaceAsync(_second, CancellationToken.None);
            trace.Add("#close");
            _t2 = Assert.IsType<LedgerStatus.Closed>(ledgers.Ledger(_second).Status.Value).T;
            (trace.Ledger2, trace.Starts2) = await ConstantsAsync(ledgers.Ledger(_second), _t2);
            trace.Second = (_t2.Value, [.. trace.Starts2["Set(".Length..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(static start => long.Parse(start, CultureInfo.InvariantCulture))]);
            _secondClosed = true;
            trace.FaultRate = 0;
            foreach (var view in _views)
            {
                view.StragglerRate = 0;
            }

            for (var attempt = 0; attempt < 50 && !_secondDropped; attempt++)
            {
                await DrainOnceAsync(1);
            }

            await SweepOnceAsync(sweeper);
            if (!_secondDropped)
            {
                trace.Violations.Add("The ledgers never dropped once the faults stopped.");
            }

            await VerifyAsync();
        }

        /// <summary>
        /// The negative control's run: a <c>marks-unheld</c> worker drains the closed first ledger once, every create and
        /// checkpoint of the key failing, so it marks a segment whose write never landed.
        /// </summary>
        private async Task DrainUnheldAsync()
        {
            var map = await ledgers.OpenViewAsync(CancellationToken.None);
            _maps.Add(map);
            _readers.Add(ledgers.OpenReader(map));
            _views.Add(new SimulatedArchiveLogStore(logs, environment, "trace-drainer-1"));
            _gates.Add(await PassGateAsync(_views[^1]));
            _workers.Add(NewWorker(1, map, marksUnheld: true));
            trace.FaultRate = 0;
            await DrainOnceAsync(1);
        }

        /// <summary>Passes a drainer's archive gate over <paramref name="view"/>, as its process does before it drains.</summary>
        private async Task<ArchiveLogGate> PassGateAsync(IArchiveLogStore view)
        {
            var gated = new LogArchiveStore(view, objects, ArchiveLogNames.Hexadecimal);
            var record = await gated.StoreRecordAsync(ImmutableDictionary<string, string>.Empty, CancellationToken.None);
            return await ArchiveLogGate.PassAsync(gated, new ArchiveStoreIdentity(ArchiveStoreKind.Logs, new ArchiveStorePrefix("t"), record.Id), ImmutableDictionary<string, string>.Empty, CancellationToken.None);
        }

        private ArchiveLogDrainWorker NewWorker(int drainer, LedgerMapView map, bool marksUnheld = false) =>
            new(_gates[drainer - 1], map, _readers[drainer - 1], new ArchiveLogOptions { DrainConcurrency = 2 }, SimulationEnvironment.Clock, new ArchiveLogWriterOptions { Delay = TimeSpan.FromMilliseconds(1) })
            {
                MarksUnheldMutant = marksUnheld,
                BackpressureDelay = TimeSpan.FromMilliseconds(1),
                StartedSegment = (segment, end) => Started(drainer, segment, end),
                CompletedSegment = (_, _) => trace.CompletedSegment(drainer),
            };

        /// <summary>
        /// Maps a segment's start: first the drops and decisions the worker's drain shows, which the model has not yet taken,
        /// since the worker read them before it scanned.
        /// </summary>
        private void Started(int drainer, LedgerSegmentId segment, Slot end)
        {
            if (segment.Ledger == _second)
            {
                DropFirst();
                trace.Add(_secondClosed && end == _t2 ? "#close" : Invariant($"#decide:{end}"));
            }

            trace.StartedSegment(drainer, segment, end);
        }

        /// <summary>Records <c>drop(1)</c> once: the map has dropped the first ledger, as a drain of its successor shows.</summary>
        private void DropFirst()
        {
            if (!_firstDropped)
            {
                _firstDropped = true;
                trace.Add("drop(1)");
            }
        }

        /// <summary>Proposes a few rows of <c>k</c> and <c>o</c> to <paramref name="ledger"/>, one a batch, ending segments now and then.</summary>
        private async Task WriteAsync(LedgerId ledger)
        {
            for (var count = 0; count < 4; count++)
            {
                await Task.Delay(random.NextTimeSpan(TimeSpan.FromMilliseconds(4)), environment.TimeProvider);
                var key = random.Chance(0.7) ? "k" : "o";
                var kind = random.Next(10) switch
                {
                    < 6 => RowKind.Append,
                    < 8 => RowKind.Replace,
                    _ => RowKind.Delete,
                };
                var body = kind == RowKind.Delete && random.Chance(0.5) ? string.Empty : Invariant($"{key}{_proposed.Count:D7}");
                var tags = await ledgers.ProposeAsync(ledger, [LiveRow.Of(key, kind, body)], CancellationToken.None);
                _proposed.Add((key, kind, body));
                Decided(ledger, tags[0].Slot);
                if (random.Chance(0.35))
                {
                    Decided(ledger, await ledgers.EndSegmentAsync(ledger, CancellationToken.None));
                }
            }
        }

        /// <summary>Records a decision of the second ledger's writer: the batch at <paramref name="slot"/> is decided.</summary>
        private void Decided(LedgerId ledger, Slot slot)
        {
            if (ledger == _second)
            {
                trace.Add(Invariant($"#decide:{slot}"));
            }
        }

        private async Task DrainAsync(int drainer)
        {
            for (var pass = 0; pass < 6; pass++)
            {
                await Task.Delay(random.NextTimeSpan(TimeSpan.FromMilliseconds(5)), environment.TimeProvider);
                await DrainOnceAsync(drainer);
            }
        }

        /// <summary>Drains each ledger the drainer's view lists once, crashing it now and then, and drops a complete one.</summary>
        private async Task DrainOnceAsync(int drainer)
        {
            var map = await _maps[drainer - 1].ReadAsync(CancellationToken.None);
            foreach (var ledger in map.Ledgers.Where(listed => listed.Id == _first || listed.Id == _second))
            {
                Slot below;
                if (ledger.Status.Value is LedgerStatus.Closed closed)
                {
                    below = closed.T;
                }
                else if ((await _readers[drainer - 1].ReadTailAsync(ledger, CancellationToken.None)).Value is LedgerTail tail)
                {
                    below = tail.ContiguousTail;
                }
                else
                {
                    continue;
                }

                using var crash = new CancellationTokenSource();
                var crashing = trace.FaultRate > 0 && random.Chance(0.2) ? CrashAfterAsync(crash, random.NextTimeSpan(TimeSpan.FromMilliseconds(4))) : Task.CompletedTask;
                Actor.Value = drainer.ToString(CultureInfo.InvariantCulture);
                ArchiveLogDrainOutcome? outcome = null;
                try
                {
                    outcome = await _workers[drainer - 1].DrainAsync(ledger, below, crash.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ArchiveLogDrainBlockedException)
                {
                    if (exception is ArchiveLogDrainBlockedException blocked)
                    {
                        trace.Violations.Add(Invariant($"drainer {drainer} blocked: {blocked.Message}"));
                    }

                    trace.Crashed(drainer);
                    _workers[drainer - 1] = NewWorker(drainer, _maps[drainer - 1]);
                }
                finally
                {
                    Actor.Value = null;
                    await crashing;
                }

                if (outcome?.Value is ArchiveLogDrainOutcome.Complete)
                {
                    await DropAsync(drainer, ledger.Id);
                }
            }
        }

        private async Task CrashAfterAsync(CancellationTokenSource crash, TimeSpan delay)
        {
            await Task.Delay(delay, environment.TimeProvider);

            // Cancelled inline, on the simulation's thread: CancelAsync would run the callbacks on the thread pool.
            crash.Cancel();
        }

        private async Task DropAsync(int drainer, LedgerId ledger)
        {
            var outcome = await _workers[drainer - 1].SubmitDropAsync(ledger, ledgers.CommitAsync);
            if (outcome.Value is not LedgerMapCommitOutcome.Applied)
            {
                return;
            }

            if (ledger == _first)
            {
                DropFirst();
            }
            else if (!_secondDropped)
            {
                trace.Add("#close");
                trace.Add("drop(2)");
                _secondDropped = true;
            }
        }

        private async Task SweepAsync(ArchiveLogSweeper sweeper)
        {
            for (var pass = 0; pass < 3; pass++)
            {
                await Task.Delay(random.NextTimeSpan(TimeSpan.FromMilliseconds(12)), environment.TimeProvider);
                await SweepOnceAsync(sweeper);
            }
        }

        private async Task SweepOnceAsync(ArchiveLogSweeper sweeper)
        {
            Actor.Value = "sweeper";
            try
            {
                _ = await sweeper.SweepAsync(CancellationToken.None);
            }
            catch (IOException)
            {
                // A listing, read, or deletion failed before it landed; the model's sweep ends as the sweeper's does.
            }
            finally
            {
                Actor.Value = null;
                trace.SweepEnded();
            }
        }

        /// <summary>Returns the model's constants of a decided ledger: each slot's entry, and the slots which start segments.</summary>
        private async Task<(string Entries, string Starts)> ConstantsAsync(LedgerMetadata ledger, Slot end)
        {
            var entries = new List<string>();
            var starts = new List<long>();
            await foreach (var item in ledgers.Reader.ScanAsync(ledger, Slot.Zero, end, CancellationToken.None))
            {
                Assert.True(item.Read.TryGetValue(out LedgerRead.Data data), Invariant($"The trace's scan of ledger {ledger.Id} met {item.Read}."));
                using (data)
                {
                    if (data.View.IsSegmentStart)
                    {
                        starts.Add(data.Slot.Value);
                    }

                    var entry = "\"M\"";
                    foreach (var row in item.Rows)
                    {
                        entry = row.Key.SequenceEqual("k"u8) ? row.Kind switch
                        {
                            RowKind.Append => "\"A\"",
                            RowKind.Replace => "\"R\"",
                            RowKind.Delete => "\"E\"",
                            _ => throw new UnreachableException(),
                        } : "\"O\"";
                    }

                    entries.Add(entry);
                }
            }

            Assert.True(entries.Count < 10, Invariant($"Ledger {ledger.Id} holds {entries.Count} batches, past the model's tags of one digit."));
            return ("[" + string.Join(", ", entries) + "]", "Set(" + string.Join(", ", starts) + ")");
        }

        /// <summary>The run's own oracle: the key's final read folds to every row it proposed.</summary>
        private async Task VerifyAsync()
        {
            foreach (var key in new[] { "k", "o" })
            {
                using var read = await new ArchiveReader(ledgers.Reader, store, SimulationEnvironment.Clock).ReadAsync(Encoding.UTF8.GetBytes(key), CancellationToken.None);
                var state = ArchiveOracle.Fold(read.Prefix, read.Rows);
                var expected = ArchiveOracle.Fold(_proposed.Where(proposed => proposed.Key == key), static proposed => proposed.Kind, static proposed => Encoding.UTF8.GetBytes(proposed.Body));
                if (!expected.SameAs(state))
                {
                    trace.Violations.Add(Invariant($"The final read of {key} folds to {state}, not {expected}."));
                }
            }
        }
    }
}
