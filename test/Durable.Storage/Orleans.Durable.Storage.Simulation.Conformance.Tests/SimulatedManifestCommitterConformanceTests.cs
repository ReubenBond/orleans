using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using CsCheck;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.GroupCommit;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.ManifestChanges;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// The manifest committer's conformance (archive §2.9, §10) under the simulation's scheduler: CsCheck's generated
/// histories, each run in a simulation of its own, whose rounds of concurrent submissions are explored windows
/// (verification.md §4.1). Two committers share one scripted manifest object store; each drain or compaction is derived
/// deterministically from the manifest history (now and then an older epoch, so competing changes reload and supersede
/// one another) and submitted to either; replaces meet scripted outcomes: answered, applied and answered mismatched,
/// failed after applying, failed without applying, or failed and landed later, while reads may also fail.
/// </summary>
/// <remarks>
/// Every answer is judged by the reference reading of the commit rules (<see cref="ManifestReference"/>) against the
/// manifest's history: epoch monotonicity from 1, LSM-I1, LSM-I7, durable answers (conventions.md §13), applied drains
/// retained in the final manifest, and superseded changes' sections absent from the history. The explorer chooses how
/// each round's submissions, the batches they join, the committers' reloads, and the store's replace decisions
/// interleave.
/// </remarks>
public sealed class SimulatedManifestCommitterConformanceTests
{
    private const int MaxSteps = 12;

    /// <summary>The most generated histories the mutant's test explores.</summary>
    private const int MutantHistories = 40;

    /// <summary>The seed of the fixed set of histories whose answers cover every kind of outcome.</summary>
    private const uint CoverageSeed = 1;

    /// <summary>The size of the fixed set of histories.</summary>
    private const int CoverageHistories = 12;

    private enum Fault
    {
        None,
        Answer,
        AppliedMismatch,
        FailApplied,
        FailUnapplied,
        LandLater,
        ReadFails,
    }

    private static Gen<History> Histories { get; } =
        Gen.Select(
                Gen.Select(Gen.Int[0, 99], Gen.Int.NonNegative, Gen.Int[0, 99], Gen.Int[0, 2])
                    .Select(static (kind, choice, staleness, ends) => new ManifestStep(kind, choice, staleness, ends != 0))
                    .Array[1, MaxSteps],
                Gen.Enum<Fault>().Array[MaxSteps],
                Gen.Int[0, 1].Array[MaxSteps],
                Gen.Int.NonNegative)
            .Select(static (steps, faults, committers, seed) => new History(steps, faults, committers, seed));

    /// <summary>
    /// Every history of a fixed set, drawn from one seed, holds under every schedule of its rounds within the bound, and
    /// the set's answers cover every kind of outcome, both drains and compactions, reloads, applied mismatches, delayed
    /// landings, and failed reads.
    /// </summary>
    [Fact]
    public void EveryAnswerHoldsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var pcg = new PCG(CoverageSeed, CoverageSeed);
        var observed = new Coverage();
        for (var index = 0; index < CoverageHistories; index++)
        {
            _ = Check(conformance, Histories.Generate(pcg, null, out _), mutant: false, observed);
        }

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
        foreach (var kind in new[] { "Applied", "Superseded", "unknown" })
        {
            Assert.True(observed.Outcomes.Contains(kind), $"No submission was answered {kind}.");
        }

