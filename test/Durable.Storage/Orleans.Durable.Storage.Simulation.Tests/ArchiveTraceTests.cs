using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// The LSM simulation's traces for validation against <c>test/Durable.Storage/specs/archive/Archive.qnt</c> (test/Durable.Storage/docs/spec/verification.md §5.1;
/// archive-lsm.md §10). Each seed runs one key's history, <c>k</c>, beside another key's, <c>o</c>, over a closed ledger
/// and its successor, which a writer extends while two drainers race to drain both, crash by cancellation, and restart;
/// two compactors race to push or merge the overfull partition; two committers replace the manifest; a sweeper, restarted
/// now and then, sweeps; a dropper drops complete ledgers; and a reader reads <c>k</c>'s history; all over the kit's
/// <c>SimulatedObjectStore</c>, which fails requests, leaves outcomes unknown, and lands writes late. The successor then
/// closes, and, with the faults off, both ledgers drain, drop, and compact, the sweeper sweeps, and the reader reads
/// again. Each request's effect at the store is recorded at the instant it takes effect and mapped onto the model's
/// action, and the run's decided ledgers give the model's constants. With <c>DURABLE_STORAGE_ARCHIVE_TRACES</c> set, each seed
/// writes the trace as a Quint module whose <c>traceReplay</c> run applies every action in order and checks the model's
/// invariants after each, the manifest's watermarks and references after each commit, each upload's rows, and each read's
/// answer against the fold of the decided history at its cut, which <c>test/Durable.Storage/eng/archive-traces.ps1</c> runs with
/// <c>quint test</c>, with trace mutants of its steps, which the model must refuse at the steps they change.
/// </summary>
/// <remarks>
/// <para>
/// The mapping: a drain output's create is <c>drainUpload</c> when the manifest's watermark of its ledger is its start,
/// and a compaction output's whose partition contains <c>k</c> is <c>compactKeep</c>, with the rows of <c>k</c> it holds,
/// when the manifest lists every input; any other output can never be committed, or holds no row of <c>k</c>, and maps to
/// no step, as its delete and the commits which find it superseded do. A manifest replace is a sequence of commits: each
/// drain output it references from its start's watermark is <c>drainCommit</c>, each drain of a segment with no row,
/// which uploads nothing, <c>drainUpload</c> then <c>drainCommit</c>, each compaction output <c>compactCommit</c>, and a
/// compaction which kept no row <c>compactKeep</c> with none, then <c>compactCommit</c>. The sweeper's first listing of a
/// sweep is <c>sweepFirst</c>, its manifest read <c>sweepSecond</c>, each delete <c>sweepDelete</c>, and its end
/// <c>sweepDone</c>. A history read is <c>readMap</c>, <c>readManifest</c>, and <c>readFinish</c> at its end, to the
/// successor's tail the read reached. The writer's decisions give the model's decided tail, as the drains and reads which
/// show them do, <c>ReplaceAsync</c> the successor's close, and the dropper's drops, or a read whose map no longer lists
/// a ledger, the map's drops. Manifest reads and object reads of other processes change nothing the model holds.
/// </para>
/// </remarks>
public sealed class ArchiveTraceTests
{
    private const int FirstSeed = 1;
    private const int SeedCount = 3;
    private const int BodyLength = 8;
    private const string SweeperActor = "sweeper";
    private static readonly byte[] Key = "k"u8.ToArray();
    private static readonly AsyncLocal<string?> Actor = new();

