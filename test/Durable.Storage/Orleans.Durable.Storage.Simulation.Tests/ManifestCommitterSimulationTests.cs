using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.ManifestChanges;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// The manifest committer's history oracle (archive §2.9, §5, §10), in deterministic simulation: two committers share
/// one manifest in a simulated object store which fails requests, leaves outcomes unknown, lands writes late, and answers
/// a retried write's own landing as <see cref="ObjectReplaceOutcome.Mismatch"/>. Workers commit changes derived from
/// manifest values they read, and every answer is judged, by the reference reading of the commit rules, against the
/// manifest's history: the value each applied replace wrote.
/// </summary>
/// <remarks>
/// An answer names an epoch the history holds, at or after every replace which applied before the change was
/// submitted, whose value holds the change's goal (<c>Applied</c>) or lacks it and fails its rule (<c>Superseded</c>);
/// a <c>Superseded</c> change's sections never enter the history; an applied drain's goal holds in the last value; and
/// the history holds one value per epoch from 1, keeps LSM-I1, and never lists a removed section again (LSM-I7). A seed
/// replays its schedule, so each failing seed reproduces. The driver's teardown checks that every page the run rented
/// returned, the committers' encodings and the manifest reads among them (BUF-I1, BUF-I2).
/// </remarks>
public sealed class ManifestCommitterSimulationTests
{
    private const int Workers = 4;
    private const int ChangesPerWorker = 25;

    /// <summary>
    /// Gets the seed corpus.
    /// </summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    // Every change runs three seeds and exploration runs more through CLOG_SIM_SEEDS
    // (docs/spec/verification.md §7.4, §12).
    private const int FirstSeed = 1;
    private const int SeedCount = 3;

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TwoCommittersAnswerOnlyFromTheHistory(int seed) =>
        SeededSimulation.Run(seed, static async environment => Assert.Empty(await SimulateAsync(environment, Committers.Real)));

    /// <summary>
    /// The simulation replays from its seed and its artifact, and each fault it injects fires.
    /// </summary>
    [Fact]
    public void TheCommitterSimulationReplaysAndEachFaultFires() =>
        SeededSimulation.RunAndReplay(22, static async environment =>
        {
            Assert.Empty(await SimulateAsync(environment, Committers.Real, store =>
            {
                Assert.True(store.UnknownOutcomes > 0, "No write's outcome was unknown.");
                Assert.True(store.DelayedLandings > 0, "No write landed late.");
                Assert.True(store.RetriedAnswers > 0, "No retried write met its own landing.");
            }));
        });