        Assert.True(observed.Drains > 0, "No drain was submitted.");
        Assert.True(observed.Compactions > 0, "No compaction was submitted.");
        Assert.True(observed.AppliedMismatches > 0, "No replace applied and answered a mismatch.");
        Assert.True(observed.DelayedLandings > 0, "No replace whose outcome was unknown landed later.");
        Assert.True(observed.FailedReads > 0, "No manifest read failed.");
    }

    /// <summary>
    /// Two committers whose object store checks the manifest's version in one step, replaces in a later one, and answers
    /// applied from its check (<see cref="ChecksVersionBeforeReplacingStore"/>) answer a change applied whose replace
    /// another committer's replace superseded between the two steps, so the change never reaches the manifest. The first
    /// generated history with a round of concurrent submissions to both committers catches it, by one preemption; its
    /// default schedule, which runs each submission on until it waits, passes.
    /// </summary>
    [Fact]
    public void ExploredSchedulesCatchAStoreWhichAnswersFromAnEarlierCheck()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Gate);
        var pcg = new PCG(1, 1);
        var histories = 0;
        History? caught = null;
        while (caught is null && histories < MutantHistories)
        {
            histories++;
            var history = Histories.Generate(pcg, null, out _);
            try
            {
                _ = Check(conformance, history, mutant: true, coverage: null);
            }
            catch (ConformanceScheduleException)
            {
                caught = history;
            }
        }

        Assert.NotNull(caught);
        var found = conformance.Explorations[^1];
        Assert.True(found.Schedules > 1, $"The default schedule failed: {found}.");
        Assert.Contains(found.Failure!.Prefix, static choice => choice != 0);

        // The same history under its seeded schedule, one simulation per seed, catches the mutant only on some seeds.
        const int Seeds = 40;
        var seeded = new SimulatedConformance();
        var caughtSeeded = 0;
        for (var seed = 1; seed <= Seeds; seed++)
        {
            try
            {
                _ = Check(seeded, caught with { Seed = seed }, mutant: true, coverage: null);
            }
            catch (ConformanceScheduleException)
            {
                caughtSeeded++;
            }
        }

        TestDiagnostics.WriteLine(FormattableString.Invariant(
            $"The check-then-replace manifest mutant: explored, caught in history {histories} ({caught}) at {found}, by schedule {WindowScheduleExplorer.Format(found.Failure.Prefix)}; seeded, caught on {caughtSeeded} of {Seeds} seeds."));
    }

    private static HashSet<string> Check(SimulatedConformance conformance, History history, bool mutant, Coverage? coverage)
    {
        HashSet<string> kinds = [];
        conformance.RunCaseAsync(
                new ConformanceCase([], history.ToString(), history.Seed),
                async () => kinds = await RunAsync(conformance, history, mutant, coverage))
            .GetAwaiter()
            .GetResult();
        return kinds;
    }

    private static async Task<HashSet<string>> RunAsync(SimulatedConformance conformance, History history, bool mutant, Coverage? coverage)
    {
        var store = new ScriptedManifestObjectStore();
        var world = new ManifestWorld();
        var submissions = new List<ManifestSubmission>();
        var landings = new List<Func<bool>>();
        IObjectStore StoreFor(int writer) => mutant ? new ChecksVersionBeforeReplacingStore(store.Writer(writer)) : store.Writer(writer);
        var views = new[] { StoreFor(0), StoreFor(1) };
        await using var first = new ManifestCommitter(views[0], new ManifestView(views[0]), SimulationEnvironment.Clock, TimeSpan.Zero);
        await using var second = new ManifestCommitter(views[1], new ManifestView(views[1]), SimulationEnvironment.Clock, TimeSpan.Zero);
        var round = new List<Func<Task<ManifestSubmission>>>();
        for (var index = 0; index < history.Steps.Length; index++)
        {
            var step = history.Steps[index];
            var change = world.Derive(step, index);
            var fault = history.Faults[index];
            if (fault == Fault.ReadFails)
            {
                store.FailNextRead();
            }
            else if (fault != Fault.None)
            {
                store.HoldNextReplace().DecideOnArrival(held => Decide(held, fault, landings));
            }

            var committer = history.Committers[index];
            round.Add(() => SubmitAsync(committer == 0 ? first : second, store, change));

            if (step.EndsBatch || index == history.Steps.Length - 1)
            {
                submissions.AddRange(await RunRoundAsync(conformance, round));
                round.Clear();

                // Some replaces whose outcome was unknown land now, after the reads which ended their batches.
                if (step.Choice % 2 == 0)
                {
                    foreach (var land in landings)
                    {
                        if (land())
                        {
                            coverage?.RecordDelayedLanding();
                        }
                    }

                    landings.Clear();
                }

                world.History.Clear();
                world.History.AddRange(Applied(store).Values);
            }
        }

        // Land or discard any remaining delayed writes before judging the history.
        foreach (var land in landings)
        {
            if (land())
            {
                coverage?.RecordDelayedLanding();
            }
        }

        var (values, dispatches) = Applied(store);
        Assert.Empty(Judge(values, dispatches, submissions));
        if (coverage is not null)
        {
            coverage.RecordStore(store);
            foreach (var submission in submissions)
            {
                coverage.RecordSubmission(submission);
            }
        }

        return [.. submissions.Select(static submission => submission.Outcome?.Value?.GetType().Name ?? "unknown")];
    }

    /// <summary>
    /// Runs one round's submissions as a concurrent group, each started as an operation of the simulation's scheduler, in
    /// an explored window when it holds more than one.
    /// </summary>
    private static async Task<List<ManifestSubmission>> RunRoundAsync(SimulatedConformance conformance, List<Func<Task<ManifestSubmission>>> round)
    {
        var concurrent = round.Count > 1;
        if (concurrent)
        {
            conformance.GroupStarting();
        }

        var started = round.Select(submit => conformance.Start(async () => await submit())).ToList();
        var answers = new List<ManifestSubmission>(started.Count);
        foreach (var answer in started)
        {
            answers.Add((ManifestSubmission)await answer);
        }

        if (concurrent)
        {
            conformance.GroupCompleted();
        }

        return answers;
    }

    private static void Decide(ScriptedManifestObjectStore.HeldReplace held, Fault fault, List<Func<bool>> landings)
    {
        switch (fault)
        {
            case Fault.AppliedMismatch:
                held.ApplyAndAnswerMismatch();
                break;
            case Fault.FailApplied:
                held.Fail(apply: true);
                break;
            case Fault.FailUnapplied:
                held.Fail(apply: false);
                break;
            case Fault.LandLater:
                landings.Add(held.FailAndLandLater());
                break;
            case Fault.None:
            case Fault.Answer:
            case Fault.ReadFails:
            default:
                held.Answer();
                break;
        }
    }

    private static (List<ManifestValue> Values, List<long> Dispatches) Applied(ScriptedManifestObjectStore store) =>
        (
            [ManifestValue.Absent, .. store.AppliedWrites.Select(static bytes => Assert.IsType<ManifestValue>(ManifestFormat.Decode(new ReadOnlySequence<byte>(bytes)).Value))],
            [-1, .. store.AppliedDispatches]);

    private static async Task<ManifestSubmission> SubmitAsync(ManifestCommitter committer, ScriptedManifestObjectStore store, ManifestChange change)
    {
        var submittedAt = (ulong)store.AppliedWrites.Count;
        try
        {
            var outcome = await committer.CommitAsync(change);
            return new(change, submittedAt, outcome, AnsweredAt: store.Dispatches);
        }
        catch (Exception failure) when (failure is IOException or TimeoutException or GroupCommitOutcomeUnknownException)
        {
            return new(change, submittedAt, null, AnsweredAt: store.Dispatches);
        }
    }

    /// <summary>
    /// Judges the manifest's history and every answer against the reference reading of archive §2.9's rules, LSM-I1,
    /// LSM-I7, and durable answers (conventions.md §13).
    /// </summary>
    private static List<string> Judge(List<ManifestValue> history, List<long> dispatches, List<ManifestSubmission> submissions)
    {
        List<string> violations = [];
        for (var epoch = 1; epoch < history.Count; epoch++)
        {
            if (history[epoch].Epoch != history[epoch - 1].Epoch + 1)
            {
                violations.Add(Invariant($"A replace wrote epoch {history[epoch].Epoch} after epoch {history[epoch - 1].Epoch}."));
            }
        }

        HashSet<SectionRef> removed = [];
        for (var epoch = 1; epoch < history.Count; epoch++)
        {
            var (previous, value) = (ReferenceManifest.Of(history[epoch - 1]), ReferenceManifest.Of(history[epoch]));
            if (value.DrainedBelow < previous.DrainedBelow
                || previous.Watermarks.Any(entry => entry.Key >= value.DrainedBelow && value.WatermarkOf(entry.Key) < entry.Value.Watermark))
            {
                violations.Add(Invariant($"Epoch {epoch} lowers DrainedBelow or a watermark of epoch {epoch - 1}."));
            }

            if (value.Sections.FirstOrDefault(removed.Contains) is { } returned)
            {
                violations.Add(Invariant($"Epoch {epoch} lists {returned.Object}, which an earlier replace removed."));
            }

            removed.UnionWith(previous.Sections.Where(section => !value.Lists(section)));
        }

        var everListed = history.SelectMany(static value => value.Sections).ToHashSet();
        var last = ReferenceManifest.Of(history[^1]);
        foreach (var submission in submissions)
        {
            if (submission.Outcome is not { } outcome)
            {
                continue;
            }

            var (epoch, applied) = outcome switch
            {
                ManifestCommitOutcome.Applied verdict => (verdict.Epoch, true),
                ManifestCommitOutcome.Superseded verdict => (verdict.Epoch, false),
                null => throw new UnreachableException(),
            };

            if (epoch < submission.SubmittedAt)
            {
                violations.Add(Invariant($"{submission}: answered from epoch {epoch}, older than epoch {submission.SubmittedAt}, which applied before submission."));
                continue;
            }

            if (epoch >= (ulong)history.Count)
            {
                violations.Add(Invariant($"{submission}: answered from epoch {epoch}, which no replace wrote."));
                continue;
            }

            if (dispatches[(int)epoch] >= submission.AnsweredAt)
            {
                violations.Add(Invariant($"{submission}: answered from epoch {epoch} before its replace was dispatched."));
            }

            var value = ReferenceManifest.Of(history[(int)epoch]);
            var goal = ManifestReference.Goal(submission.Change, value);
            if (applied)
            {
                if (!goal)
                {
                    violations.Add(Invariant($"{submission}: the manifest at epoch {epoch} lacks the change's goal."));
                }
                else if (submission.Change.Value is ManifestChange.Drain && !ManifestReference.Goal(submission.Change, last))
                {
                    violations.Add(Invariant($"{submission}: the last manifest lost the applied drain's goal."));
                }
            }
            else
            {
                if (goal || ManifestReference.Rule(submission.Change, value))
                {
                    violations.Add(Invariant($"{submission}: the manifest at epoch {epoch} holds the change's goal or its rule."));
                }
                else if (Adds(submission.Change).Any(everListed.Contains))
                {
                    violations.Add(Invariant($"{submission}: the Superseded change's sections entered the manifest's history."));
                }
            }
        }

        return violations;
    }

    private static IEnumerable<SectionRef> Adds(ManifestChange change) => change switch
    {
        ManifestChange.Drain drain => drain.Sections,
        ManifestChange.Compaction compaction => compaction.Outputs,
        null => throw new UnreachableException(),
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One generated step: kind (compaction vs drain), deterministic choice index, staleness (how often to derive from
    /// an earlier epoch of the history), and whether the step ends its concurrent round.
    /// </summary>
    private sealed record ManifestStep(int Kind, int Choice, int Staleness, bool EndsBatch);

    /// <summary>
    /// A generated history: its steps, the fault each step's replace or read meets, the committer each submits to, and the
    /// simulation's seed.
    /// </summary>
    private sealed record History(ManifestStep[] Steps, Fault[] Faults, int[] Committers, int Seed)
    {
        public override string ToString() =>
            Invariant($"{Steps.Length} steps, faults [{string.Join(", ", Faults.Take(Steps.Length))}], seed {Seed}");
    }

    /// <summary>
    /// Derives deterministic drains and compactions from the manifest history, with section names fixed by step index so
    /// re-executions of an explored window produce identical encodings and choice points.
    /// </summary>
    private sealed class ManifestWorld
    {
        public List<ManifestValue> History { get; } = [ManifestValue.Absent];

        public ManifestChange Derive(ManifestStep step, int stepIndex)
        {
            var basis = History.Count > 1 && step.Staleness < 35
                ? History[step.Choice % History.Count]
                : History[^1];

            if (basis.Sections.Length > 0 && step.Kind < 30)
            {
                var inputCount = 1 + step.Choice % Math.Min(basis.Sections.Length, 2);
                var start = step.Choice / 2 % basis.Sections.Length;
                List<SectionRef> inputs = [];
                for (var offset = 0; offset < inputCount; offset++)
                {
                    var candidate = basis.Sections[(start + offset) % basis.Sections.Length];
                    if (!inputs.Contains(candidate))
                    {
                        inputs.Add(candidate);
                    }
                }

                var outputCount = step.Choice % 3;
                SectionRef[] outputs =
                [
                    .. Enumerable.Range(0, outputCount)
                        .Select(slot => Section(Invariant($"c/1/s{stepIndex:x4}_{slot}"))),
                ];
                return Compaction([.. inputs], outputs);
            }

            var ledger = basis.DrainedBelow.Value + (ulong)(step.Choice % 3);
            while (basis.IsComplete(Ledger(ledger)))
            {
                ledger++;
            }

            var from = basis.RecordedWatermark(Ledger(ledger)).Value;
            var tail = (long)(ledger % 5) * 2;
            var below = Math.Min(from + 1 + step.Choice % 2, tail);
            SectionRef[] sections = below > from
                ? [Section(Invariant($"d/{ledger}/{from}/s{stepIndex:x4}"))]
                : [];
            var complete = below == tail && (below == from || step.Choice % 2 == 0);
            return Drain(ledger, from, below, complete, sections);
        }
    }

    private sealed record ManifestSubmission(ManifestChange Change, ulong SubmittedAt, ManifestCommitOutcome? Outcome, long AnsweredAt)
    {
        public override string ToString() =>
            Invariant($"{Change.Value} submitted after epoch {SubmittedAt}: {(Outcome is { } outcome ? outcome.Value : "unknown")}");
    }

    private sealed class Coverage
    {
        public HashSet<string> Outcomes { get; } = [];

        public int Drains { get; private set; }

        public int Compactions { get; private set; }

        public int AppliedMismatches { get; private set; }

        public int DelayedLandings { get; private set; }

        public int FailedReads { get; private set; }

        public void RecordDelayedLanding() => DelayedLandings++;

        public void RecordStore(ScriptedManifestObjectStore store)
        {
            AppliedMismatches += store.AppliedMismatches;
            FailedReads += store.FailedReads;
        }

        public void RecordSubmission(ManifestSubmission submission)
        {
            _ = Outcomes.Add(submission.Outcome?.Value?.GetType().Name ?? "unknown");
            if (submission.Change.Value is ManifestChange.Drain)
            {
                Drains++;
            }
            else
            {
                Compactions++;
            }
        }
    }

    /// <summary>
    /// A scripted manifest-only <see cref="IObjectStore"/> mirroring <see cref="ScriptedConditionalRegister"/>: held
    /// replaces take their decision on arrival and apply it a scheduler step later via <see cref="SimulationTasks.Yield"/>,
    /// while reads can be scripted to fail with <see cref="IOException"/>.
    /// </summary>
    private sealed class ScriptedManifestObjectStore : IObjectStore
    {
        private readonly ArcBufferPageAccounting? _accounting = ArcBufferPageAccounting.Current;
        private readonly Lock _lock = new();
        private readonly Queue<HeldReplace> _held = [];
        private readonly List<byte[]> _applied = [];
        private readonly List<long> _appliedDispatches = [];
        private byte[]? _value;
        private long _dispatches;
        private int _readsToFail;

        public IReadOnlyList<byte[]> AppliedWrites
        {
            get
            {
                lock (_lock)
                {
                    return [.. _applied];
                }
            }
        }

        public IReadOnlyList<long> AppliedDispatches
        {
            get
            {
                lock (_lock)
                {
                    return [.. _appliedDispatches];
                }
            }
        }

        public long Dispatches
        {
            get
            {
                lock (_lock)
                {
                    return _dispatches;
                }
            }
        }

        public int AppliedMismatches { get; private set; }

        public int FailedReads { get; private set; }

        public ObjectVersion? Version
        {
            get
            {
                lock (_lock)
                {
                    return _value is null ? null : VersionOf(_applied.Count);
                }
            }
        }

        public IObjectStore Writer(int writer) => new WriterView(this, writer);

        public void FailNextRead()
        {
            lock (_lock)
            {
                _readsToFail++;
            }
        }

        public HeldReplace HoldNextReplace()
        {
            var held = new HeldReplace(this);
            lock (_lock)
            {
                _held.Enqueue(held);
            }

            return held;
        }

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            if (name != ObjectName.Manifest)
            {
                throw new NotSupportedException($"Unexpected object read: {name.Value}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[]? value;
            int count;
            lock (_lock)
            {
                if (_readsToFail > 0)
                {
                    _readsToFail--;
                    FailedReads++;
                    throw new IOException("Scripted manifest read failure.");
                }

                value = _value;
                count = _applied.Count;
            }

            return ValueTask.FromResult<VersionedObject?>(value is null ? null : new VersionedObject(Rent(value), VersionOf(count)));
        }

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            ReplaceAsync(name, expectedVersion, content, crc32c, writer: 0, cancellationToken);

        private async ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, int writer, CancellationToken cancellationToken)
        {
            if (name != ObjectName.Manifest)
            {
                throw new NotSupportedException($"Unexpected object replace: {name.Value}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var copy = content.ToArray();
            if (crc32c is uint expectedCrc)
            {
                var actualCrc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, copy);
                if (actualCrc != expectedCrc)
                {
                    throw ObjectContentChangedException.For(name);
                }
            }

            HeldReplace? held;
            long dispatch;
            lock (_lock)
            {
                dispatch = _dispatches++;
                _ = _held.TryDequeue(out held);
            }

            if (held is null)
            {
                return Answer(expectedVersion, copy, dispatch, writer);
            }

            var decide = held.Arrive(expectedVersion, copy, dispatch, writer);
            await SimulationTasks.Yield();
            return await (await decide)();
        }

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static ObjectVersion VersionOf(int applied) => new(applied.ToString(CultureInfo.InvariantCulture));

        private ObjectReplaceOutcome Answer(ObjectVersion? expectedVersion, byte[] value, long dispatch, int writer) =>
            TryApply(expectedVersion, value, dispatch, writer) ? new ObjectReplaceOutcome.Applied(Version!) : ObjectReplaceOutcome.Mismatch.Instance;

        private bool TryApply(ObjectVersion? expectedVersion, byte[] value, long dispatch, int _)
        {
            lock (_lock)
            {
                if (expectedVersion is null ? _value is not null : _value is null || expectedVersion != VersionOf(_applied.Count))
                {
                    return false;
                }

                _value = value;
                _applied.Add(value);
                _appliedDispatches.Add(dispatch);
                return true;
            }
        }

        private ArcBuffer Rent(byte[] value)
        {
            if (value.Length == 0)
            {
                return default;
            }

            var previous = ArcBufferPageAccounting.Current;
            ArcBufferPageAccounting.Current = _accounting;
            try
            {
                using var writer = new ArcBufferWriter(value.Length);
                writer.Write(value);
                return writer.ConsumeSlice(value.Length);
            }
            finally
            {
                ArcBufferPageAccounting.Current = previous;
            }
        }

        internal sealed class HeldReplace(ScriptedManifestObjectStore store)
        {
            private readonly TaskCompletionSource<Func<ValueTask<ObjectReplaceOutcome>>> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Action<HeldReplace>? _onArrival;
            private ObjectVersion? _expected;
            private byte[]? _value;
            private long _dispatch;
            private int _writer;

            public void DecideOnArrival(Action<HeldReplace> decide) => _onArrival = decide;

            internal Task<Func<ValueTask<ObjectReplaceOutcome>>> Arrive(ObjectVersion? expected, byte[] value, long dispatch, int writer)
            {
                _expected = expected;
                _value = value;
                _dispatch = dispatch;
                _writer = writer;
                _onArrival?.Invoke(this);
                return _decision.Task;
            }

            public void Answer() =>
                _decision.SetResult(() => ValueTask.FromResult(store.Answer(_expected, _value!, _dispatch, _writer)));

            public void ApplyAndAnswerMismatch() =>
                _decision.SetResult(() =>
                {
                    if (store.TryApply(_expected, _value!, _dispatch, _writer))
                    {
                        store.AppliedMismatches++;
                    }

                    return ValueTask.FromResult<ObjectReplaceOutcome>(ObjectReplaceOutcome.Mismatch.Instance);
                });

            public void Fail(bool apply) =>
                _decision.SetResult(() =>
                {
                    if (apply)
                    {
                        _ = store.TryApply(_expected, _value!, _dispatch, _writer);
                    }

                    return ValueTask.FromException<ObjectReplaceOutcome>(new IOException("Scripted manifest replace failure."));
                });

            public Func<bool> FailAndLandLater()
            {
                _decision.SetResult(() => ValueTask.FromException<ObjectReplaceOutcome>(new IOException("Scripted delayed manifest replace failure.")));
                var landed = false;
                return () =>
                {
                    if (landed || _value is null)
                    {
                        return false;
                    }

                    landed = true;
                    return store.TryApply(_expected, _value, _dispatch, _writer);
                };
            }
        }

        private sealed class WriterView(ScriptedManifestObjectStore store, int writer) : IObjectStore
        {
            public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
                store.ReadVersionedAsync(name, cancellationToken);

            public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
                store.ReplaceAsync(name, expectedVersion, content, crc32c, writer, cancellationToken);

            public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
                store.CreateAsync(name, content, crc32c, cancellationToken);

            public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
                store.ReadAsync(name, offset, length, cancellationToken);

            public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
                store.GetPropertiesAsync(name, cancellationToken);

            public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
                store.DeleteAsync(name, cancellationToken);

            public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) =>
                store.ListAsync(prefix, startAfter, cancellationToken);
        }
    }

    /// <summary>
    /// A race mutant of the manifest store: a replace checks the manifest's version in one step, yields a scheduler step,
    /// then replaces and answers applied from its earlier check, whatever the underlying replace answered, with a version
    /// of its own. Another committer's replace between the two steps supersedes it, and its change never reaches the
    /// manifest.
    /// </summary>
    private sealed class ChecksVersionBeforeReplacingStore(IObjectStore inner) : IObjectStore
    {
        private int _unapplied;

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public async ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            bool matches;
            var current = await inner.ReadVersionedAsync(name, cancellationToken);
            if (current is VersionedObject loaded)
            {
                using (loaded.Content)
                {
                    matches = expectedVersion is not null && loaded.Version == expectedVersion;
                }
            }
            else
            {
                matches = expectedVersion is null;
            }

            if (!matches)
            {
                return ObjectReplaceOutcome.Mismatch.Instance;
            }

            var bytes = content.ToArray();
            await SimulationTasks.Yield();
            using var copy = ArcBuffer.Wrap(bytes);
            var written = await inner.ReplaceAsync(name, expectedVersion, copy, crc32c, cancellationToken);
            return written.Value is ObjectReplaceOutcome.Mismatch
                ? new ObjectReplaceOutcome.Applied(new ObjectVersion(Invariant($"unapplied-{++_unapplied}")))
                : written;
        }

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);
    }
}