    // The compactors' small sections push a partition's sections down a grid step at a time; the drainers' larger ones
    // keep each segment in one object.
    private static readonly ArchiveOptions CompactionOptions = new() { GridBits = 1, MaxSectionsPerPartition = 2, SectionBytes = 150, SectionBlockBytes = 64 };
    private static readonly ArchiveOptions DrainOptions = CompactionOptions with { SectionBytes = 4096 };

    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    /// <summary>The drains, compactions, commits, drops, sweeps, and reads of a seed make a trace of the model's actions, which writes when asked.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void AnArchiveSimulationRecordsItsTrace(int seed)
    {
        Recorder? trace = null;
        _ = SeededSimulation.Run(seed, async environment => trace = await SimulateAsync(environment));

        var module = string.Create(CultureInfo.InvariantCulture, $"ArchiveTrace{seed}");
        var rendered = new Renderer(trace!).Render();
        Assert.True(trace!.Violations.Count == 0, string.Join(" | ", trace.Violations));
        Assert.Contains(rendered.Steps, static step => step.Action.StartsWith("drainUpload(", StringComparison.Ordinal));
        Assert.Contains(rendered.Steps, static step => step.Action.StartsWith("drainCommit(", StringComparison.Ordinal));
        Assert.Contains(rendered.Steps, static step => step.Action == "sweepFirst");
        Assert.Contains(rendered.Steps, static step => step.Action.StartsWith("readFinish(", StringComparison.Ordinal));
        Assert.Contains(rendered.Steps, static step => step.Action == "drop(2)");
        if (Environment.GetEnvironmentVariable("DURABLE_STORAGE_ARCHIVE_TRACES") is { Length: > 0 } directory)
        {
            _ = Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, module + ".qnt"), rendered.Module(module));
        }
    }

    private static async Task<Recorder> SimulateAsync(SimulationEnvironment environment)
    {
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
        await using var objects = new SimulatedObjectStore(environment, "archive")
        {
            UnknownOutcomeRate = 0.05,
            RetriedAnswerRate = 0.03,
            ReadFailureRate = 0.03,
        };
        var trace = new Recorder();
        objects.Effects = trace.Effect;
        var options = new LedgerWriterOptions(1 << 20, 1 << 10, TimeSpan.FromDays(1), LedgerWriterOptions.DefaultMaxProposalBytes);
        await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None, options);
        await using var scenario = new Scenario(environment, ledgers, objects, trace);
        await scenario.RunAsync();
        return trace;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>A row of <c>k</c> as the model writes it: its tag, its ledger's index times 10 plus its slot, and its kind.</summary>
    private readonly record struct ModelRow(int Tag, string Kind)
    {
        public override string ToString() => Invariant($"{{ tag: {Tag}, kind: \"{Kind}\" }}");
    }

    /// <summary>A ledger's watermark in a manifest value: complete, or recorded below a slot.</summary>
    private readonly record struct Watermark(bool Complete, long Below);

    private enum SweepStep
    {
        Listed,
        Snapshot,
        Deleted,
        Ended,
    }

    /// <summary>One effect the recorder saw, in the order the store's requests took effect.</summary>
    private abstract record TraceEvent;

    /// <summary>The successor's writer decided the batch at <paramref name="Slot"/>.</summary>
    private sealed record Decision(long Slot) : TraceEvent;

    /// <summary>The successor closed.</summary>
    private sealed record Closing : TraceEvent;

    /// <summary>The map dropped the ledger of index <paramref name="Ledger"/>.</summary>
    private sealed record Dropping(int Ledger) : TraceEvent;

    /// <summary>
    /// A run object's create landed: a drain output of the ledger of index <paramref name="Ledger"/> from
    /// <paramref name="From"/>, or a compaction output of <paramref name="Inputs"/>, holding <paramref name="Rows"/> of
    /// <c>k</c>, whose partitions contain <c>k</c> when <paramref name="HoldsKey"/>.
    /// </summary>
    private sealed record Upload(string Name, bool Drain, int Ledger, long From, string[] Inputs, bool HoldsKey, ModelRow[] Rows) : TraceEvent;

    /// <summary>A manifest replace landed, referencing <paramref name="Objects"/>, with the two ledgers' watermarks.</summary>
    private sealed record Manifest(string[] Objects, Watermark First, Watermark Second) : TraceEvent;

    /// <summary>A step of the sweeper's.</summary>
    private sealed record Sweep(SweepStep Step, string? Name) : TraceEvent;

    /// <summary>
    /// A history read of <c>k</c> answered: whether its map listed the closed ledger, the successor's tail it read, none
    /// when its map no longer listed it, its prefix's bodies, and the live rows it did not hold.
    /// </summary>
    private sealed record Read(bool ListsFirst, long? ThroughSecond, string[] PrefixBodies, ModelRow[] Live) : TraceEvent;

    /// <summary>
    /// The recorded effects, the constants the run's decided ledgers give, and the violations the run met.
    /// </summary>
    private sealed class Recorder
    {
        public List<TraceEvent> Events { get; } = [];

        public List<string> Violations { get; } = [];

        public LedgerId First { get; set; }

        public LedgerId Second { get; set; }

        /// <summary>Gets each ledger's decided entries, by index: the model's <c>Ledger1</c> and <c>Ledger2</c>.</summary>
        public string[][] Entries { get; } = [[], [], []];

        /// <summary>Gets the slots which start each ledger's segments, by index.</summary>
        public long[][] Starts { get; } = [[], [], []];

        /// <summary>Gets the row of <c>k</c> each body names, which the decided ledgers hold.</summary>
        public Dictionary<string, ModelRow> Bodies { get; } = [];

        public int Index(LedgerId ledger) => ledger == First ? 1 : ledger == Second ? 2 : 0;

        public ModelRow RowOf(Tag tag, RowKind kind) => new(Index(tag.Ledger) * 10 + (int)tag.Slot.Value, kind switch
        {
            RowKind.Append => "A",
            RowKind.Replace => "R",
            RowKind.Delete => "E",
            RowKind.Message => throw new UnreachableException("RowKind.Message is not part of LSM traces."),
            _ => throw new UnreachableException(),
        });

        /// <summary>Records a request's effect at the store, at the instant it took effect; it runs in the store's step, so it never awaits.</summary>
        public void Effect(ObjectStoreEffect effect)
        {
            switch (effect.Kind)
            {
                case ObjectStoreEffectKind.Written when effect.Name == ObjectName.Manifest.Value:
                    if (ManifestFormat.Decode(new ReadOnlySequence<byte>(effect.Content!)).Value is ManifestValue value)
                    {
                        Events.Add(new Manifest([.. value.Sections.Select(static section => section.Object.Value).Distinct()], WatermarkOf(value, First), WatermarkOf(value, Second)));
                    }
                    else
                    {
                        Violations.Add("A manifest replace landed with a value which breaks its format.");
                    }

                    break;
                case ObjectStoreEffectKind.Written when effect.Name.StartsWith(RunObjectNames.DrainPrefix, StringComparison.Ordinal) || effect.Name.StartsWith(RunObjectNames.CompactionPrefix, StringComparison.Ordinal):
                    Uploaded(effect.Name, effect.Content!);
                    break;
                case ObjectStoreEffectKind.Deleted when Actor.Value is SweeperActor:
                    Events.Add(new Sweep(SweepStep.Deleted, effect.Name));
                    break;
                case ObjectStoreEffectKind.Deleted:
                    Violations.Add(Invariant($"{effect.Name} was deleted by {Actor.Value ?? "a process"} other than the sweeper."));
                    break;
                case ObjectStoreEffectKind.Listed when Actor.Value is SweeperActor:
                    Events.Add(new Sweep(SweepStep.Listed, null));
                    break;
                case ObjectStoreEffectKind.Read when Actor.Value is SweeperActor && effect.Name == ObjectName.Manifest.Value:
                    Events.Add(new Sweep(SweepStep.Snapshot, null));
                    break;
                case ObjectStoreEffectKind.Written:
                case ObjectStoreEffectKind.Appended:
                case ObjectStoreEffectKind.Sealed:
                case ObjectStoreEffectKind.Listed:
                case ObjectStoreEffectKind.Read:
                default:
                    break;
            }
        }

        public void Decided(LedgerId ledger, Slot slot)
        {
            if (ledger == Second)
            {
                Events.Add(new Decision(slot.Value));
            }
        }

        public void Closed() => Events.Add(new Closing());

        public void Dropped(LedgerId ledger)
        {
            if (Index(ledger) is var index and > 0)
            {
                Events.Add(new Dropping(index));
            }
        }

        public void SweepEnded() => Events.Add(new Sweep(SweepStep.Ended, null));

        /// <summary>Records a history read's answer: its cut, its prefix's bodies, and the live rows of the two ledgers it did not hold.</summary>
        public void Answered(ArchiveRead read)
        {
            var content = read.Prefix is { } prefix ? Encoding.UTF8.GetString(prefix.Content.AsReadOnlySequence()) : string.Empty;
            if (content.Length % BodyLength != 0)
            {
                Violations.Add(Invariant($"A read's prefix holds {content.Length} bytes, not whole bodies of {BodyLength}."));
            }

            var live = new List<ModelRow>();
            foreach (var row in read.Rows)
            {
                if (!row.IsHeld && Index(row.Tag.Ledger) > 0)
                {
                    live.Add(RowOf(row.Tag, row.Kind));
                }
            }

            Events.Add(new Read(
                read.Through.ContainsKey(First),
                read.Through.TryGetValue(Second, out var through) ? through.Value : null,
                [.. Enumerable.Range(0, content.Length / BodyLength).Select(index => content.Substring(index * BodyLength, BodyLength))],
                [.. live]));
        }

        private static Watermark WatermarkOf(ManifestValue value, LedgerId ledger) =>
            new(value.IsComplete(ledger), value.RecordedWatermark(ledger).Value);

        private void Uploaded(string name, byte[] bytes)
        {
            using var content = ArcBuffer.Wrap(bytes);
            if (RunObjectFormat.Decode(content).Value is not RunObject decoded)
            {
                Violations.Add(Invariant($"{name} landed with content which breaks its format."));
                return;
            }

            using (decoded)
            {
                var rows = new List<ModelRow>();
                var holdsKey = false;
                foreach (var section in decoded.Sections)
                {
                    holdsKey |= section.Metadata.Entry.Partition.Contains(Key);
                    foreach (var row in section.Rows.Rows)
                    {
                        if (row.Row.Key.Span.SequenceEqual(Key))
                        {
                            rows.Add(RowOf(row.Tag, row.Row.Kind));
                        }
                    }
                }

                switch (decoded.Trailer.Condition.Value)
                {
                    case RunObjectCondition.Drain drain:
                        Events.Add(new Upload(name, Drain: true, Index(drain.Ledger), drain.From.Value, [], holdsKey, [.. rows]));
                        break;
                    case RunObjectCondition.Compaction compaction:
                        Events.Add(new Upload(name, Drain: false, 0, 0, [.. compaction.Inputs.Select(static input => input.Object.Value).Distinct()], holdsKey, [.. rows]));
                        break;
                    case null:
                    default:
                        throw new UnreachableException("conventions.md §15: a condition holds a case.");
                }
            }
        }
    }

    /// <summary>A step of the replay: the model's action, and what the state after it must also satisfy.</summary>
    private sealed record Step(string Action, IReadOnlyList<string> Expects)
    {
        public string Line => Invariant($"    .then({Action}).expect({(Expects.Count == 0 ? "Invariants" : "and { Invariants, " + string.Join(", ", Expects) + " }")})");
    }

    /// <summary>The rendered trace: the model's constants and its steps.</summary>
    private sealed record RenderedTrace(string Constants, IReadOnlyList<Step> Steps)
    {
        /// <summary>Writes the trace as a Quint module.</summary>
        public string Module(string name)
        {
            var text = new StringBuilder()
                .AppendLine(Invariant($"// The trace of a seed of {nameof(ArchiveTraceTests)}, generated; trace validation replays it."))
                .AppendLine(Invariant($"module {name} {{"))
                .AppendLine(Invariant($"  import Archive({Constants}, Mutation = \"none\").* from \"../../specs/archive/Archive\""))
                .AppendLine("  val Invariants = and { ReferencedExist, FoldPreserved, WatermarksMonotone, WatermarksAtSegmentStarts, NoDrainSuperseded, ReadsCorrect }")
                .AppendLine("  run traceReplay = init.expect(Invariants)");
            foreach (var step in Steps)
            {
                _ = text.AppendLine(step.Line);
            }

            return text.AppendLine("}").ToString();
        }
    }

    /// <summary>
    /// Maps the recorded effects onto the model's actions, tracking the model's state the actions reach: its objects, the
    /// manifest's watermarks and references, the successor's decided tail and close, the drops, and the sweeper's phase.
    /// </summary>
    private sealed class Renderer(Recorder trace)
    {
        private readonly Dictionary<string, ModelObject> _objects = [];
        private readonly HashSet<string> _uncommittable = [];
        private readonly HashSet<string> _outside = [];
        private readonly HashSet<int> _refs = [];
        // The model's empty objects, which stand for a drain of a segment with no row and a compaction which kept none:
        // the manifest references neither, and the model's references keep both.
        private readonly HashSet<int> _empty = [];
        private readonly long[] _watermark = new long[3];
        private readonly List<Step> _steps = [];
        private readonly long[] _ends = [0, trace.Entries[1].Length, trace.Entries[2].Length];
        private int _nextId = 1;
        private long _decided;
        private bool _closed;
        private readonly bool[] _dropped = new bool[3];
        private int _sweeperPhase;

        public RenderedTrace Render()
        {
            _decided = _ends[2] > 0 ? 1 : 0;
            foreach (var recorded in trace.Events)
            {
                switch (recorded)
                {
                    case Decision decision:
                        DecideThrough(decision.Slot);
                        break;
                    case Closing:
                        Close();
                        break;
                    case Dropping dropping:
                        Drop(dropping.Ledger);
                        break;
                    case Upload { Drain: true } upload:
                        DrainUpload(upload);
                        break;
                    case Upload upload:
                        CompactionUpload(upload);
                        break;
                    case Manifest manifest:
                        Commit(manifest);
                        break;
                    case Sweep sweep:
                        Sweeping(sweep);
                        break;
                    case Read read:
                        Reading(read);
                        break;
                    default:
                        throw new UnreachableException();
                }
            }

            var constants = Invariant($"Ledger1 = [{string.Join(", ", trace.Entries[1].Select(static entry => $"\"{entry}\""))}], Ledger2 = [{string.Join(", ", trace.Entries[2].Select(static entry => $"\"{entry}\""))}], Starts1 = Set({string.Join(", ", trace.Starts[1])}), Starts2 = Set({string.Join(", ", trace.Starts[2])}), MaxObjects = {Math.Max(1, _nextId - 1)}");
            return new RenderedTrace(constants, _steps);
        }

        private static string SetOf<T>(IEnumerable<T> items) => "Set(" + string.Join(", ", items) + ")";

        private static string RowsOf(IEnumerable<ModelRow> rows) => SetOf(rows.Distinct().OrderBy(static row => row.Tag));

        private Step Emit(string action, params string[] expects)
        {
            var step = new Step(action, expects);
            _steps.Add(step);
            return step;
        }

        /// <summary>The end of the segment of the ledger of index <paramref name="ledger"/> which starts at <paramref name="start"/>: the next segment's start, or <c>T</c>.</summary>
        private long EndOfSegment(int ledger, long start) =>
            trace.Starts[ledger].Where(next => next > start).DefaultIfEmpty(_ends[ledger]).Min();

        private bool Complete(int ledger) => (ledger == 1 || _closed) && _watermark[ledger] == _ends[ledger];

        /// <summary>Decides the successor's batches through the one at <paramref name="slot"/>, a segment at a time, as <c>decideStart</c> does.</summary>
        private void DecideThrough(long slot)
        {
            var through = Math.Min(_ends[2], slot + 1);
            while (_decided < through)
            {
                var next = trace.Starts[2].Where(start => start >= _decided).DefaultIfEmpty(long.MaxValue).Min();
                _decided = next < _ends[2] ? next + 1 : _ends[2];
                _ = Emit("decideStart");
            }
        }

        private void Close()
        {
            DecideThrough(_ends[2] - 1);
            if (!_closed)
            {
                _closed = true;
                _ = Emit("close2");
            }
        }

        private void Drop(int ledger)
        {
            if (ledger == 2)
            {
                Close();
                Drop(1);
            }

            if (!_dropped[ledger])
            {
                _dropped[ledger] = true;
                _ = Emit(Invariant($"drop({ledger})"));
            }
        }

        /// <summary>Takes the successor's decided tail past <paramref name="end"/>, or closes it when <paramref name="end"/> is its <c>T</c>.</summary>
        private void Decide(int ledger, long end)
        {
            if (ledger != 2)
            {
                return;
            }

            if (end == _ends[2])
            {
                Close();
            }
            else
            {
                DecideThrough(end);
            }
        }

        /// <summary>
        /// A drain output from the watermark is <c>drainUpload</c>; one from another start can never be committed, and one
        /// of another ledger lies outside the model.
        /// </summary>
        private void DrainUpload(Upload upload)
        {
            var ledger = upload.Ledger;
            if (ledger == 0)
            {
                _ = _outside.Add(upload.Name);
                return;
            }

            if (Complete(ledger) || _watermark[ledger] != upload.From)
            {
                _ = _uncommittable.Add(upload.Name);
                return;
            }

            var end = EndOfSegment(ledger, upload.From);
            Decide(ledger, end);
            var id = _nextId++;
            _objects[upload.Name] = new ModelObject(id, Drain: true, ledger, upload.From, end, [.. upload.Rows], []);
            _ = Emit(Invariant($"drainUpload({ledger}, {end})"), Invariant($"s.obj.get({id}).rows == {RowsOf(upload.Rows)}"));
        }

        /// <summary>
        /// A compaction output of <c>k</c>'s partition whose inputs the manifest lists is <c>compactKeep</c>; one of another
        /// partition holds no row of <c>k</c>, and one whose input left the manifest can never be committed.
        /// </summary>
        private void CompactionUpload(Upload upload)
        {
            if (!upload.HoldsKey)
            {
                _ = _outside.Add(upload.Name);
                return;
            }

            if (!upload.Inputs.All(input => _objects.TryGetValue(input, out var known) && _refs.Contains(known.Id)))
            {
                _ = _uncommittable.Add(upload.Name);
                return;
            }

            var inputs = upload.Inputs.Select(input => _objects[input]).ToArray();
            var id = _nextId++;
            _objects[upload.Name] = new ModelObject(id, Drain: false, 0, 0, 0, [.. upload.Rows], [.. inputs.Select(static input => input.Id)]);
            var inputIds = SetOf(inputs.Select(static input => input.Id).Order());
            _ = Emit(Invariant($"compactKeep({inputIds}, {RowsOf(upload.Rows)})"));
        }

        /// <summary>
        /// Maps a manifest replace onto the commits it applied, drains first, a ledger at a time from its watermark, then
        /// compactions, and expects the model's watermarks and references to be the manifest's.
        /// </summary>
        private void Commit(Manifest manifest)
        {
            var before = _steps.Count;
            var referenced = new HashSet<int>();
            foreach (var name in manifest.Objects)
            {
                if (_objects.TryGetValue(name, out var known))
                {
                    _ = referenced.Add(known.Id);
                }
                else if (!_outside.Contains(name))
                {
                    // An output whose condition was false when it landed can never be committed (LSM-I1, LSM-I2, LSM-I5).
                    trace.Violations.Add(Invariant($"A manifest replace references {name}, which could not be committed when it landed."));
                }
            }

            var added = _objects.Values.Where(known => referenced.Contains(known.Id) && !_refs.Contains(known.Id)).ToList();
            var removed = _refs.Where(id => !_empty.Contains(id) && !referenced.Contains(id)).ToHashSet();
            Watermark[] watermarks = [default, manifest.First, manifest.Second];
            for (var ledger = 1; ledger <= 2; ledger++)
            {
                var target = watermarks[ledger].Complete ? _ends[ledger] : watermarks[ledger].Below;
                var drains = added.Where(known => known.Drain && known.Ledger == ledger).OrderBy(static known => known.A).ToList();
                while (_watermark[ledger] < target)
                {
                    var from = _watermark[ledger];
                    if (drains.FirstOrDefault(known => known.A == from) is { } drain)
                    {
                        _ = drains.Remove(drain);
                        CommitDrain(drain);
                        continue;
                    }

                    // A drain of a segment which holds no row uploads nothing; the model uploads its empty object.
                    var end = EndOfSegment(ledger, from);
                    Decide(ledger, end);
                    var id = _nextId++;
                    var empty = new ModelObject(id, Drain: true, ledger, from, end, [], []);
                    _objects[Invariant($"empty-drain-{id}")] = empty;
                    _ = _empty.Add(id);
                    _ = Emit(Invariant($"drainUpload({ledger}, {end})"), Invariant($"s.obj.get({id}).rows == Set()"));
                    CommitDrain(empty);
                }

                // A drain the walk does not reach commits out of order, which the model refuses.
                foreach (var drain in drains)
                {
                    CommitDrain(drain);
                }
            }

            foreach (var output in added.Where(static known => !known.Drain))
            {
                _ = Emit(Invariant($"compactCommit({output.Id})"));
                _ = _refs.Add(output.Id);
                _refs.ExceptWith(output.Inputs);
                removed.ExceptWith(output.Inputs);
            }

            if (removed.Count > 0)
            {
                // A compaction which kept no row uploaded nothing; the model uploads its empty output.
                var id = _nextId++;
                _objects[Invariant($"empty-compaction-{id}")] = new ModelObject(id, Drain: false, 0, 0, 0, [], [.. removed]);
                _ = _empty.Add(id);
                _ = Emit(Invariant($"compactKeep({SetOf(removed.Order())}, Set())"));
                _ = Emit(Invariant($"compactCommit({id})"));
                _ = _refs.Add(id);
                _refs.ExceptWith(removed);
            }

            var expectedRefs = referenced.Union(_empty).Order().ToArray();
            var expectedWatermarks = new[] { 0L, watermarks[1].Complete ? _ends[1] : watermarks[1].Below, watermarks[2].Complete ? _ends[2] : watermarks[2].Below };
            if (_steps.Count == before)
            {
                if (!_refs.SetEquals(expectedRefs) || expectedWatermarks[1] != _watermark[1] || expectedWatermarks[2] != _watermark[2])
                {
                    trace.Violations.Add("A manifest replace changed the watermarks or references by no commit the model takes.");
                }

                return;
            }

            var last = _steps[^1];
            _steps[^1] = last with { Expects = [.. last.Expects, Invariant($"s.watermark == Map(1 -> {expectedWatermarks[1]}, 2 -> {expectedWatermarks[2]})"), Invariant($"s.refs == {SetOf(expectedRefs)}")] };
        }

        private void CommitDrain(ModelObject drain)
        {
            _ = Emit(Invariant($"drainCommit({drain.Id})"));
            _ = _refs.Add(drain.Id);
            _watermark[drain.Ledger] = Math.Max(_watermark[drain.Ledger], drain.B);
        }

        private void Sweeping(Sweep sweep)
        {
            switch (sweep.Step)
            {
                case SweepStep.Listed when _sweeperPhase == 0:
                    _ = Emit("sweepFirst");
                    _sweeperPhase = 1;
                    break;
                case SweepStep.Snapshot when _sweeperPhase == 1:
                    _ = Emit("sweepSecond");
                    _sweeperPhase = 2;
                    break;
                case SweepStep.Deleted when _objects.TryGetValue(sweep.Name!, out var deleted):
                    _ = Emit(Invariant($"sweepDelete({deleted.Id})"));
                    break;
                case SweepStep.Ended:
                    if (_sweeperPhase == 1)
                    {
                        _ = Emit("sweepSecond");
                        _sweeperPhase = 2;
                    }

                    if (_sweeperPhase == 2)
                    {
                        _ = Emit("sweepDone");
                    }

                    _sweeperPhase = 0;
                    break;
                case SweepStep.Listed:
                case SweepStep.Snapshot:
                case SweepStep.Deleted:
                default:
                    break;
            }
        }

        /// <summary>
        /// Maps a history read onto the model's reader, at its end, after the drops and decisions it shows, and expects its
        /// answer, folded from its prefix and its live rows, to be the fold of the decided history at its cut.
        /// </summary>
        private void Reading(Read read)
        {
            if (!read.ListsFirst)
            {
                Drop(1);
            }

            if (read.ThroughSecond is null)
            {
                Drop(2);
            }

            var through = read.ThroughSecond ?? _ends[2];
            if (through > 0)
            {
                DecideThrough(through - 1);
            }

            var state = new HashSet<ModelRow>();
            foreach (var body in read.PrefixBodies)
            {
                if (trace.Bodies.TryGetValue(body, out var row))
                {
                    _ = state.Add(row);
                }
                else
                {
                    trace.Violations.Add(Invariant($"A read's prefix holds the body {body}, which no decided row of k holds."));
                }
            }

            foreach (var row in read.Live)
            {
                switch (row.Kind)
                {
                    case "R":
                        state = [row];
                        break;
                    case "E":
                        state.Clear();
                        break;
                    default:
                        _ = state.Add(row);
                        break;
                }
            }

            _ = Emit("readMap");
            _ = Emit("readManifest");
            _ = Emit(Invariant($"readFinish({through})"), Invariant($"{RowsOf(state)} == reduced(live(1).union(live(2).filter(x => slot(x) < {through})))"));

        }
    }

    /// <summary>A model object: its ID, whether a drain uploaded it, a drain's ledger and segment, its rows of <c>k</c>, and a compaction's inputs.</summary>
    private sealed record ModelObject(int Id, bool Drain, int Ledger, long A, long B, ModelRow[] Rows, int[] Inputs);

    private sealed class Scenario(SimulationEnvironment environment, LiveLedgers ledgers, SimulatedObjectStore objects, Recorder trace) : IAsyncDisposable
    {
        private readonly SimulationRandom _random = environment.CreateRandom("archive-trace");
        private readonly List<ManifestCommitter> _committers = [];
        private readonly List<(LedgerMapView View, LedgerReader Reader)> _drainers = [];
        private readonly List<DrainWorker> _workers = [];
        private readonly List<(LedgerMapView View, ManifestView Manifest, CompactionWorker Worker)> _compactors = [];
        private readonly List<(string Key, RowKind Kind, string Body)> _proposed = [];
        private LedgerId _first;
        private LedgerId _second;
        private bool _faults = true;
        private bool _secondDropped;

        private Sweeper SweeperProcess { get => field ?? throw new InvalidOperationException("The scenario has not started."); set; }

        private LedgerMapView DropperView { get => field ?? throw new InvalidOperationException("The scenario has not started."); set; }

        private ManifestView Evidence { get => field ?? throw new InvalidOperationException("The scenario has not started."); set; }

        private LedgerDropper Dropper { get => field ?? throw new InvalidOperationException("The scenario has not started."); set; }

        private ArchiveReader Reader { get => field ?? throw new InvalidOperationException("The scenario has not started."); set; }

        public async Task RunAsync()
        {
            await ledgers.BootstrapAsync(CancellationToken.None);
            _first = ledgers.OpenContaining(Key).Id;
            trace.First = _first;
            await WriteAsync(_first);
            await ledgers.ReplaceAsync(_first, CancellationToken.None);
            var t1 = Assert.IsType<LedgerStatus.Closed>(ledgers.Ledger(_first).Status.Value).T;
            await ConstantsAsync(ledgers.Ledger(_first), t1, 1);
            _second = ledgers.OpenContaining(Key).Id;
            trace.Second = _second;
            await StartAsync();

            await SimulationTasks.WhenAll(WriteAsync(_second), DrainAsync(0), DrainAsync(1), CompactAsync(0), CompactAsync(1), SweepAsync(), DropAsync(), ReadAsync());
            await ledgers.ReplaceAsync(_second, CancellationToken.None);
            trace.Closed();
            var t2 = Assert.IsType<LedgerStatus.Closed>(ledgers.Ledger(_second).Status.Value).T;
            await ConstantsAsync(ledgers.Ledger(_second), t2, 2);

            // With the faults off and every late write landed, the ledgers drain, drop, and compact, the sweeper sweeps until
            // it deletes what it marked, and the reader reads the archive's whole history.
            (objects.UnknownOutcomeRate, objects.RetriedAnswerRate, objects.ReadFailureRate, _faults) = (0, 0, 0, false);
            await Task.Delay(objects.MaxLandingDelay + TimeSpan.FromMilliseconds(1), environment.TimeProvider);
            for (var attempt = 0; attempt < 50 && !_secondDropped; attempt++)
            {
                await DrainOnceAsync(0);
                await DropOnceAsync();
            }

            if (!_secondDropped)
            {
                trace.Violations.Add("The ledgers never dropped once the faults stopped.");
            }

            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (!await CompactOnceAsync(0))
                {
                    break;
                }
            }

            for (var sweep = 0; sweep < 3; sweep++)
            {
                await SweepOnceAsync();
            }

            await ReadOnceAsync();
            await VerifyAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var committer in _committers)
            {
                await committer.DisposeAsync();
            }
        }

        private async Task StartAsync()
        {
            for (var index = 0; index < 2; index++)
            {
                _committers.Add(new ManifestCommitter(objects, new ManifestView(objects), SimulationEnvironment.Clock, TimeSpan.Zero));
                var view = await ledgers.OpenViewAsync(CancellationToken.None);
                _drainers.Add((view, ledgers.OpenReader(view)));
                _workers.Add(NewWorker(index));
                var manifest = new ManifestView(objects);
                _compactors.Add((await ledgers.OpenViewAsync(CancellationToken.None), manifest, new CompactionWorker(objects, new TailCache(objects, CompactionOptions), manifest, CompactionOptions)));
            }

            SweeperProcess = NewSweeper();
            DropperView = await ledgers.OpenViewAsync(CancellationToken.None);
            Evidence = new ManifestView(objects);
            Dropper = new LedgerDropper(DropperView, Evidence, NullLogger<LedgerDropper>.Instance);
            var readerView = await ledgers.OpenViewAsync(CancellationToken.None);
            var store = new LsmArchiveStore(objects, new ManifestView(objects), new TailCache(objects, CompactionOptions), CompactionOptions, SimulationEnvironment.Clock);
            Reader = new ArchiveReader(ledgers.OpenReader(readerView), store, SimulationEnvironment.Clock);
        }

        private DrainWorker NewWorker(int drainer) =>
            new(objects, new ManifestView(objects), _drainers[drainer].Reader, DrainOptions, SimulationEnvironment.Clock) { BackpressureDelay = TimeSpan.FromMilliseconds(1) };

        private Sweeper NewSweeper() => new(objects, new ManifestView(objects), SimulationEnvironment.Clock, NullLogger<Sweeper>.Instance);

        /// <summary>Proposes rows of <c>k</c> and <c>o</c> to <paramref name="ledger"/>, one a batch, ending segments now and then.</summary>
        private async Task WriteAsync(LedgerId ledger)
        {
            for (var count = 0; count < 4; count++)
            {
                await Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(4)), environment.TimeProvider);
                var key = _random.Chance(0.7) ? "k" : "o";
                var kind = _random.Next(10) switch
                {
                    < 6 => RowKind.Append,
                    < 8 => RowKind.Replace,
                    _ => RowKind.Delete,
                };

                // Every Delete is an empty one, an expiry, which a compaction may drop with every older row of its key.
                var body = kind == RowKind.Delete ? string.Empty : Invariant($"{key}{_proposed.Count:D7}");
                var tags = await ledgers.ProposeAsync(ledger, [LiveRow.Of(key, kind, body)], CancellationToken.None);
                _proposed.Add((key, kind, body));
                trace.Decided(ledger, tags[0].Slot);
                if (_random.Chance(0.45))
                {
                    trace.Decided(ledger, await ledgers.EndSegmentAsync(ledger, CancellationToken.None));
                }
            }
        }

        private async Task DrainAsync(int drainer)
        {
            for (var pass = 0; pass < 6; pass++)
            {
                await Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(5)), environment.TimeProvider);
                await DrainOnceAsync(drainer);
            }
        }

        /// <summary>Drains each of the two ledgers the drainer's view lists, a segment a drain, crashing it now and then.</summary>
        private async Task DrainOnceAsync(int drainer)
        {
            var (view, reader) = _drainers[drainer];
            var map = await view.ReadAsync(CancellationToken.None);
            foreach (var ledger in map.Ledgers.Where(listed => listed.Id == _first || listed.Id == _second))
            {
                Slot below;
                if (ledger.Status.Value is LedgerStatus.Closed closed)
                {
                    below = closed.T;
                }
                else if ((await reader.ReadTailAsync(ledger, CancellationToken.None)).Value is LedgerTail tail)
                {
                    below = tail.ContiguousTail;
                }
                else
                {
                    continue;
                }

                for (var drain = 0; drain < 16; drain++)
                {
                    using var crash = new CancellationTokenSource();
                    var crashing = _faults && _random.Chance(0.15) ? CrashAfterAsync(crash, _random.NextTimeSpan(TimeSpan.FromMilliseconds(4))) : Task.CompletedTask;
                    DrainOutcome? outcome = null;
                    try
                    {
                        outcome = await _workers[drainer].DrainAsync(ledger, below, Commit, crash.Token);
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or IOException or TimeoutException)
                    {
                        // A crash or a failed request: the drainer's process restarts with a fresh manifest view.
                        _workers[drainer] = NewWorker(drainer);
                    }
                    catch (ArchiveCorruptException exception)
                    {
                        trace.Violations.Add(Invariant($"A drain of ledger {ledger.Id} found the archive corrupt: {exception.Message}"));
                    }
                    finally
                    {
                        await crashing;
                    }

                    if (outcome?.Value is not DrainOutcome.Drained)
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>Submits a drain's change through a committer chosen at random. The worker calls it from the code under test, so it never awaits.</summary>
        private Task<ManifestCommitOutcome> Commit(ManifestChange change, CancellationToken cancellationToken) =>
            _committers[_random.Next(_committers.Count)].CommitAsync(change, cancellationToken);

        private async Task CrashAfterAsync(CancellationTokenSource crash, TimeSpan delay)
        {
            await Task.Delay(delay, environment.TimeProvider);

            // Cancelled inline, on the simulation's thread: CancelAsync would run the callbacks on the thread pool.
            crash.Cancel();
        }

        private async Task CompactAsync(int compactor)
        {
            for (var pass = 0; pass < 5; pass++)
            {
                await Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(6)), environment.TimeProvider);
                _ = await CompactOnceAsync(compactor);
            }
        }

        /// <summary>
        /// Compacts an overfull partition of a manifest value the compactor reads, before or after its map, and commits the
        /// change through a committer chosen at random; answers whether a compaction applied.
        /// </summary>
        private async Task<bool> CompactOnceAsync(int compactor)
        {
            var (view, manifestView, worker) = _compactors[compactor];
            ManifestValue manifest;
            LedgerMapValue map;
            try
            {
                if (_random.Chance(0.5))
                {
                    map = await view.ReadAsync(CancellationToken.None);
                    manifest = (await manifestView.ReadAsync(CancellationToken.None)).Value;
                }
                else
                {
                    manifest = (await manifestView.ReadAsync(CancellationToken.None)).Value;
                    map = await view.ReadAsync(CancellationToken.None);
                }
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                return false;
            }

            var overfull = CompactionWorker.OverfullPartitions(manifest, CompactionOptions);
            if (overfull.IsEmpty)
            {
                return false;
            }

            try
            {
                if ((await worker.CompactAsync(overfull[_random.Next(overfull.Length)], manifest, map, CancellationToken.None)).Value is not CompactionOutcome.Compacted compacted)
                {
                    return false;
                }

                var committed = await _committers[_random.Next(_committers.Count)].CommitAsync(compacted.Change, CancellationToken.None);
                return committed.Value is ManifestCommitOutcome.Applied;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                return false;
            }
            catch (ArchiveCorruptException exception)
            {
                trace.Violations.Add(Invariant($"A compaction found the archive corrupt: {exception.Message}"));
                return false;
            }
        }

        private async Task SweepAsync()
        {
            for (var pass = 0; pass < 4; pass++)
            {
                await Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(10)), environment.TimeProvider);
                await SweepOnceAsync();
                if (_random.Chance(0.25))
                {
                    // The sweeper's process restarts, which loses its marks.
                    SweeperProcess = NewSweeper();
                }
            }
        }

        private async Task SweepOnceAsync()
        {
            Actor.Value = SweeperActor;
            try
            {
                _ = await SweeperProcess.SweepAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                // A listing or read failed; the model's sweep ends as the sweeper's does.
            }
            finally
            {
                Actor.Value = null;
                trace.SweepEnded();
            }
        }

        private async Task DropAsync()
        {
            for (var pass = 0; pass < 3; pass++)
            {
                await Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(8)), environment.TimeProvider);
                await DropOnceAsync();
            }
        }

        /// <summary>Drops every complete ledger the dropper's map and its evidence, the manifest values it read, allow.</summary>
        private async Task DropOnceAsync()
        {
            _ = await DropperView.ReadAsync(CancellationToken.None);
            try
            {
                _ = await Evidence.ReadAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                // The dropper decides from the evidence it holds.
            }

            foreach (var dropped in await Dropper.DropCompleteAsync(ledgers.CommitAsync, CancellationToken.None))
            {
                trace.Dropped(dropped);
                _secondDropped |= dropped == _second;
            }
        }

        private async Task ReadAsync()
        {
            for (var count = 0; count < 6; count++)
            {
                await Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(5)), environment.TimeProvider);
                await ReadOnceAsync();
            }
        }

        private async Task ReadOnceAsync()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using var read = await Reader.ReadAsync(Key, CancellationToken.None);
                    trace.Answered(read);
                    return;
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    // A store read failed; the reader reads again.
                }
                catch (ArchiveCorruptException exception)
                {
                    trace.Violations.Add(Invariant($"A read found the archive corrupt: {exception.Message}"));
                    return;
                }
            }
        }

        /// <summary>
        /// Records the model's constants of a decided ledger: each slot's entry of <c>k</c>, or <c>M</c> for a batch which
        /// holds none, the slots which start segments, and the row each of <c>k</c>'s bodies names.
        /// </summary>
        private async Task ConstantsAsync(LedgerMetadata ledger, Slot end, int index)
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

                    var entry = "M";
                    foreach (var row in item.Rows)
                    {
                        if (row.Key.SequenceEqual(Key))
                        {
                            var modelRow = trace.RowOf(row.Tag, row.Kind);
                            entry = modelRow.Kind;
                            if (row.BodyLength > 0)
                            {
                                trace.Bodies[Encoding.UTF8.GetString(row.Body)] = modelRow;
                            }
                        }
                    }

                    entries.Add(entry);
                }
            }

            Assert.True(entries.Count < 10, Invariant($"Ledger {ledger.Id} holds {entries.Count} batches, past the model's tags of one digit."));
            trace.Entries[index] = [.. entries];
            trace.Starts[index] = [.. starts];
        }

        /// <summary>The run's own oracle: the key's final read folds to every row it proposed.</summary>
        private async Task VerifyAsync()
        {
            foreach (var key in new[] { "k", "o" })
            {
                using var read = await Reader.ReadAsync(Encoding.UTF8.GetBytes(key), CancellationToken.None);
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