    /// <summary>
    /// A committer which answers from the value it cached, without reading the manifest after the change was submitted,
    /// breaks the oracle on every seed, and the first seed's violations replay.
    /// </summary>
    [Fact]
    public void TheOracleFlagsACommitterWhichAnswersFromAValueItCachedOnEverySeed()
    {
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, SeedCount))
        {
            List<List<string>> runs = [];
            async Task ScenarioAsync(SimulationEnvironment environment) => runs.Add(await SimulateAsync(environment, Committers.CachedAnswer));
            _ = seed == FirstSeed ? SeededSimulation.RunAndReplay(seed, ScenarioAsync) : SeededSimulation.Run(seed, ScenarioAsync);
            Assert.True(runs[0].Count > 0, string.Create(CultureInfo.InvariantCulture, $"Seed {seed}: the oracle accepted every answer of the cached-answer committer."));
            Assert.All(runs, run => Assert.Equal(runs[0], run));
        }
    }

    private enum Committers
    {
        Real,
        CachedAnswer,
    }

    /// <summary>
    /// Runs the workers against two committers of the given kind, and returns the oracle's violations.
    /// </summary>
    private static async Task<List<string>> SimulateAsync(SimulationEnvironment environment, Committers kind, Action<SimulatedObjectStore>? check = null)
    {
        await using var store = new SimulatedObjectStore(environment, "archive")
        {
            UnknownOutcomeRate = 0.2,
            RetriedAnswerRate = 0.1,
            ReadFailureRate = 0.05,
        };
        List<Answer>[] answers;
        if (kind == Committers.Real)
        {
            await using var first = new ManifestCommitter(store, new ManifestView(store), SimulationEnvironment.Clock, TimeSpan.Zero);
            await using var second = new ManifestCommitter(store, new ManifestView(store), SimulationEnvironment.Clock, TimeSpan.Zero);
            answers = await WorkAllAsync(environment, store, [change => first.CommitAsync(change), change => second.CommitAsync(change)]);
        }
        else
        {
            answers = await WorkAllAsync(environment, store, [new CachedAnswerCommitter(store).CommitAsync, new CachedAnswerCommitter(store).CommitAsync]);
        }

        // Every delayed write lands, or never will, before the history is judged.
        await Task.Delay(store.MaxLandingDelay + TimeSpan.FromMilliseconds(1), environment.TimeProvider);
        check?.Invoke(store);
        return Judge(store, [.. answers.SelectMany(static answers => answers)]);
    }

    /// <summary>Runs every worker against <paramref name="committers"/>, and returns each worker's answers.</summary>
    private static Task<List<Answer>[]> WorkAllAsync(SimulationEnvironment environment, SimulatedObjectStore store, Func<ManifestChange, Task<ManifestCommitOutcome>>[] committers) =>
        SimulationTasks.WhenAll(Enumerable.Range(0, Workers).Select(worker =>
            WorkAsync(environment.CreateRandom(string.Create(CultureInfo.InvariantCulture, $"worker-{worker}")), store, committers)));

    /// <summary>
    /// Commits changes derived from manifest values the worker read, now and then an older one, each through a committer
    /// the worker chooses, and records each answer with the last epoch applied before its submission.
    /// </summary>
    private static async Task<List<Answer>> WorkAsync(
        Random random,
        SimulatedObjectStore store,
        Func<ManifestChange, Task<ManifestCommitOutcome>>[] committers)
    {
        List<Answer> answers = [];
        ManifestValue? previous = null;
        while (answers.Count < ChangesPerWorker)
        {
            ManifestValue read;
            try
            {
                read = (await new ManifestView(store).ReadAsync()).Value;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                continue;
            }

            var change = ManifestWorkload.Derive(previous is not null && random.Next(4) == 0 ? previous : read, random);
            previous = read;
            var bound = (ulong)store.AppliedWritesOf(ObjectName.Manifest).Count;
            try
            {
                answers.Add(new Answer(change, bound, await committers[random.Next(committers.Length)](change)));
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                answers.Add(new Answer(change, bound, null));
            }
        }

        return answers;
    }

    /// <summary>
    /// Returns every violation of the history and of the answers, judged by the reference reading of the rules.
    /// </summary>
    private static List<string> Judge(SimulatedObjectStore store, List<Answer> answers)
    {
        List<string> violations = [];
        List<ManifestValue> history = [ManifestValue.Absent];
        foreach (var bytes in store.AppliedWritesOf(ObjectName.Manifest))
        {
            if (ManifestFormat.Decode(new ReadOnlySequence<byte>(bytes)).Value is not ManifestValue value)
            {
                violations.Add("A replace wrote a manifest the format rejects.");
                return violations;
            }

            if (value.Epoch != history[^1].Epoch + 1)
            {
                violations.Add(Invariant($"A replace wrote epoch {value.Epoch} after epoch {history[^1].Epoch}."));
            }

            history.Add(value);
        }

        violations.AddRange(HistoryViolations(history));
        var everListed = history.SelectMany(static value => value.Sections).ToHashSet();
        var last = ReferenceManifest.Of(history[^1]);
        foreach (var answer in answers)
        {
            if (answer.Outcome is { } outcome && Verdict(answer, outcome, history, everListed, last) is { } violation)
            {
                violations.Add(violation);
            }
        }

        return violations;
    }

    private static string? Verdict(Answer answer, ManifestCommitOutcome outcome, List<ManifestValue> history, HashSet<SectionRef> everListed, ReferenceManifest last)
    {
        var (epoch, applied) = outcome switch
        {
            ManifestCommitOutcome.Applied verdict => (verdict.Epoch, true),
            ManifestCommitOutcome.Superseded verdict => (verdict.Epoch, false),
            null => throw new UnreachableException(),
        };

        if (epoch < answer.Bound)
        {
            return Invariant($"{answer}: answered from epoch {epoch}, older than epoch {answer.Bound}, which applied before the change was submitted.");
        }

        if (epoch >= (ulong)history.Count)
        {
            return Invariant($"{answer}: answered from epoch {epoch}, which no replace wrote.");
        }

        var value = ReferenceManifest.Of(history[(int)epoch]);
        var goal = ManifestReference.Goal(answer.Change, value);
        if (applied)
        {
            if (!goal)
            {
                return Invariant($"{answer}: the manifest at epoch {epoch} lacks the change's goal.");
            }

            return answer.Change.Value is ManifestChange.Drain && !ManifestReference.Goal(answer.Change, last)
                ? Invariant($"{answer}: the last manifest lost the applied drain's goal.")
                : null;
        }

        if (goal || ManifestReference.Rule(answer.Change, value))
        {
            return Invariant($"{answer}: the manifest at epoch {epoch} holds the change's goal or its rule.");
        }

        return Adds(answer.Change).Any(everListed.Contains)
            ? Invariant($"{answer}: the Superseded change's sections entered the manifest's history.")
            : null;
    }

    private static IEnumerable<SectionRef> Adds(ManifestChange change) => change switch
    {
        ManifestChange.Drain drain => drain.Sections,
        ManifestChange.Compaction compaction => compaction.Outputs,
        null => throw new UnreachableException(),
    };

    /// <summary>
    /// Returns the history's own violations: a watermark or <c>DrainedBelow</c> which decreased (LSM-I1), or a section
    /// which returned after a replace removed it (LSM-I7).
    /// </summary>
    private static IEnumerable<string> HistoryViolations(List<ManifestValue> history)
    {
        HashSet<SectionRef> removed = [];
        for (var epoch = 1; epoch < history.Count; epoch++)
        {
            var (previous, value) = (ReferenceManifest.Of(history[epoch - 1]), ReferenceManifest.Of(history[epoch]));
            if (value.DrainedBelow < previous.DrainedBelow
                || previous.Watermarks.Any(entry => entry.Key >= value.DrainedBelow && value.WatermarkOf(entry.Key) < entry.Value.Watermark))
            {
                yield return Invariant($"Epoch {epoch} lowers DrainedBelow or a watermark of epoch {epoch - 1}.");
            }

            if (value.Sections.FirstOrDefault(removed.Contains) is { } returned)
            {
                yield return Invariant($"Epoch {epoch} lists {returned.Object}, which an earlier replace removed.");
            }

            removed.UnionWith(previous.Sections.Where(section => !value.Lists(section)));
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A change, the last epoch applied before it was submitted, and its answer, or <see langword="null"/> when its
    /// outcome is unknown.
    /// </summary>
    private sealed record Answer(ManifestChange Change, ulong Bound, ManifestCommitOutcome? Outcome)
    {
        public override string ToString() =>
            Invariant($"{Change.Value} submitted after epoch {Bound}: {(Outcome is { } outcome ? outcome.Value : "unknown")}");
    }

    /// <summary>
    /// A mutant committer which answers a change from the value it last read or wrote, without reading the manifest
    /// after the change was submitted.
    /// </summary>
    private sealed class CachedAnswerCommitter(IObjectStore store)
    {
        private ManifestSnapshot? _cached;

        public async Task<ManifestCommitOutcome> CommitAsync(ManifestChange change)
        {
            var basis = _cached ??= await new ManifestView(store).ReadAsync();
            while (true)
            {
                if (ManifestCommitRules.GoalHolds(basis.Value, change))
                {
                    return new ManifestCommitOutcome.Applied(basis.Value.Epoch);
                }

                if (!ManifestCommitRules.RuleHolds(basis.Value, change))
                {
                    return new ManifestCommitOutcome.Superseded(basis.Value.Epoch);
                }

                var next = Next(basis.Value, change);
                ObjectReplaceOutcome outcome;
                using (var encoded = ManifestFormat.Encode(next))
                {
                    try
                    {
                        outcome = await store.ReplaceAsync(ObjectName.Manifest, basis.Version, encoded.Content, encoded.Crc32C);
                    }
                    catch (Exception exception) when (exception is IOException or TimeoutException)
                    {
                        _cached = null;
                        throw;
                    }
                }

                if (outcome.Value is ObjectReplaceOutcome.Applied written)
                {
                    _cached = new ManifestSnapshot(next, written.Version);
                    return new ManifestCommitOutcome.Applied(next.Epoch);
                }

                basis = _cached = await new ManifestView(store).ReadAsync();
            }
        }
    }
}
