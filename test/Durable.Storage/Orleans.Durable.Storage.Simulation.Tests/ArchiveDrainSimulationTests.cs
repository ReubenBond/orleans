using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
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
/// Drains, compactions, drops, transitions, sweeps, and reads interleaved over the LSM in deterministic simulation
/// (archive §2.3, §2.4, §2.5, §2.6, §2.7, §5, §10). One node's writer process proposes rows of four keys, half its
/// <c>Delete</c>s empty expiries, and now and then replaces or splits a key's ledger, over a ledger map register which
/// leaves outcomes unknown, its writer ending small segments by size, keys, and age; meanwhile two drainer processes, each
/// with its own lagging map view, drain the ledgers they list, a whole closed segment at a time, one of them splitting a
/// segment's rows across objects past a small object bound, over a simulated object store which fails requests, leaves
/// outcomes unknown, and lands writes late, committing through two committers whose views lag each other, with answers
/// lost and workers restarted; two
/// compactor processes, each reading the manifest and its own lagging map view, race to push or merge in place the
/// overfull partitions, committing through either committer; a dropper drops complete ledgers, and the node's
/// reconciliation drops their replicas; two sweeper processes sweep alongside, restarting now and then, which loses their
/// marks, and after each of the first two rounds one sweeps twice while a drain's commit is held; and two reader
/// processes, the writer's and another, read histories through the history reader and read the store directly. Replica
/// reads answer backpressure at random. A final phase, with every fault off, completes and drops every closed ledger, and
/// sweeps until no garbage is left.
/// </summary>
/// <remarks>
/// <para>
/// The oracles: every history read equals the reference rows from the base over its <c>Through</c>, misses no row of a
/// ledger closed before it began nor, linearizable, a row answered durable before it began, and holds none proposed after
/// it ended (HI-I1 over LSM-I2 and LSM-I4); every drain takes one whole closed segment, from a segment start to the next
/// or to <c>T</c>, every watermark the manifest's history records is a segment start, and no drain is superseded, since
/// two drains from one watermark take the same segment (LSM-I2); every store answer's prefix folds, from its base in the
/// store, the rows of the ledgers its map dropped, or those rows without a leading empty <c>Delete</c> which a compaction
/// dropped with every older row of its key (LSM-I5), or the subset still in its view when a closed, complete ledger its
/// map still lists superseded rows of dropped ledgers (archive-lsm.md §8.6); every drop is submitted while the durable
/// manifest shows its ledger complete (LSM-I3); the
/// manifest's history keeps its watermarks, completions, and <c>DrainedBelow</c> (LSM-I1); and every delete removes an
/// object which the durable manifest neither references nor could, by the reference reading of the change behind it, and
/// which no later manifest references (LSM-I6), and no write of its name lands after it (LSM-I7).
/// </para>
/// <para>
/// A <c>Stale</c> answer comes from a map older than the store's view, or from a view which still lists a compaction's
/// input which a sweep deleted.
/// </para>
/// </remarks>
public sealed class ArchiveDrainSimulationTests
{
    // Every change runs one seed, which takes every counted path, its drainers at both grid steps, and exploration runs
    // more through DURABLE_STORAGE_SIM_SEEDS (test/Durable.Storage/docs/spec/verification.md §7.4, §12).
    private const int FirstSeed = 1;
    private const int SeedCount = 1;

    // Each mutant runs on the corpus's first seed, and exploration runs it on every seed it explores.
    private const int MutantSeedCount = 1;
    private const int Rounds = 4;
    private const int ProposalsPerRound = 8;
    private const int Passes = 3;
    private const int ReadsPerRound = 6;
    private const int Attempts = 20;

    // The most drains of one ledger a pass takes, each a whole segment, of which the small segments make many.
    private const int MaxDrainsPerLedger = 64;

    // The most segments a scripted drain to a segment start takes, past every segment a ledger here holds.
    private const int MaxSegmentsDrained = 1000;

    // The drains' section size, past a small segment's rows, where the compactions' small one would split every segment
    // across objects: the second drainer drains at the compactions' size, so its drains split them.
    private const long DrainSectionBytes = 4096;

    // The chance that a random sweep's listing fails partway, or one of its deletes fails before it reaches the store.
    private const double SweepFaultRate = 0.1;

    // The chance that the writer ends a key's open segment with a marker after a proposal, as a failure-triggered drain does.
    private const double MarkerRate = 0.3;
    private const string ReadOracle = "read oracle: ";
    private const string StoreOracle = "store oracle: ";
    private const string DropCheck = "LSM-I3: ";
    private const string ManifestCheck = "LSM-I1: ";
    private const string WholeSegmentCheck = "LSM-I2: ";
    private const string SweepCheck = "LSM-I6: ";
    private const string NameCheck = "LSM-I7: ";
    private const string GraceCheck = "sweep grace: ";
    private const string InputsCheck = "LSM-I5: ";
    private const string CorruptionCheck = "sweep corruption: ";
    private const string GarbageCheck = "garbage: ";
    private static readonly string[] Keys = ["k0", "k1", "k2", "k3"];

    // The theory and the corpus check share each seed's run, so the corpus runs once.
    private static readonly ConcurrentDictionary<int, Lazy<Counts>> Runs = new();

    private enum Mutant
    {
        None,

        /// <summary>The store reads with its caller's previous map value, so its view need not cover the caller's map.</summary>
        UncoveredDrop,

        /// <summary>A drainer whose completing drain's commit failed drops the ledger, as if the unknown outcome applied.</summary>
        DropOnUnknown,

        /// <summary>
        /// <c>uncovered-expiry</c>: in the uncovered expiry schedule, a store reads with a map value from before its caller's
        /// map dropped the ledger of an empty Delete which a compaction then dropped, so its view need not hold the Delete.
        /// </summary>
        UncoveredExpiry,

        /// <summary>
        /// <c>drop-ignoring-live</c>: a compactor whose production guard omits the check that lower live ledgers are
        /// complete, so undrained older rows in live ledgers do not keep an empty Delete.
        /// </summary>
        DropIgnoringLive,

        /// <summary>
        /// <c>drop-ignoring-sections</c>: a compactor whose production guard omits the check of outside sections, so older
        /// rows in sections outside the compaction do not keep an empty Delete.
        /// </summary>
        DropIgnoringSections,

        /// <summary>
        /// <c>drop-trusting-unlisted</c>: a compactor which takes a lower ledger the map no longer lists as excluding the
        /// key, by judging against a manifest value showing every such ledger complete.
        /// </summary>
        DropTrustingUnlisted,

        /// <summary><c>never-drop</c>: a compactor which judges against an empty map, so no empty Delete ever drops.</summary>
        NeverDrop,

        /// <summary>
        /// <c>sweep-drain</c>: the sweeper sweeps every unreferenced drain output, whatever its reference condition, so it
        /// deletes a drain upload whose commit is in flight. Its runs take only the held-commit schedule, each of whose
        /// in-flight rounds repeats until its held commit applies.
        /// </summary>
        SweepDrain,

        /// <summary>
        /// <c>sweep-compaction</c>: the sweeper sweeps every unreferenced compaction output, whatever its reference
        /// condition, so it deletes a compaction upload whose commit is in flight.
        /// </summary>
        SweepCompaction,

        /// <summary>
        /// <c>stale-drain</c>: the manifest committer skips the watermark check for drains of an incomplete ledger, so a
        /// stale drain lowers the ledger's recorded watermark (LSM-I1).
        /// </summary>
        StaleDrain,

        /// <summary>
        /// <c>ignore-missing</c>: the archive store skips any listed section whose object is gone rather than answering
        /// <c>Stale</c>, so a read across a swept section misses its rows (LSM-I4, LSM-I5).
        /// </summary>
        IgnoreMissing,

        /// <summary>
        /// <c>hold-by-watermark</c>: the archive store folds every drained row in its view and reports coverage by the
        /// view's watermarks rather than the map's dropped ledgers, which out-of-order draining breaks (LSM-I4, LSM-I5).
        /// </summary>
        HoldByWatermark,

        /// <summary>
        /// <c>supersede-by-open</c>: the compactor lets a <c>Replace</c> or <c>Delete</c> of an incomplete ledger
        /// supersede older rows, dropping a dropped ledger's row while the open ledger is still listed (LSM-I4, LSM-I5).
        /// </summary>
        SupersedeByOpen,

        /// <summary>
        /// <c>no-predates-check</c>: the archive store omits the check that the map's open ledger is not already complete
        /// in the view, reading a compacted section that dropped older rows with a map that still lists the superseding
        /// ledger open (LSM-I4, LSM-I5).
        /// </summary>
        NoPredatesCheck,

        /// <summary>
        /// <c>accepts-changed-content</c>: an object store which keeps a create whose content changed after its writer
        /// verified it, though the content no longer matches the writer's CRC-32C.
        /// </summary>
        AcceptsChangedContent,

        /// <summary>
        /// <c>lsm-mid-segment-watermark</c>: drainers whose workers drain to their bound, the contiguous tail or short of
        /// it, inside a segment, as <c>Archive.qnt</c>'s mutant does.
        /// </summary>
        MidSegmentWatermark,
    }

    /// <summary>How the object store meets creates whose content changed after their writer verified it.</summary>
    private enum ChangedContent
    {
        /// <summary>No content changes.</summary>
        None,

        /// <summary>Some creates' content changes, and the store refuses them with the answer lost: an unknown outcome.</summary>
        RefusedAnswerLost,

        /// <summary>Some creates' content changes, and the store refuses them with an answer, which stops the writer's process.</summary>
        RefusedAnswered,
    }

    /// <summary>Gets the seed corpus.</summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    [Theory]
    [MemberData(nameof(Seeds))]
    public void InterleavedDrainsDropsTransitionsAndReadsKeepTheArchivesInvariants(int seed) => _ = RunSeed(seed);

    /// <summary>
    /// Across the corpus, every path the scenario counts is taken, by the random rounds or the scripted steps, and each
    /// fault path the random rounds must reach themselves, beside a scripted step which forces it, is taken by them.
    /// </summary>
    [Fact]
    public void TheCorpusExercisesEveryPath()
    {
        var (totals, generated) = (new Dictionary<string, int>(StringComparer.Ordinal), new Dictionary<string, int>(StringComparer.Ordinal));
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, SeedCount))
        {
            var counts = RunSeed(seed);
            foreach (var (path, count) in counts.Paths.Concat(counts.Scripted))
            {
                totals[path] = totals.GetValueOrDefault(path) + count;
            }

            foreach (var (path, count) in counts.Paths)
            {
                generated[path] = generated.GetValueOrDefault(path) + count;
            }
        }

        string[] missing = [.. Paths.All.Where(path => totals.GetValueOrDefault(path) == 0)];
        string[] scriptedOnly = [.. Paths.Generated.Where(path => generated.GetValueOrDefault(path) == 0)];
        Assert.True(missing.Length == 0, $"The corpus never exercised: {string.Join(", ", missing)}. It took: {string.Join(", ", totals.Select(static total => $"{total.Key} {total.Value}"))}.");
        Assert.True(scriptedOnly.Length == 0, $"Only scripted steps exercised: {string.Join(", ", scriptedOnly)}. The random rounds took: {string.Join(", ", generated.Select(static total => $"{total.Key} {total.Value}"))}.");
    }

    /// <summary>
    /// With three replicas a ledger, so a batch is decided once two hold it and a close recovers the decided ledger, every
    /// drain takes whole decided segments, which the oracle takes from the recovered, decided ledgers, and every other
    /// oracle holds.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void ThreeReplicasDrainWholeDecidedSegments(int seed)
    {
        Counts? counts = null;
        _ = SeededSimulation.Run(seed, async environment => counts = await SimulateAsync(environment, Mutant.None, replicas: 3));
        Assert.True(counts!.Violations.Count == 0, Invariant($"Seed {seed}: {string.Join(" | ", counts.Violations)}"));
    }

    /// <summary>
    /// The <c>uncovered-drop</c> mutant, a store which reads with its caller's previous map value, misses the rows of a
    /// ledger its caller's map dropped after the store's view, which the read oracle flags on the mutants' seed.
    /// </summary>
    [Fact]
    public void TheReadOracleFlagsAStoreWhoseViewDoesNotCoverTheCallersMap() => AssertEverySeedFails(Mutant.UncoveredDrop, ReadOracle);

    /// <summary>
    /// The <c>uncovered-expiry</c> mutant, a store which reads with a map value from before its caller's map dropped the
    /// ledger of an empty Delete, answers without the Delete from a view from before it drained, which the store oracle
    /// flags on the mutants' seed though a later compaction dropped the Delete.
    /// </summary>
    [Fact]
    public void TheStoreOracleFlagsAStoreWhoseViewPredatesADroppedExpiry() => AssertEverySeedFails(Mutant.UncoveredExpiry, StoreOracle);

    /// <summary>
    /// The <c>drop-on-unknown</c> mutant, which drops a ledger whose completing drain's commit failed, drops it while the
    /// durable manifest shows it incomplete, which the LSM-I3 check flags on the mutants' seed.
    /// </summary>
    [Fact]
    public void TheDropCheckFlagsADropOnAnUnknownOutcome() => AssertEverySeedFails(Mutant.DropOnUnknown, DropCheck);

    /// <summary>
    /// The <c>sweep-drain</c> mutant, a sweeper which sweeps a drain output merely unreferenced, deletes a drain upload
    /// while its commit is held across two sweeps, and the commit then references the deleted object, which an LSM-I6
    /// delete check flags on the mutants' seed.
    /// </summary>
    [Fact]
    public void TheSweepCheckFlagsASweeperWhichDeletesADrainUploadInFlight() => AssertEverySeedFails(Mutant.SweepDrain, SweepCheck);

    /// <summary>
    /// The <c>sweep-compaction</c> mutant, a sweeper which sweeps a compaction output merely unreferenced, deletes a
    /// compaction upload while its commit is held across two sweeps, and the commit then references the deleted object,
    /// which an LSM-I6 delete check flags on the mutants' seed.
    /// </summary>
    [Fact]
    public void TheSweepCheckFlagsASweeperWhichDeletesACompactionUploadInFlight() => AssertEverySeedFails(Mutant.SweepCompaction, SweepCheck);

    /// <summary>
    /// The <c>stale-drain</c> mutant, a committer which applies a drain of an incomplete ledger without checking its
    /// watermark, lowers the ledger's recorded watermark, which the LSM-I1 manifest check flags on the mutants' seed.
    /// </summary>
    [Fact]
    public void TheManifestCheckFlagsAStaleDrainWhichLowersAWatermark() => AssertEverySeedFails(Mutant.StaleDrain, ManifestCheck);

    /// <summary>
    /// The <c>ignore-missing</c> mutant, a store which skips a swept section rather than answering <c>Stale</c>, misses a
    /// dropped ledger's row after compaction and sweeping remove its section, which the store oracle flags on the mutants'
    /// seed (LSM-I4, LSM-I5).
    /// </summary>
    [Fact]
    public void TheStoreOracleFlagsAStoreWhichIgnoresAMissingSection() => AssertEverySeedFails(Mutant.IgnoreMissing, StoreOracle);

    /// <summary>
    /// The <c>hold-by-watermark</c> mutant, a store which folds every drained row in its view and reports coverage by
    /// watermark rather than dropped ledgers, breaks on out-of-order draining, which the read and store oracles flag on the
    /// mutants' seed (LSM-I4, LSM-I5).
    /// </summary>
    [Fact]
    public void TheOraclesFlagAStoreWhichHoldsRowsByWatermark() => AssertEverySeedFails(Mutant.HoldByWatermark, ReadOracle, StoreOracle);

    /// <summary>
    /// The <c>supersede-by-open</c> mutant, a compactor which lets a <c>Replace</c> of an open ledger supersede a dropped
    /// ledger's row, drops the older row while the open ledger is still listed, which the store oracle flags on the
    /// mutants' seed (LSM-I4, LSM-I5).
    /// </summary>
    [Fact]
    public void TheStoreOracleFlagsACompactorWhichSupersedesByAnOpenLedger() => AssertEverySeedFails(Mutant.SupersedeByOpen, StoreOracle);

    /// <summary>
    /// A dropped ledger's rows compacted behind a <c>Replace</c> or <c>Delete</c> of a closed, complete ledger that the
    /// read's map still lists may be absent (or reduced to sections outside the compaction) in the store's answer
    /// (archive-lsm.md §8.6): the store oracle accepts the compliant answers while the history reads fold to the
    /// superseding ledger's state.
    /// </summary>
    [Fact]
    public void TheStoreOracleAcceptsDroppedRowsSupersededByAClosedCompleteListedLedger()
    {
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, MutantSeedCount))
        {
            Counts? counts = null;
            _ = SeededSimulation.Run(seed, async environment => counts = await SimulateAsync(environment, Mutant.None, supersededByClosedOnly: true));
            Assert.True(counts!.Violations.Count == 0, Invariant($"Seed {seed}: {string.Join(" | ", counts.Violations)}"));
            Assert.True(
                counts.Paths.GetValueOrDefault(Paths.SupersededReads) >= 3,
                Invariant($"Seed {seed}: expected at least 3 superseded store reads (Replace, Delete, and partial), saw {counts.Paths.GetValueOrDefault(Paths.SupersededReads)}."));
        }
    }

    /// <summary>
    /// The <c>no-predates-check</c> mutant, a store which skips the check that the map's open ledger is not already
    /// complete in its view, reads a compacted section with a stale open map and misses the superseded row, which the
    /// store oracle flags on the mutants' seed (LSM-I4, LSM-I5).
    /// </summary>
    [Fact]
    public void TheStoreOracleFlagsAStoreWhichSkipsThePredatesCheck() => AssertEverySeedFails(Mutant.NoPredatesCheck, StoreOracle);

    /// <summary>
    /// Each unsafe drop mutant, <c>drop-ignoring-live</c>, <c>drop-ignoring-sections</c>, and <c>drop-trusting-unlisted</c>,
    /// drops an empty Delete over an older row of its key in the one fixed expiry schedule it runs, which the read or store
    /// oracle flags on the mutants' seed (LSM-I5).
    /// </summary>
    [Theory]
    [InlineData("drop-ignoring-live")]
    [InlineData("drop-ignoring-sections")]
    [InlineData("drop-trusting-unlisted")]
    public void TheOraclesFlagAnUnsafeDropOfAnEmptyDelete(string mutant) =>
        AssertEverySeedFails(
            mutant switch
            {
                "drop-ignoring-live" => Mutant.DropIgnoringLive,
                "drop-ignoring-sections" => Mutant.DropIgnoringSections,
                "drop-trusting-unlisted" => Mutant.DropTrustingUnlisted,
                _ => throw new ArgumentOutOfRangeException(nameof(mutant)),
            },
            ReadOracle,
            StoreOracle);

    /// <summary>
    /// Creates whose content changed after their writer verified it are refused, their answers lost: each drain or
    /// compaction meets an unknown outcome and writes again under a fresh name, no manifest references a refused object,
    /// and every oracle holds.
    /// </summary>
    [Fact]
    public void ACreateWhoseContentChangedIsRefusedAndNoManifestReferencesIt()
    {
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, MutantSeedCount))
        {
            Counts? counts = null;
            _ = SeededSimulation.Run(seed, async environment => counts = await SimulateAsync(environment, Mutant.None, ChangedContent.RefusedAnswerLost));
            Assert.True(counts!.Violations.Count == 0, Invariant($"Seed {seed}: {string.Join(" | ", counts.Violations)}"));
            Assert.True(counts.ChangedContentWrites > 0, Invariant($"Seed {seed}: no create's content changed."));
        }
    }

    /// <summary>
    /// Regression for #402: at seed 5 the changed-content faults failed so many of a lower ledger's drains, each restarting
    /// its drainer half the time from its process's stale manifest view, that the lower ledger never completed, so the
    /// read-across-a-drop schedule's ledger, which drops after it (LG-I10), never dropped. Once its passes under the faults
    /// end, the schedule completes and drops with every fault off, so its ledger drops whatever the faults drew.
    /// </summary>
    [Fact]
    public void TheReadAcrossADropScheduleDropsItsLedgerUnderChangedContent()
    {
        Counts? counts = null;
        _ = SeededSimulation.Run(5, async environment => counts = await SimulateAsync(environment, Mutant.None, ChangedContent.RefusedAnswerLost));
        Assert.True(counts!.Violations.Count == 0, string.Join(" | ", counts.Violations));
    }

    /// <summary>
    /// A writer which hears that the store refused its changed content reports the defect, which stops its process
    /// (archive §9, conventions §14): the run fails with the report of <see cref="ObjectContentChangedException"/> under
    /// the shared defect code.
    /// </summary>
    [Fact]
    public void AWriterWhichHearsItsContentChangedStopsItsProcess()
    {
        var seed = SeededSimulation.CorpusSeeds(FirstSeed, MutantSeedCount).First();
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(seed, static environment => SimulateAsync(environment, Mutant.None, ChangedContent.RefusedAnswered), minimize: false));

        var violation = Assert.IsType<InvariantViolationException>(failure.InnerException);
        Assert.Contains("exit code 79: the defect ObjectContentChanged", violation.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(ObjectContentChangedException).FullName!, violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>accepts-changed-content</c> mutant, a store which keeps a create whose content no longer matches its
    /// writer's CRC-32C, lets a manifest reference the changed object, which the manifest check flags on the mutants' seed.
    /// </summary>
    [Fact]
    public void TheManifestCheckFlagsAStoreWhichAcceptsChangedContent() => AssertEverySeedFails(Mutant.AcceptsChangedContent, ManifestCheck);

    /// <summary>
    /// The <c>lsm-mid-segment-watermark</c> mutant, whose drainers drain to their bound inside a segment, records a
    /// watermark which starts no segment, which the LSM-I2 check flags on the mutants' seed.
    /// </summary>
    [Fact]
    public void TheWholeSegmentCheckFlagsADrainInsideASegment() => AssertEverySeedFails(Mutant.MidSegmentWatermark, WholeSegmentCheck);

    /// <summary>
    /// The <c>never-drop</c> mutant drops no empty Delete as its key's base in the positive expiry schedule, which it runs
    /// alone on the mutants' seed, so the corpus check, which requires the path, flags it.
    /// </summary>
    [Fact]
    public void TheCorpusCheckFlagsACompactorWhichNeverDropsAnEmptyDelete()
    {
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, MutantSeedCount))
        {
            Counts? counts = null;
            _ = SeededSimulation.Run(seed, async environment => counts = await SimulateAsync(environment, Mutant.NeverDrop));
            Assert.Equal(0, counts!.Paths.GetValueOrDefault(Paths.ExpiriesDropped));
        }
    }

    private static void AssertEverySeedFails(Mutant mutant, params string[] oracles)
    {
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, MutantSeedCount))
        {
            Counts? counts = null;
            _ = SeededSimulation.Run(seed, async environment => counts = await SimulateAsync(environment, mutant));
            Assert.True(
                counts!.Violations.Any(violation => oracles.Any(oracle => violation.StartsWith(oracle, StringComparison.Ordinal))),
                Invariant($"Seed {seed}: the {mutant} mutant broke no '{string.Join("' or '", oracles.Select(static oracle => oracle.Trim()))}' check: [{string.Join("; ", counts.Violations)}]."));
        }
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

    private static async Task<Counts> SimulateAsync(SimulationEnvironment environment, Mutant mutant, ChangedContent changedContent = ChangedContent.None, int replicas = 1, bool supersededByClosedOnly = false)
    {
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map") { UnknownOutcomeRate = 0.05 };
        await using var objects = new SimulatedObjectStore(environment, "archive")
        {
            UnknownOutcomeRate = mutant == Mutant.DropOnUnknown ? 0.5 : 0.1,
            RetriedAnswerRate = 0.05,
            ReadFailureRate = 0.03,
            ChangedContentRate = mutant == Mutant.AcceptsChangedContent || changedContent != ChangedContent.None ? 0.2 : 0,
            LosesChangedContentRefusals = changedContent == ChangedContent.RefusedAnswerLost,
            AcceptsChangedContent = mutant == Mutant.AcceptsChangedContent,
        };
        await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None, SmallSegments.Options, replicas);
        var scenario = new Scenario(environment, register, ledgers, objects, mutant, supersededByClosedOnly);
        try
        {
            await scenario.RunAsync();
        }
        finally
        {
            await scenario.DisposeAsync();
        }

        // Every delayed write lands, or never will, before the manifest's history is judged.
        await Task.Delay(Max(objects.MaxLandingDelay, register.MaxLandingDelay) + TimeSpan.FromMilliseconds(1), environment.TimeProvider);
        scenario.JudgeManifests();
        scenario.JudgeDrains();
        scenario.JudgeSweeps();
        scenario.Counts.ChangedContentWrites = objects.ChangedContentWrites;
        return scenario.Counts;
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static Decided[] RowsOf(ReadOnlySpan<ArchiveRow> rows)
    {
        var decided = new Decided[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            decided[index] = new Decided(rows[index].Tag, rows[index].Kind, Encoding.UTF8.GetString(rows[index].Body.AsReadOnlySequence()));
        }

        return decided;
    }

    private static ArchiveFold Fold(IEnumerable<Decided> rows) => ArchiveOracle.Fold(rows, static row => row.Kind, static row => Encoding.UTF8.GetBytes(row.Payload));

    private static ManifestValue Decode(byte[] bytes) =>
        Assert.IsType<ManifestValue>(ManifestFormat.Decode(new ReadOnlySequence<byte>(bytes)).Value);

    /// <summary>The paths the corpus check requires: no fault, schedule, or outcome among them goes unexercised.</summary>
    private static class Paths
    {
        public const string Duplicates = "drains whose commit found their segment drained by another";
        public const string MixedBatches = "commit epochs answering one drain carried and another's duplicate";
        public const string Resubmitted = "pending drains submitted again";
        public const string LostAnswers = "commits whose answer was lost";
        public const string DrainFailures = "drains which failed";
        public const string Restarts = "drain worker restarts";
        public const string DuplicateAfterRestart = "restarted workers whose first drain found its segment drained";
        public const string OpenSegments = "drains which found their segment open";
        public const string MarkerSegments = "segments holding only a marker drained without an object";
        public const string MarkerDrains = "failure-triggered drains which ended at their marker's slot";
        public const string Splits = "segments split across objects";
        public const string Covered = "covered drains";
        public const string CoveredFromALaggingView = "covered drains of a ledger a lagging view lists open";
        public const string DrainDropped = "drains which met a dropped replica";
        public const string DrainBackpressure = "drains which met backpressure";
        public const string GridBits1 = "drains with one grid bit";
        public const string GridBits4 = "drains with four grid bits";
        public const string Drops = "ledger drops";
        public const string DropperFailures = "dropper passes which failed";
        public const string ReplicaDrops = "replicas the node's reconciliation dropped";
        public const string MapUnknown = "map writes whose outcome was unknown";
        public const string ReplicaBackpressure = "replica reads which answered backpressure";
        public const string StoreStale = "store reads with a map older than the store's view";
        public const string ReaderStale = "history reads restarted on Stale";
        public const string ReaderDropped = "history reads restarted on a dropped ledger";
        public const string ReaderBackpressured = "history reads resumed after backpressure";
        public const string Compactions = "compactions applied";
        public const string CompactionsSuperseded = "compactions superseded by a racing one";
        public const string Pushes = "pushes";
        public const string MergesInPlace = "merges in place";
        public const string CompactionDrops = "compactions which dropped rows";
        public const string CompactionFailures = "compactions which failed";
        public const string SubsetReads = "pushes reading a strict subset of an input's blocks";
        public const string FailuresAfterUpload = "pushes failing after a child's upload";
        public const string ExpiriesDropped = "compactions which dropped an empty Delete as its key's base";
        public const string ExpiredReads = "store answers without an empty Delete a compaction dropped";
        public const string SupersededReads = "store answers without dropped rows a closed, complete listed ledger superseded";
        public const string Marked = "objects a sweep marked";
        public const string SweptDropped = "drain outputs swept once their ledger lay below DrainedBelow";
        public const string SweptComplete = "drain outputs swept once their ledger completed";
        public const string SweptPassed = "drain outputs swept once their ledger's watermark passed them";
        public const string SweptUnlisted = "compaction outputs swept once an input was no longer listed";
        public const string SweptRemoved = "objects swept once a compaction removed them";
        public const string KeptInFlight = "sweeps which kept an unreferenced upload whose condition held";
        public const string HeldInFlight = "uploads unreferenced across two sweeps whose held commit then applied";
        public const string HeldDuplicated = "held uploads whose segment a competing drain committed";
        public const string SweepFailures = "sweeps which failed";
        public const string SweeperRestarts = "sweeper restarts";

        public static readonly string[] All =
        [
            Duplicates, MixedBatches, Resubmitted, LostAnswers, DrainFailures, Restarts, DuplicateAfterRestart, OpenSegments,
            MarkerSegments, MarkerDrains, Splits, Covered, CoveredFromALaggingView, DrainDropped, DrainBackpressure, GridBits1,
            GridBits4, Drops, DropperFailures,
            ReplicaDrops, MapUnknown, ReplicaBackpressure, StoreStale, ReaderStale, ReaderDropped, ReaderBackpressured,
            Compactions, CompactionsSuperseded, Pushes, MergesInPlace, CompactionDrops, CompactionFailures, SubsetReads, FailuresAfterUpload, ExpiriesDropped, ExpiredReads, SupersededReads,
            Marked, SweptDropped, SweptComplete, SweptPassed, SweptUnlisted, SweptRemoved, KeptInFlight, HeldInFlight, HeldDuplicated,
            SweepFailures, SweeperRestarts,
        ];

        /// <summary>
        /// Gets the fault paths the random rounds must take themselves, though a scripted step also forces each: a scripted
        /// step proves the path is handled, and only a random one, where it races drains, compactions, and sweeps, tests it.
        /// </summary>
        public static readonly string[] Generated = [SweepFailures, Duplicates, Splits, OpenSegments, MarkerSegments];
    }

    private sealed class Counts
    {
        /// <summary>Gets the paths the random rounds and the final phase took.</summary>
        public Dictionary<string, int> Paths { get; } = [with(StringComparer.Ordinal)];

        /// <summary>Gets the paths the scripted steps and schedules took.</summary>
        public Dictionary<string, int> Scripted { get; } = [with(StringComparer.Ordinal)];

        /// <summary>Gets or sets a value indicating whether a scripted step runs, whose paths count apart.</summary>
        public bool Scripting { get; set; }

        public List<string> Violations { get; } = [];

        /// <summary>Gets or sets the number of creates whose content the object store changed.</summary>
        public int ChangedContentWrites { get; set; }

        /// <summary>Counts a path taken, by a scripted step when <paramref name="scripted"/>, or while one runs.</summary>
        public void Add(string path, int count = 1, bool? scripted = null)
        {
            var paths = scripted ?? Scripting ? Scripted : Paths;
            paths[path] = paths.GetValueOrDefault(path) + count;
        }

        /// <summary>Runs <paramref name="step"/> as a scripted step, whose paths count apart.</summary>
        public async Task ScriptAsync(Func<Task> step)
        {
            var scripting = Scripting;
            Scripting = true;
            try
            {
                await step();
            }
            finally
            {
                Scripting = scripting;
            }
        }
    }

    /// <summary>A decided row of a key: its tag, kind, and payload.</summary>
    private readonly record struct Decided(Tag Tag, RowKind Kind, string Payload);

    /// <summary>A proposal answered durable, with the scenario's steps at which it was called and answered.</summary>
    private sealed record Proposal(Decided Row, long CalledAt, long AnsweredAt);

    /// <summary>
    /// A history read, with its steps, its live rows, what its prefix and rows not held fold to, its <c>Through</c>, and
    /// whether it reported itself linearizable.
    /// </summary>
    private sealed record HistoryReadRecord(string Reader, string Key, long CalledAt, long AnsweredAt, Decided[] Rows, ArchiveFold Fold, Dictionary<LedgerId, Slot> Through, bool Linearizable)
    {
        /// <summary>Gets the chain checker's verdict on the read's rows, judged as it answered.</summary>
        public string? Chain { get; init; }

        /// <summary>Gets the prefix's version base and the state it holds, or null for <c>Absent</c>.</summary>
        public (RowToken Base, string State)? VersionBase { get; init; }
    }

    /// <summary>A store answer: the map value it paired, and what its prefix folds to, or null for absence.</summary>
    private sealed record StoreReadRecord(string Reader, string Key, LedgerMapValue Map, ArchiveFold? Prefix, ulong? ViewEpoch)
    {
        /// <summary>Gets the prefix's version base, or null for <c>Absent</c>.</summary>
        public RowToken? VersionBase { get; init; }
    }

    /// <summary>A row of a section, with its key, as its object landed.</summary>
    private sealed record SectionRow(string Key, Tag Tag, RowKind Kind, string Payload);

    /// <summary>
    /// A reader process: its map view and reader, its archive store, the history reader over them, and a map value one of
    /// its readers took at the start of the round and still holds.
    /// </summary>
    private sealed record ReaderProcess(string Name, LedgerMapView View, LedgerReader Reader, LsmArchiveStore Store, ArchiveReader History, bool Owned)
    {
        public LedgerMapValue Held { get; set; } = View.Current;
    }

    /// <summary>
    /// A drainer process: its own map view, reader, and manifest view, and its drain worker, which a restart replaces, as
    /// the <c>lsm-mid-segment-watermark</c> mutant when <paramref name="insideSegments"/>.
    /// </summary>
    private sealed class Drainer(LedgerMapView view, LedgerReader reader, IObjectStore store, ArchiveOptions options, bool insideSegments)
    {
        /// <summary>Gets the options the drainer's workers drain under, its grid step among them.</summary>
        public ArchiveOptions Options => options;

        private readonly ManifestView _manifest = new(store);

        public LedgerMapView View { get; } = view;

        public LedgerReader Reader { get; } = reader;

        public DrainWorker Worker { get; private set; } = Create(store, new ManifestView(store), reader, options, insideSegments);

        /// <summary>Gets a value indicating whether the worker restarted and has had no drain decided since.</summary>
        public bool Restarted { get; private set; }

        /// <summary>Gets or sets the change the worker last submitted in the current drain.</summary>
        public ManifestChange.Drain? Submitted { get; set; }

        /// <summary>
        /// Replaces the worker, as a crash and restart of the process's drain service do: the new worker holds no resume
        /// point, and takes its watermarks from the process's manifest view, which may predate its predecessor's commits.
        /// </summary>
        public void Restart()
        {
            Worker = Create(store, _manifest, Reader, options, insideSegments);
            Restarted = true;
        }

        public void Decided() => Restarted = false;

        private static DrainWorker Create(IObjectStore store, ManifestView manifest, LedgerReader reader, ArchiveOptions options, bool insideSegments) =>
            new(store, manifest, reader, options, SimulationEnvironment.Clock) { DrainsInsideSegmentsMutant = insideSegments };
    }

    /// <summary>
    /// A drain answered <c>Applied</c>, by the committer and at the epoch which answered it, whether its worker had
    /// restarted, and whether a scripted step drained it.
    /// </summary>
    private sealed record AppliedDrain(int Committer, ulong Epoch, ManifestChange.Drain Change, bool AfterRestart, bool Scripted);

    /// <summary>A drain's answer that the segment at <paramref name="From"/> does not close below <paramref name="Below"/>.</summary>
    private sealed record OpenAnswer(LedgerId Ledger, Slot From, Slot Below);

    /// <summary>A compactor process: its own map view and manifest view, and its compaction worker.</summary>
    private sealed record Compactor(LedgerMapView View, ManifestView Manifest, CompactionWorker Worker);

    /// <summary>
    /// A sweeper process: its own manifest view and its sweeper, which a restart replaces, losing its marks.
    /// </summary>
    private sealed class SweeperProcess
    {
        private readonly IObjectStore _store;
        private readonly ManifestView _manifest;
        private readonly bool _sweepDrainMutant;
        private readonly bool _sweepCompactionMutant;

        public SweeperProcess(IObjectStore store, bool sweepDrainMutant = false, bool sweepCompactionMutant = false)
        {
            (_store, _manifest, _sweepDrainMutant, _sweepCompactionMutant) = (store, new ManifestView(store), sweepDrainMutant, sweepCompactionMutant);
            Sweeper = Create();
        }

        public Sweeper Sweeper { get; private set; }

        /// <summary>Gets or sets a value indicating whether the sweeper restarted and has completed no sweep since.</summary>
        public bool Restarted { get; set; }

        public void Restart()
        {
            Sweeper = Create();
            Restarted = true;
        }

        private Sweeper Create() =>
            new(_store, _manifest, SimulationEnvironment.Clock, NullLogger<Sweeper>.Instance)
            {
                SweepDrainMutant = _sweepDrainMutant,
                SweepCompactionMutant = _sweepCompactionMutant,
            };
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly SimulationEnvironment _environment;
        private readonly SimulatedConditionalRegister _register;
        private readonly LiveLedgers _ledgers;
        private readonly SimulatedObjectStore _objects;
        private readonly Mutant _mutant;
        private readonly bool _supersededByClosedOnly;
        private readonly SimulationRandom _random;
        private readonly ArchiveOptions _options;
        private readonly ManifestView _evidence;
        private readonly ManifestCommitter _first;
        private readonly ManifestCommitter _second;
        private readonly ManifestCommitter[] _committers;
        private readonly List<ReaderProcess> _readers = [];
        private readonly List<Drainer> _drainers = [];
        private readonly List<Compactor> _compactors = [];
        private readonly List<SweeperProcess> _sweepers = [];
        private readonly SimulationRandom _sweepRandom;
        private readonly bool _compacting;
        private readonly List<ManifestChange.Compaction> _uploaded = [];
        private readonly HashSet<Tag> _droppedExpiries = [];
        private readonly Dictionary<SectionRef, SectionRow[]> _sectionRows = [];
        private readonly Dictionary<string, List<Decided>> _reference = Keys.ToDictionary(static key => key, static _ => new List<Decided>(), StringComparer.Ordinal);
        private readonly List<Proposal> _proposals = [];
        private readonly List<HistoryReadRecord> _reads = [];
        private readonly List<StoreReadRecord> _storeReads = [];
        private readonly Dictionary<LedgerId, long> _closedAt = [];
        private readonly Dictionary<ManifestChange.Drain, int> _committedBy = [with(ReferenceEqualityComparer.Instance)];
        private readonly Dictionary<ObjectName, ManifestChange.Drain> _drainOf = [];
        private readonly List<AppliedDrain> _appliedDrains = [];
        private readonly List<ManifestChange.Drain> _submittedDrains = [];
        private readonly List<OpenAnswer> _openAnswers = [];
        private readonly Dictionary<LedgerId, (ImmutableArray<Slot> Starts, Slot Below, Slot? T)> _decidedStarts = [];
        private readonly VersionChecker _versions = new();
        private int _readsChecked;
        private int _storeReadsChecked;
        private LedgerMapView? _dropperView;
        private FlakySweepStore? _sweepStore;
        private double _lossRate = 0.05;
        private long _steps;

        public Scenario(SimulationEnvironment environment, SimulatedConditionalRegister register, LiveLedgers ledgers, SimulatedObjectStore objects, Mutant mutant, bool supersededByClosedOnly = false)
        {
            _environment = environment;
            _register = register;
            _ledgers = ledgers;
            _objects = objects;
            _mutant = mutant;
            _supersededByClosedOnly = supersededByClosedOnly;

            // The drain and drop mutants target drains, drops, and reads, which run without compactors or sweeps.
            _compacting = mutant is not (Mutant.UncoveredDrop or Mutant.DropOnUnknown or Mutant.MidSegmentWatermark);
            _random = environment.CreateRandom("archive-scenario");
            _sweepRandom = environment.CreateRandom("archive-sweeps");
            // The compactors push one bit at a time; the drainers drain at one bit and at four (StartAsync).
            // Blocks of 64 bytes, a row or two each, so a push's children share blocks and read strict subsets of them.
            _options = new ArchiveOptions { GridBits = 1, MaxSectionsPerPartition = 2, SectionBytes = 150, SectionBlockBytes = 64 };
            _evidence = new ManifestView(objects);
            _first = new(objects, _evidence, SimulationEnvironment.Clock, TimeSpan.Zero) { StaleDrainMutant = mutant == Mutant.StaleDrain };
            _second = new(objects, new ManifestView(objects), SimulationEnvironment.Clock, TimeSpan.Zero);
            _committers = [_first, _second];
            var backpressure = environment.CreateRandom("archive-backpressure");
            ledgers.Transport.Saturated = (_, _) => backpressure.Chance(0.03);
            environment.Invariants.Add("archive-chains", CheckChainsAndVersions);
        }

        public Counts Counts { get; } = new();

        /// <summary>Gets a value indicating whether the run sweeps: the corpus's and the sweep mutants' do.</summary>
        private bool Sweeps => _mutant is Mutant.None or Mutant.SweepDrain or Mutant.SweepCompaction;

        private LedgerDropper Dropper { get => field ?? throw new InvalidOperationException("The scenario has not started."); set; }

        private LedgerMapView NodeView { get => field ?? throw new InvalidOperationException("The scenario has not started."); set; }

        private LedgerMapView DropperView => _dropperView ?? throw new InvalidOperationException("The scenario has not started.");

        private FlakySweepStore SweepStore => _sweepStore ?? throw new InvalidOperationException("The scenario has not started.");

        public async Task RunAsync()
        {
            await StartAsync();
            if (_mutant == Mutant.SweepDrain)
            {
                for (var round = 0; round < Rounds; round++)
                {
                    await Counts.ScriptAsync(() => HoldCommitsAsync(duplicated: round % 2 == 1));
                }

                await RecordListedStartsAsync();
                return;
            }

            Func<Task>? scriptedMutant = _supersededByClosedOnly
                ? CompactWithAClosedCompleteReplaceAsync
                : _mutant switch
                {
                    Mutant.SweepCompaction => HoldCompactionCommitAsync,
                    Mutant.StaleDrain => CommitAStaleDrainAsync,
                    Mutant.IgnoreMissing => ReadAcrossASweptSectionAsync,
                    Mutant.HoldByWatermark => ReadWithOutOfOrderDrainAsync,
                    Mutant.SupersedeByOpen => CompactWithAnOpenReplaceAsync,
                    Mutant.NoPredatesCheck => ReadWithAStaleOpenMapAsync,
                    Mutant.None or Mutant.UncoveredDrop or Mutant.UncoveredExpiry or Mutant.DropOnUnknown
                        or Mutant.DropIgnoringLive or Mutant.DropIgnoringSections or Mutant.DropTrustingUnlisted
                        or Mutant.NeverDrop or Mutant.SweepDrain or Mutant.AcceptsChangedContent
                        or Mutant.MidSegmentWatermark => null,
                    _ => throw new UnreachableException(),
                };
            if (scriptedMutant is not null)
            {
                await WithoutFaultsAsync(async () =>
                {
                    await Counts.ScriptAsync(scriptedMutant);
                    return true;
                });
                await RecordListedStartsAsync();
                VerifyReads();
                VerifyStoreReads();
                return;
            }

            // The expiry mutants meet their oracles in the fixed expiry schedules, which run without the random rounds.
            var rounds = _mutant is Mutant.UncoveredExpiry or Mutant.DropIgnoringLive or Mutant.DropIgnoringSections or Mutant.DropTrustingUnlisted or Mutant.NeverDrop ? 0 : Rounds;
            for (var round = 0; round < rounds; round++)
            {
                foreach (var reader in _readers)
                {
                    reader.Held = reader.View.Current;
                }

                await SimulationTasks.WhenAll(
                    WriteAsync(),
                    DrainAsync(_drainers[0]),
                    DrainAsync(_drainers[1]),
                    _compacting ? CompactAsync(_compactors[0]) : Task.CompletedTask,
                    _compacting ? CompactAsync(_compactors[1]) : Task.CompletedTask,
                    DropAsync(),
                    SweepAsync(_sweepers[0]),
                    SweepAsync(_sweepers[1]),
                    ReadAsync(_readers[0]),
                    ReadAsync(_readers[1]));
                await Counts.ScriptAsync(() => ReadAcrossADrainAndDropAsync(staleMap: round % 2 == 1));

                // The held schedule's two variants run once each, in the first two rounds.
                if (Sweeps && round < 2)
                {
                    await Counts.ScriptAsync(() => HoldCommitsAsync(duplicated: round == 1));
                }
            }

            await SettleAsync();
            await RecordListedStartsAsync();
            VerifyReads();
            VerifyStoreReads();
            CountPaths();
        }

        /// <summary>
        /// Checks the manifest's history (LSM-I1): <c>DrainedBelow</c> never decreases, and no ledger loses its completion
        /// or, incomplete, its watermark.
        /// </summary>
        public void JudgeManifests()
        {
            var previous = ManifestValue.Absent;
            var removed = new HashSet<SectionRef>();
            var changed = _objects.ChangedObjects.ToHashSet();
            foreach (var bytes in _objects.AppliedWritesOf(ObjectName.Manifest))
            {
                var value = Decode(bytes);
                foreach (var section in value.Sections.Where(section => changed.Contains(section.Object)))
                {
                    Counts.Violations.Add(Invariant($"{ManifestCheck}epoch {value.Epoch} lists {section.Object}, whose content changed after its writer verified it."));
                }

                JudgeSuccession(previous, value);
                JudgeRemovals(previous, value, removed);
                previous = value;
            }
        }

        /// <summary>
        /// Checks every delete against the manifest's history (LSM-I6, LSM-I7): the manifest durable when it applied
        /// neither references the object nor could, by the reference reading of archive §2.9 applied to the change behind
        /// the object, no later manifest references it, and no write of its name lands after it. Counts each delete by what
        /// made its condition false, and whether a compaction had removed it.
        /// </summary>
        public void JudgeSweeps()
        {
            var writes = _objects.AppliedWrites;
            var manifests = new List<(int Index, ManifestValue Value)>();
            for (var index = 0; index < writes.Count; index++)
            {
                if (writes[index].Name == ObjectName.Manifest)
                {
                    manifests.Add((index, Decode(writes[index].Content)));
                }
            }

            foreach (var (name, before) in _objects.Deletions)
            {
                var durable = manifests.LastOrDefault(manifest => manifest.Index < before).Value ?? ManifestValue.Absent;
                if (References(durable, name))
                {
                    Counts.Violations.Add(Invariant($"{SweepCheck}{name} was deleted while the manifest at epoch {durable.Epoch} referenced it."));
                }
                else if (Deletable(ReferenceManifest.Of(durable), name) is { } cause)
                {
                    Counts.Add(cause);
                    if (manifests.Any(manifest => manifest.Index < before && References(manifest.Value, name)))
                    {
                        Counts.Add(Paths.SweptRemoved);
                    }
                }
                else
                {
                    Counts.Violations.Add(Invariant($"{SweepCheck}{name} was deleted while its reference condition held at epoch {durable.Epoch}."));
                }

                if (manifests.FirstOrDefault(manifest => manifest.Index >= before && References(manifest.Value, name)).Value is { } later)
                {
                    Counts.Violations.Add(Invariant($"{SweepCheck}{name} was deleted, and the manifest at epoch {later.Epoch} references it."));
                }

                if (writes.Skip(before).Any(write => write.Name == name))
                {
                    Counts.Violations.Add(Invariant($"{NameCheck}a write of {name} landed after its deletion."));
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var reader in _readers.Where(static reader => reader.Owned))
            {
                await reader.Reader.DisposeAsync();
            }

            foreach (var drainer in _drainers)
            {
                await drainer.Reader.DisposeAsync();
            }

            await _first.DisposeAsync();
            await _second.DisposeAsync();
        }

        private static string Show(IEnumerable<Decided> rows) => string.Join(", ", rows.Select(static row => row.Payload));

        /// <summary>
        /// Checks LSM-I7 and LSM-I5 across one replace: no section once removed is listed again, and the sections it removes
        /// are exactly the inputs of compactions which were uploaded, each with every input listed before and none after,
        /// and every output listed after.
        /// </summary>
        private void JudgeRemovals(ManifestValue previous, ManifestValue value, HashSet<SectionRef> removed)
        {
            var listed = value.Sections.ToHashSet();
            foreach (var section in value.Sections.Where(removed.Contains))
            {
                Counts.Violations.Add(Invariant($"{ManifestCheck}epoch {value.Epoch} lists again {section.Object}'s section at {section.Offset}, removed before (LSM-I7)."));
            }

            var before = previous.Sections.ToHashSet();
            var gone = previous.Sections.Where(section => !listed.Contains(section)).ToHashSet();
            var explained = _uploaded
                .Where(change => change.Inputs.All(before.Contains) && !change.Inputs.Any(listed.Contains) && change.Outputs.All(listed.Contains))
                .SelectMany(static change => change.Inputs)
                .ToHashSet();
            if (!gone.SetEquals(explained))
            {
                Counts.Violations.Add(Invariant($"{ManifestCheck}epoch {value.Epoch} removes sections no applied compaction's inputs account for."));
            }

            removed.UnionWith(gone);
        }

        private static bool References(ManifestValue value, ObjectName name) => value.Sections.Any(section => section.Object == name);

        /// <summary>
        /// Returns what makes the run object <paramref name="name"/> deletable in <paramref name="manifest"/>, the path which
        /// counts it, or null when a commit could still reference it (archive §8.5), by the reference reading of §2.9: a
        /// drain output's change's rule fails and stays failed, as its ledger was dropped, completed, or drained past its
        /// start; a compaction output, whose trailer names exactly its change's inputs, has an input no longer listed.
        /// </summary>
        private string? Deletable(ReferenceManifest manifest, ObjectName name)
        {
            if (name.Value.StartsWith("c/", StringComparison.Ordinal))
            {
                return CompactionInputsOf(name).All(input => manifest.Sections.Any(section => section.Object == input.Object && section.Offset == input.Offset))
                    ? null
                    : Paths.SweptUnlisted;
            }

            if (DrainOf(name) is not { } change || ManifestReference.Rule(change, manifest))
            {
                return null;
            }

            var ledger = change.Ledger.Value;
            return ledger < manifest.DrainedBelow ? Paths.SweptDropped
                : manifest.MarkedComplete(ledger) ? Paths.SweptComplete
                : manifest.WatermarkOf(ledger) > change.From.Value ? Paths.SweptPassed
                : null;
        }

        /// <summary>
        /// Returns the inputs of the compaction behind an output: those of the change the compaction answered, which must
        /// equal exactly the inputs its landed trailer names (LSM-I5), so a trailer naming another input breaks this check
        /// on its own; or, for an output of a compaction which failed before it answered, its trailer's. No commit
        /// references the output once one of them is no longer listed.
        /// </summary>
        private SectionPosition[] CompactionInputsOf(ObjectName name)
        {
            var content = _objects.AppliedWrites.First(write => write.Name == name).Content;
            var trailer = Assert.IsType<RunTrailer>(RunObjectFormat.DecodeTrailerAt(new ReadOnlySequence<byte>(content), 0).Value);
            SectionPosition[] recorded = [.. Assert.IsType<RunObjectCondition.Compaction>(trailer.Condition.Value).Inputs];
            if (_uploaded.FirstOrDefault(change => change.Outputs.Any(output => output.Object == name)) is not { } change)
            {
                return recorded;
            }

            SectionPosition[] inputs = [.. change.Inputs.Select(static input => new SectionPosition(input.Object, input.Offset)).Order()];
            if (!inputs.SequenceEqual(recorded))
            {
                Counts.Violations.Add(Invariant($"{InputsCheck}{name}'s trailer names inputs other than its compaction's."));
            }

            return inputs;
        }

        /// <summary>
        /// Returns the change behind a drain output: the one a drain submitted with it, or, for an upload whose drain never
        /// submitted its change, one of a slot from the ledger and watermark its name <c>d/{S}/{a}/{id}</c> gives; null for
        /// any other name.
        /// </summary>
        private ManifestChange.Drain? DrainOf(ObjectName name)
        {
            if (_drainOf.TryGetValue(name, out var change))
            {
                return change;
            }

            return name.Value.Split('/') is ["d", var ledger, var from, _]
                ? new ManifestChange.Drain(
                    new LedgerId(ulong.Parse(ledger, CultureInfo.InvariantCulture)),
                    new Slot(long.Parse(from, CultureInfo.InvariantCulture)),
                    new Slot(long.Parse(from, CultureInfo.InvariantCulture) + 1),
                    [],
                    complete: false)
                : null;
        }

        /// <summary>Records a drain's submitted change as the one behind each object its sections name, for its judging.</summary>
        private void Submitted(ManifestChange change)
        {
            var drain = Assert.IsType<ManifestChange.Drain>(change.Value);
            foreach (var section in drain.Sections)
            {
                _drainOf[section.Object] = drain;
            }

            _submittedDrains.Add(drain);
        }

        /// <summary>Submits a drain's change to a committer, recording it as <see cref="Submitted"/> does.</summary>
        private Func<ManifestChange, CancellationToken, Task<ManifestCommitOutcome>> Checked(ManifestCommitter committer) =>
            (change, cancellationToken) =>
            {
                Submitted(change);
                return committer.CommitAsync(change, cancellationToken);
            };

        /// <summary>
        /// Records the slots at which <paramref name="ledger"/>'s decided batches below <paramref name="below"/> start
        /// segments, scanning the decided ledger, once a ledger closes and before it drops, or, open, at the run's end.
        /// </summary>
        private async Task RecordDecidedStartsAsync(LedgerMetadata ledger, Slot below)
        {
            if (_decidedStarts.TryGetValue(ledger.Id, out var recorded) && recorded.Below >= below)
            {
                return;
            }

            if (await _ledgers.DecidedStartsAsync(ledger, below, CancellationToken.None) is { } starts)
            {
                _decidedStarts[ledger.Id] = (starts, below, ledger.Status.Value is LedgerStatus.Closed closed ? closed.T : null);
            }
        }

        /// <summary>
        /// Records the decided starts of every ledger the writer's view lists, the open ones below the tail their primary
        /// confirms, with every fault off.
        /// </summary>
        private async Task RecordListedStartsAsync()
        {
            var saturated = _ledgers.Transport.Saturated;
            _ledgers.Transport.Saturated = static (_, _) => false;
            try
            {
                foreach (var ledger in (await _ledgers.Map.ReadAsync(CancellationToken.None)).Ledgers)
                {
                    var below = ledger.Status.Value is LedgerStatus.Closed closed ? closed.T : await _ledgers.ConfirmOpenAsync(ledger.Id, CancellationToken.None);
                    await RecordDecidedStartsAsync(ledger, below);
                }
            }
            finally
            {
                _ledgers.Transport.Saturated = saturated;
            }
        }

        /// <summary>
        /// Checks every drain against the decided segments (LSM-I2), which the recovered, decided ledgers give: each
        /// submitted change starts at a segment start and ends at the next, or completes its ledger at its <c>T</c>, with
        /// no segment starting between, and each answer that a segment is open finds no start between its start and its
        /// bound; and every incomplete ledger's watermark the manifest's history records is a segment start. A ledger whose
        /// starts no scan recorded, as one a mutant dropped unseen, is not judged.
        /// </summary>
        public void JudgeDrains()
        {
            foreach (var drain in _submittedDrains)
            {
                if (!_decidedStarts.TryGetValue(drain.Ledger, out var decided))
                {
                    continue;
                }

                if (drain.From == drain.Below && !drain.Complete)
                {
                    Counts.Violations.Add(Invariant($"{WholeSegmentCheck}a drain of ledger {drain.Ledger} over the empty range at {drain.From} completes nothing."));
                }

                if (drain.Complete && drain.Below != decided.T)
                {
                    Counts.Violations.Add(Invariant($"{WholeSegmentCheck}a drain of ledger {drain.Ledger} completes it at {drain.Below}, not at its T {decided.T}."));
                }

                if (drain.From != drain.Below && !decided.Starts.Contains(drain.From))
                {
                    Counts.Violations.Add(Invariant($"{WholeSegmentCheck}a drain of ledger {drain.Ledger} starts at {drain.From}, which starts no decided segment of [{string.Join(", ", decided.Starts)}]."));
                }

                if (!drain.Complete && !decided.Starts.Contains(drain.Below))
                {
                    Counts.Violations.Add(Invariant($"{WholeSegmentCheck}a drain of ledger {drain.Ledger} ends at {drain.Below}, which starts no decided segment of [{string.Join(", ", decided.Starts)}]."));
                }

                if (decided.Starts.FirstOrDefault(start => start > drain.From && start < drain.Below) is var inside && inside > drain.From)
                {
                    Counts.Violations.Add(Invariant($"{WholeSegmentCheck}a drain of ledger {drain.Ledger} over [{drain.From}, {drain.Below}) holds the segment start {inside}."));
                }
            }

            foreach (var open in _openAnswers)
            {
                if (_decidedStarts.TryGetValue(open.Ledger, out var decided) && decided.Starts.FirstOrDefault(start => start > open.From && start < open.Below) is var start && start > open.From)
                {
                    Counts.Violations.Add(Invariant($"{WholeSegmentCheck}a drain of ledger {open.Ledger} answered its segment at {open.From} open below {open.Below}, and a segment starts at {start}."));
                }
            }

            foreach (var bytes in _objects.AppliedWritesOf(ObjectName.Manifest))
            {
                var value = Decode(bytes);
                foreach (var (ledger, watermark) in value.Watermarks)
                {
                    if (!watermark.Complete && watermark.Below != Slot.Zero && _decidedStarts.TryGetValue(ledger, out var decided) && !decided.Starts.Contains(watermark.Below))
                    {
                        Counts.Violations.Add(Invariant($"{WholeSegmentCheck}epoch {value.Epoch} records ledger {ledger}'s watermark at {watermark.Below}, which starts no decided segment."));
                    }
                }
            }

            if (_mutant == Mutant.None && _submittedDrains.Select(static drain => drain.Ledger).Distinct().FirstOrDefault(ledger => !_decidedStarts.ContainsKey(ledger)) is { } unseen && unseen != default)
            {
                Counts.Violations.Add(Invariant($"{WholeSegmentCheck}no scan recorded the decided starts of ledger {unseen}, which drains took."));
            }
        }
        private void JudgeSuccession(ManifestValue previous, ManifestValue value)
        {
            if (value.DrainedBelow < previous.DrainedBelow)
            {
                Counts.Violations.Add(Invariant($"{ManifestCheck}epoch {value.Epoch} lowers DrainedBelow from {previous.DrainedBelow} to {value.DrainedBelow}."));
            }

            var ledgers = new SortedSet<LedgerId>(previous.Watermarks.Keys);
            for (var id = previous.DrainedBelow.Value; id < Math.Max(previous.DrainedBelow.Value, value.DrainedBelow.Value); id++)
            {
                _ = ledgers.Add(new LedgerId(id));
            }

            foreach (var ledger in ledgers)
            {
                if (previous.IsComplete(ledger) && !value.IsComplete(ledger))
                {
                    Counts.Violations.Add(Invariant($"{ManifestCheck}epoch {value.Epoch} loses ledger {ledger}'s completion of epoch {previous.Epoch}."));
                }

                if (!value.IsComplete(ledger) && value.RecordedWatermark(ledger) < previous.RecordedWatermark(ledger))
                {
                    Counts.Violations.Add(Invariant($"{ManifestCheck}epoch {value.Epoch} lowers ledger {ledger}'s watermark of epoch {previous.Epoch}."));
                }
            }
        }

        private async Task StartAsync()
        {
            await TransitionAsync(null, () => _ledgers.BootstrapAsync(CancellationToken.None));
            _readers.Add(NewReader("writer", _ledgers.Map, _ledgers.Reader, owned: false));
            var view = await _ledgers.OpenViewAsync(CancellationToken.None);
            _readers.Add(NewReader("reader", view, _ledgers.OpenReader(view), owned: true));
            for (var index = 0; index < 2; index++)
            {
                var drainerView = await _ledgers.OpenViewAsync(CancellationToken.None);
                // The drainers drain at different grid steps, one bit and four, so one seed places sections at both.
                var gridBits = index == 0 ? 1 : 4;
                _drainers.Add(new Drainer(
                    drainerView,
                    _ledgers.OpenReader(drainerView),
                    _objects,
                    _options with
                    {
                        GridBits = gridBits,
                        MaxSectionsPerPartition = Math.Max(_options.MaxSectionsPerPartition, 1 << gridBits),
                        SectionBytes = index == 0 ? DrainSectionBytes : _options.SectionBytes,
                    },
                    _mutant == Mutant.MidSegmentWatermark));
                var compactorView = new ManifestView(_objects);
                _compactors.Add(new Compactor(await _ledgers.OpenViewAsync(CancellationToken.None), compactorView, NewCompactor(view: compactorView)));
            }

            NodeView = await _ledgers.OpenViewAsync(CancellationToken.None);
            _dropperView = await _ledgers.OpenViewAsync(CancellationToken.None);
            Dropper = new LedgerDropper(_dropperView, _evidence, NullLogger<LedgerDropper>.Instance);
            _sweepStore = new FlakySweepStore(_objects, _environment.CreateRandom("archive-sweep-faults"));
            for (var index = 0; index < 2; index++)
            {
                _sweepers.Add(new SweeperProcess(_sweepStore, _mutant == Mutant.SweepDrain, _mutant == Mutant.SweepCompaction));
            }
        }

        /// <summary>
        /// Returns a drain worker of the writer's process, whose reader knows the tails its markers raise, with its own
        /// manifest view, or <paramref name="view"/>, and the scenario's options at the drains' section size, or
        /// <paramref name="options"/>.
        /// </summary>
        private DrainWorker NewWorker(ManifestView? view = null, ArchiveOptions? options = null) =>
            new(_objects, view ?? new ManifestView(_objects), _ledgers.Reader, options ?? _options with { SectionBytes = DrainSectionBytes }, SimulationEnvironment.Clock);

        /// <summary>Returns a compaction worker wired with the scenario's active compaction mutant, if any.</summary>
        private CompactionWorker NewCompactor(ArchiveOptions? options = null, IObjectStore? store = null, ManifestView? view = null)
        {
            var effective = options ?? _options;
            var objects = store ?? _objects;
            return new CompactionWorker(objects, new TailCache(objects, effective), view ?? new ManifestView(objects), effective)
            {
                DropIgnoringLiveMutant = _mutant == Mutant.DropIgnoringLive,
                DropIgnoringSectionsMutant = _mutant == Mutant.DropIgnoringSections,
                SupersedeByOpenMutant = _mutant == Mutant.SupersedeByOpen,
            };
        }

        /// <summary>Returns an archive store of a process of its own: its manifest view and tail cache.</summary>
        private LsmArchiveStore NewStore() => new(_objects, new ManifestView(_objects), new TailCache(_objects, _options), _options, SimulationEnvironment.Clock)
        {
            IgnoreMissingMutant = _mutant == Mutant.IgnoreMissing,
            SkipPredatesCheckMutant = _mutant == Mutant.NoPredatesCheck,
            HoldByWatermarkMutant = _mutant == Mutant.HoldByWatermark,
        };

        private ReaderProcess NewReader(string name, LedgerMapView view, LedgerReader reader, bool owned)
        {
            var store = NewStore();
            IArchiveStore read = _mutant == Mutant.UncoveredDrop ? new LaggingMapStore(store) : store;
            return new ReaderProcess(name, view, reader, store, new ArchiveReader(reader, read, SimulationEnvironment.Clock), owned);
        }

        /// <summary>
        /// Proposes rows of random keys and kinds through the writer process, now and then replacing or splitting the key's
        /// ledger, and records each decided row in the reference.
        /// </summary>
        private async Task WriteAsync()
        {
            for (var count = 0; count < ProposalsPerRound; count++)
            {
                await DelayAsync(4);
                var key = _random.Choose(Keys);
                var kind = _random.Next(10) switch
                {
                    < 7 => RowKind.Append,
                    < 9 => RowKind.Replace,
                    _ => RowKind.Delete,
                };

                // Half the Deletes are empty, expiries, which a compaction may drop with every older row of their key.
                await ProposeAsync(key, kind, empty: kind == RowKind.Delete && _random.Chance(0.5));

                // Each round's first proposal is followed by a durable marker and its ledger's close, so the random rounds
                // close a segment which holds a marker alone on every seed, and their drains meet it among their races.
                var forced = count == 0;
                var marked = forced || _random.Chance(MarkerRate);
                if (marked)
                {
                    await EndSegmentAsync(key, durable: forced);
                }

                // A close just after a marker closes a segment which holds the marker alone.
                if (forced || _random.Next(marked ? 2 : 4) == 0)
                {
                    await CloseAsync(key);
                }
            }
        }

        /// <summary>Proposes <paramref name="count"/> rows of <paramref name="key"/> in one batch, and records each.</summary>
        private async Task ProposeAsync(string key, RowKind kind, bool empty = false, int count = 1)
        {
            var bytes = Encoding.UTF8.GetBytes(key);
            _ = _reference.TryAdd(key, []);
            string[] payloads = [.. Enumerable.Range(_reference[key].Count, count).Select(index => empty ? string.Empty : Invariant($"{key}-{index}"))];
            for (var attempt = 0; ; attempt++)
            {
                var ledger = _ledgers.OpenContaining(bytes);
                var calledAt = ++_steps;
                try
                {
                    var tags = await _ledgers.ProposeAsync(ledger.Id, [.. payloads.Select(payload => LiveRow.Of(key, kind, payload))], CancellationToken.None);
                    var answeredAt = ++_steps;
                    for (var index = 0; index < count; index++)
                    {
                        var row = new Decided(tags[index], kind, payloads[index]);
                        _reference[key].Add(row);
                        _proposals.Add(new Proposal(row, calledAt, answeredAt));
                    }

                    return;
                }
                catch (InvalidOperationException) when (attempt < Attempts)
                {
                    // A transition whose outcome was unknown left the successor's writer to the registry's reconciliation.
                    await _ledgers.ReconcileAsync(CancellationToken.None);
                }
            }
        }

        /// <summary>
        /// Ends the open segment of the open ledger containing <paramref name="key"/> with a marker, as a failure-triggered
        /// drain does, so a later marker, or the ledger's close, closes a segment which holds the marker alone; when
        /// <paramref name="durable"/>, it ends the segment again after each marker whose outcome was not durable.
        /// </summary>
        private async Task EndSegmentAsync(string key, bool durable = false)
        {
            for (var attempt = 0; ; attempt++)
            {
                var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
                try
                {
                    _ = await _ledgers.EndSegmentAsync(ledger.Id, CancellationToken.None);
                    return;
                }
                catch (InvalidOperationException)
                {
                    // The marker's outcome was not durable, or the writer no longer serves the ledger: a transition's reconciliation is due.
                    await _ledgers.ReconcileAsync(CancellationToken.None);
                }

                if (!durable)
                {
                    return;
                }

                if (attempt == Attempts)
                {
                    Counts.Violations.Add(Invariant($"No marker of ledger {ledger.Id} was durable in {Attempts + 1} attempts."));
                    return;
                }
            }
        }

        /// <summary>Replaces or splits the open ledger containing <paramref name="key"/>.</summary>
        private async Task CloseAsync(string key)
        {
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            var split = ledger.Range.Depth < 2 && _random.Next(2) == 0;
            await TransitionAsync(ledger.Id, () => split ? _ledgers.SplitAsync(ledger.Id, CancellationToken.None) : _ledgers.ReplaceAsync(ledger.Id, CancellationToken.None));
        }

        /// <summary>
        /// Executes a transition until the writer's view shows it applied: a commit whose outcome is unknown is retried,
        /// which answers applied once the first attempt landed (ledgers.md §8.1).
        /// </summary>
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

        /// <summary>
        /// Drains, in passes, every ledger the drainer's view lists, refreshing the view only now and then, so its ledgers'
        /// metadata lags their transitions and drops.
        /// </summary>
        private async Task DrainAsync(Drainer drainer)
        {
            for (var pass = 0; pass < Passes; pass++)
            {
                await DelayAsync(6);
                if (_random.Chance(0.6))
                {
                    _ = await drainer.View.ReadAsync(CancellationToken.None);
                }

                foreach (var ledger in drainer.View.Current.Ledgers)
                {
                    _ = await DrainLedgerAsync(drainer, ledger, toEnd: false);
                }
            }
        }

        /// <summary>
        /// Drains <paramref name="ledger"/>'s closed segments below its end, a closed ledger's <c>T</c> or the contiguous
        /// tail the drainer's reader learns of an open one, or now and then short of it, one segment a drain, until a drain
        /// finds its segment open or nothing left to drain.
        /// </summary>
        /// <returns>Whether the drains completed the ledger.</returns>
        private async Task<bool> DrainLedgerAsync(Drainer drainer, LedgerMetadata ledger, bool toEnd)
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
                return false;
            }

            var below = !toEnd && _random.Chance(0.3) ? new Slot(_random.Next((int)end.Value + 1)) : end;
            for (var drain = 0; drain < MaxDrainsPerLedger; drain++)
            {
                drainer.Submitted = null;
                DrainOutcome outcome;
                try
                {
                    outcome = await drainer.Worker.DrainAsync(ledger, below, (change, token) => Commit(drainer, change, token));
                }
                catch (ArchiveCorruptException exception)
                {
                    Counts.Violations.Add(Invariant($"A drain of ledger {ledger.Id} found the archive corrupt: {exception.Message}"));
                    return false;
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    Counts.Add(Paths.DrainFailures);
                    await AfterFailureAsync(drainer);
                    return false;
                }

                switch (outcome.Value)
                {
                    case DrainOutcome.Drained drained:
                        Decide(drainer, drained);
                        continue;
                    case DrainOutcome.OpenSegment open:
                        Counts.Add(Paths.OpenSegments);
                        _openAnswers.Add(new OpenAnswer(ledger.Id, open.From, below));
                        return false;
                    case DrainOutcome.Covered:
                        Counts.Add(Paths.Covered);
                        if (ledger.IsOpen && _ledgers.Map.Current.Get(ledger.Id) is not { IsOpen: true })
                        {
                            Counts.Add(Paths.CoveredFromALaggingView);
                        }

                        return !ledger.IsOpen && below == end;
                    case ReplicaWriteOutcome.Dropped:
                        Counts.Add(Paths.DrainDropped);
                        return false;
                    case ReplicaWriteOutcome.Backpressure:
                        Counts.Add(Paths.DrainBackpressure);
                        return false;
                    case null:
                    default:
                        throw new UnreachableException();
                }
            }

            return false;
        }

        /// <summary>
        /// Submits a drain's change through a committer chosen at random, losing its answer now and then once it completed.
        /// The worker calls it from the code under test, so it schedules and never awaits.
        /// </summary>
        private Task<ManifestCommitOutcome> Commit(Drainer drainer, ManifestChange change, CancellationToken cancellationToken)
        {
            var drain = Assert.IsType<ManifestChange.Drain>(change.Value);
            Submitted(change);
            if (_committedBy.ContainsKey(drain))
            {
                Counts.Add(Paths.Resubmitted);
            }

            var committer = _random.Next(_committers.Length);
            _committedBy[drain] = committer;
            drainer.Submitted = drain;
            var lose = _random.Chance(_lossRate);
            if (lose)
            {
                Counts.Add(Paths.LostAnswers);
            }

            return AnswerLoss.AfterCompletionAsync(_committers[committer].CommitAsync(change, cancellationToken), lose);
        }

        /// <summary>
        /// Records a decided drain's answer by its committer and epoch, so epochs which carried one drain and answered
        /// another's duplicate show; a superseded drain breaks LSM-I2, since two drains from one watermark take the same
        /// whole segment, and every watermark is a segment start.
        /// </summary>
        private void Decide(Drainer drainer, DrainOutcome.Drained drained)
        {
            Counts.Add(drainer.Options.GridBits == 1 ? Paths.GridBits1 : Paths.GridBits4);
            RecordDrain(_committedBy[drained.Change], drained, drainer.Restarted);
            drainer.Decided();
        }

        /// <summary>
        /// Records a decided drain's answer, its committer's, and its segment's split; <paramref name="afterRestart"/> when
        /// its worker restarted since an earlier drain of the segment.
        /// </summary>
        private void RecordDrain(int committer, DrainOutcome.Drained drained, bool afterRestart)
        {

            if (drained.Change.Sections.Length > 1)
            {
                Counts.Add(Paths.Splits);
            }

            switch (drained.Outcome.Value)
            {
                case ManifestCommitOutcome.Applied applied:
                    _appliedDrains.Add(new AppliedDrain(committer, applied.Epoch, drained.Change, afterRestart, Counts.Scripting));
                    if (drained.Change.Sections.IsEmpty && drained.Change.From < drained.Change.Below)
                    {
                        Counts.Add(Paths.MarkerSegments);
                    }

                    break;
                case ManifestCommitOutcome.Superseded superseded:
                    Counts.Violations.Add(Invariant($"{WholeSegmentCheck}a drain of ledger {drained.Change.Ledger} over [{drained.Change.From}, {drained.Change.Below}) was superseded at epoch {superseded.Epoch}."));
                    break;
                case null:
                default:
                    throw new UnreachableException();
            }
        }

        /// <summary>
        /// After a failed drain, the <c>drop-on-unknown</c> mutant drops a ledger whose completing change failed; and the
        /// worker restarts at random, as a crash between its commit and its recording of the outcome does.
        /// </summary>
        private async Task AfterFailureAsync(Drainer drainer)
        {
            if (_mutant == Mutant.DropOnUnknown && drainer.Submitted is { Complete: true } complete)
            {
                try
                {
                    _ = await SubmitDrop(new LedgerMapChange.Drop(complete.Ledger), CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    // The mutant's drop met the map register's unknown outcome; its check ran at submission.
                }
            }

            if (_random.Chance(0.5))
            {
                drainer.Restart();
                Counts.Add(Paths.Restarts);
            }
        }

        /// <summary>
        /// Submits a drop to the map's committer, checking LSM-I3 at its instant: the manifest durable then shows the ledger
        /// complete. The dropper calls it from the code under test, so it never awaits.
        /// </summary>
        private Task<LedgerMapCommitOutcome> SubmitDrop(LedgerMapChange change, CancellationToken cancellationToken)
        {
            var drop = Assert.IsType<LedgerMapChange.Drop>(change.Value);
            var writes = _objects.AppliedWritesOf(ObjectName.Manifest);
            var durable = writes.Count == 0 ? ManifestValue.Absent : Decode(writes[^1]);
            if (!durable.IsComplete(drop.Ledger))
            {
                Counts.Violations.Add(Invariant($"{DropCheck}ledger {drop.Ledger}'s drop was submitted while the durable manifest, at epoch {durable.Epoch}, shows it incomplete."));
            }

            return _ledgers.CommitAsync(change, cancellationToken);
        }

        /// <summary>
        /// Compacts, in passes, an overfull partition of a manifest value, chosen at random, so two compactors race over one
        /// partition, and commits the change through a committer chosen at random. The compactor reads the map and then the
        /// manifest, the manifest and then the map, or takes its process's lagging values of both.
        /// </summary>
        private async Task CompactAsync(Compactor compactor)
        {
            for (var pass = 0; pass < Passes; pass++)
            {
                await DelayAsync(6);
                ManifestValue manifest;
                LedgerMapValue map;
                try
                {
                    switch (_random.Next(3))
                    {
                        case 0:
                            map = await compactor.View.ReadAsync(CancellationToken.None);
                            manifest = (await compactor.Manifest.ReadAsync(CancellationToken.None)).Value;
                            break;
                        case 1:
                            manifest = (await compactor.Manifest.ReadAsync(CancellationToken.None)).Value;
                            map = await compactor.View.ReadAsync(CancellationToken.None);
                            break;
                        default:
                            manifest = (compactor.Manifest.Current ?? await compactor.Manifest.ReadAsync(CancellationToken.None)).Value;
                            map = compactor.View.Current;
                            break;
                    }
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    continue;
                }

                var overfull = CompactionWorker.OverfullPartitions(manifest, _options);
                if (!overfull.IsEmpty)
                {
                    _ = await CompactAndCommitAsync(compactor.Worker, _options, overfull[_random.Next(overfull.Length)], manifest, map);
                }
            }
        }

        /// <summary>
        /// Compacts <paramref name="partition"/> from <paramref name="manifest"/> and <paramref name="map"/>, as a mutant
        /// judges them, records the change and the empty Deletes it drops as their keys' bases, and commits it through a
        /// committer chosen at random, resubmitting it while its outcome is unknown when <paramref name="decide"/>.
        /// </summary>
        /// <returns>The commit's answer, or <see langword="null"/> when the compaction or its commit failed.</returns>
        private async Task<ManifestCommitOutcome?> CompactAndCommitAsync(
            CompactionWorker worker,
            ArchiveOptions options,
            KeyRange partition,
            ManifestValue manifest,
            LedgerMapValue map,
            bool decide = false)
        {
            var (judged, judgedMap) = Mutated(partition, manifest, map);
            CompactionOutcome outcome;
            try
            {
                outcome = await worker.CompactAsync(partition, judged, judgedMap, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                Counts.Add(Paths.CompactionFailures);
                return null;
            }
            catch (ArchiveCorruptException exception)
            {
                Counts.Violations.Add(Invariant($"A compaction found the store corrupt: {exception.Message}"));
                return null;
            }

            if (outcome.Value is not CompactionOutcome.Compacted { Change: var change })
            {
                // An input was gone, which the worker's fresh read no longer listed, and the fresh value holds no
                // compaction of the partition (archive §2.4).
                return null;
            }

            _uploaded.Add(change);
            var expiries = BaseExpiriesDropped(change);
            _droppedExpiries.UnionWith(expiries);
            var push = Assert.IsType<CompactionPlan>(CompactionWorker.Plan(partition, manifest.SectionsOf(partition), options)).IsPush;
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                ManifestCommitOutcome committed;
                try
                {
                    committed = await _committers[_random.Next(_committers.Length)].CommitAsync(change, CancellationToken.None);
                }
                catch (Exception exception) when (decide && exception is IOException or TimeoutException)
                {
                    continue;
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    return null;
                }

                switch (committed.Value)
                {
                    case ManifestCommitOutcome.Applied:
                        // An output-less compaction's Applied says only that its inputs are gone (archive §2.9).
                        Counts.Add(Paths.Compactions);
                        Counts.Add(push ? Paths.Pushes : Paths.MergesInPlace);
                        if (!change.Outputs.IsEmpty && RowsIn(change.Outputs).Length < RowsIn(change.Inputs).Length)
                        {
                            Counts.Add(Paths.CompactionDrops);
                        }

                        if (!change.Outputs.IsEmpty && expiries.Count > 0)
                        {
                            Counts.Add(Paths.ExpiriesDropped);
                        }

                        break;
                    case ManifestCommitOutcome.Superseded:
                        Counts.Add(Paths.CompactionsSuperseded);
                        break;
                    case null:
                    default:
                        throw new UnreachableException();
                }

                return committed;
            }

            Counts.Violations.Add(Invariant($"A compaction of {partition} never committed once the faults stopped."));
            return null;
        }

        /// <summary>
        /// Returns the manifest and map values the compactor judges by: those it read, or a mutant's (see <see cref="Mutant"/>).
        /// </summary>
        private (ManifestValue Manifest, LedgerMapValue Map) Mutated(KeyRange partition, ManifestValue manifest, LedgerMapValue map) => _mutant switch
        {
            Mutant.DropTrustingUnlisted => (Completing(manifest, map, static (id, map) => map.Get(id) is null), map),
            Mutant.NeverDrop => (manifest, LedgerMapValue.Empty),
            Mutant.None or Mutant.UncoveredDrop or Mutant.UncoveredExpiry or Mutant.DropOnUnknown
                or Mutant.DropIgnoringLive or Mutant.DropIgnoringSections
                or Mutant.SweepDrain or Mutant.SweepCompaction or Mutant.StaleDrain
                or Mutant.IgnoreMissing or Mutant.HoldByWatermark or Mutant.SupersedeByOpen or Mutant.NoPredatesCheck
                or Mutant.AcceptsChangedContent or Mutant.MidSegmentWatermark => (manifest, map),
            _ => throw new UnreachableException(),
        };

        /// <summary>Returns <paramref name="manifest"/> with every ledger below the map's next ID which <paramref name="complete"/> picks shown complete.</summary>
        private static ManifestValue Completing(ManifestValue manifest, LedgerMapValue map, Func<LedgerId, LedgerMapValue, bool> complete)
        {
            var watermarks = manifest.Watermarks.ToDictionary();
            for (var id = manifest.DrainedBelow.Value; id < map.NextLedgerId.Value; id++)
            {
                var ledger = new LedgerId(id);
                if (!manifest.IsComplete(ledger) && complete(ledger, map))
                {
                    watermarks[ledger] = new ManifestWatermark(new Slot(Math.Max(1, manifest.RecordedWatermark(ledger).Value)), Complete: true);
                }
            }

            var drainedBelow = manifest.DrainedBelow;
            while (watermarks.Remove(drainedBelow, out var watermark) && watermark.Complete)
            {
                drainedBelow = new LedgerId(drainedBelow.Value + 1);
            }

            if (manifest.Watermarks.TryGetValue(drainedBelow, out var kept))
            {
                watermarks[drainedBelow] = kept;
            }

            return new ManifestValue(manifest.Epoch, drainedBelow, watermarks, manifest.Sections);
        }

        /// <summary>
        /// Returns the tags of the empty Deletes <paramref name="change"/> dropped as their keys' last <c>Replace</c> or
        /// <c>Delete</c> among its inputs.
        /// </summary>
        private HashSet<Tag> BaseExpiriesDropped(ManifestChange.Compaction change)
        {
            var kept = RowsIn(change.Outputs).Select(static row => row.Tag).ToHashSet();
            return
            [
                .. RowsIn(change.Inputs)
                    .GroupBy(static row => row.Key, StringComparer.Ordinal)
                    .Select(static rows => rows.Where(static row => row.Kind is RowKind.Replace or RowKind.Delete).MaxBy(static row => row.Tag))
                    .Where(row => row is { Kind: RowKind.Delete, Payload: "" } && !kept.Contains(row.Tag))
                    .Select(static row => row!.Tag),
            ];
        }

        /// <summary>Returns the rows the sections hold, from their objects as they landed, each with its key.</summary>
        private SectionRow[] RowsIn(IEnumerable<SectionRef> sections) => [.. sections.SelectMany(SectionRowsOf)];

        private SectionRow[] SectionRowsOf(SectionRef section)
        {
            if (_sectionRows.TryGetValue(section, out var cached))
            {
                return cached;
            }

            using var content = ArcBuffer.Wrap(_objects.AppliedWritesOf(section.Object)[0]);
            var decoding = RunObjectFormat.Decode(content).Value;
            if (decoding is InvalidDataException rejection)
            {
                // Only a store which kept changed content holds an object which fails its own checks.
                Counts.Violations.Add(Invariant($"{ManifestCheck}a manifest lists {section.Object}, which fails its checks: {rejection.Message}"));
                _sectionRows.Add(section, []);
                return [];
            }

            using var decoded = Assert.IsType<RunObject>(decoding);
            SectionRow[] rows =
            [
                .. decoded.Sections.Single(read => read.Metadata.Entry.Offset == section.Offset).Rows.Rows.ToArray().Select(static row =>
                    new SectionRow(Encoding.UTF8.GetString(row.Row.Key.Span), row.Tag, row.Row.Kind, Encoding.UTF8.GetString(row.Row.Body.AsReadOnlySequence()))),
            ];
            _sectionRows.Add(section, rows);
            return rows;
        }

        /// <summary>
        /// Whether the manifest value at <paramref name="epoch"/> holds no section with <paramref name="key"/>'s row
        /// <paramref name="tag"/>, while one at a lower epoch does: the row drained, and then compacted away, by that epoch.
        /// </summary>
        private bool ExpiredBy(string key, Tag tag, ulong epoch)
        {
            var bytes = Encoding.UTF8.GetBytes(key);
            bool Holds(ManifestValue value) => value.SectionsContaining(bytes).SelectMany(SectionRowsOf).Any(row => row.Tag == tag);
            return !Holds(ManifestAt(epoch)) && _objects.AppliedWritesOf(ObjectName.Manifest).Select(Decode).Any(value => value.Epoch < epoch && Holds(value));
        }

        /// <summary>Returns the manifest value the store's applied writes hold at <paramref name="epoch"/>.</summary>
        private ManifestValue ManifestAt(ulong epoch) =>
            epoch == 0 ? ManifestValue.Absent : _objects.AppliedWritesOf(ObjectName.Manifest).Select(Decode).First(value => value.Epoch == epoch);
        private async Task DropAsync()
        {
            for (var pass = 0; pass < Passes; pass++)
            {
                await DelayAsync(8);
                await DropOnceAsync();
            }
        }

        /// <summary>
        /// Refreshes the dropper's map view and evidence, drops every complete ledger it allows, and has the node's reconciliation drop
        /// the replicas its view revokes (ledgers.md §8.5).
        /// </summary>
        private async Task DropOnceAsync()
        {
            _ = await DropperView.ReadAsync(CancellationToken.None);
            try
            {
                _ = await _evidence.ReadAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                // The dropper decides from the evidence it holds.
            }

            // The oracle's decided starts of a ledger the dropper may drop, which no scan reads once its replicas go.
            foreach (var ledger in DropperView.Current.Ledgers.Where(static ledger => !ledger.IsOpen))
            {
                await RecordDecidedStartsAsync(ledger, ((LedgerStatus.Closed)ledger.Status.Value).T);
            }

            try
            {
                Counts.Add(Paths.Drops, (await Dropper.DropCompleteAsync(SubmitDrop, CancellationToken.None)).Length);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                Counts.Add(Paths.DropperFailures);
            }

            _ = await NodeView.ReadAsync(CancellationToken.None);
            foreach (var reconciler in _ledgers.Reconcilers(NodeView))
            {
                Counts.Add(Paths.ReplicaDrops, (await reconciler.ReconcileAsync(CancellationToken.None)).Count);
            }
        }

        /// <summary>
        /// Sweeps in passes by one sweeper process, while the other's passes run alongside, restarting it now and then,
        /// which loses its marks, so its next completed sweep deletes nothing. The other mutants' runs, whose checks sweeps
        /// do not serve, sweep nothing.
        /// </summary>
        private async Task SweepAsync(SweeperProcess process)
        {
            if (!Sweeps)
            {
                return;
            }

            for (var pass = 0; pass < Passes; pass++)
            {
                await Task.Delay(_sweepRandom.NextTimeSpan(TimeSpan.FromMilliseconds(7)), _environment.TimeProvider);
                _ = await SweepOnceAsync(process);
                if (_sweepRandom.Chance(0.25))
                {
                    process.Restart();
                    Counts.Add(Paths.SweeperRestarts);
                }
            }
        }

        /// <summary>
        /// Sweeps once, answering whether the sweep completed: it fails when a request does. The first completed sweep
        /// after a restart deletes nothing, as its sweeper holds no marks; an unreadable object is a violation, as every
        /// object here is written whole.
        /// </summary>
        private async Task<bool> SweepOnceAsync(SweeperProcess process)
        {
            SweepResult result;
            try
            {
                result = await process.Sweeper.SweepAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                Counts.Add(Paths.SweepFailures);
                return false;
            }

            Counts.Add(Paths.Marked, result.Marked);
            if (result.Pending > 0)
            {
                Counts.Add(Paths.KeptInFlight);
            }

            if (result.Unreadable > 0)
            {
                Counts.Violations.Add(Invariant($"{CorruptionCheck}a sweep found {result.Unreadable} objects unreadable."));
            }

            if (process.Restarted && result.Deleted > 0)
            {
                Counts.Violations.Add(Invariant($"{GraceCheck}the first sweep after a restart deleted {result.Deleted} objects."));
            }

            process.Restarted = false;
            return true;
        }

        /// <summary>Sweeps until a sweep completes, as its sweeper's next cadence would.</summary>
        private async Task SweepToCompletionAsync(SweeperProcess process)
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                if (await SweepOnceAsync(process))
                {
                    return;
                }
            }

            Counts.Violations.Add("No sweep completed.");
        }

        /// <summary>
        /// Requires the store to hold, once the final sweeps ran, only objects the durable manifest references or whose
        /// reference condition holds in it: no garbage is left.
        /// </summary>
        private async Task VerifyNoGarbageAsync()
        {
            var writes = _objects.AppliedWritesOf(ObjectName.Manifest);
            var durable = writes.Count == 0 ? ManifestValue.Absent : Decode(writes[^1]);
            var reference = ReferenceManifest.Of(durable);
            await foreach (var listed in _objects.ListAsync(string.Empty, cancellationToken: CancellationToken.None))
            {
                if (listed.Name != ObjectName.Manifest && !References(durable, listed.Name) && Deletable(reference, listed.Name) is not null)
                {
                    Counts.Violations.Add(Invariant($"{GarbageCheck}the final sweeps left {listed.Name}."));
                }
            }
        }

        /// <summary>
        /// Runs <see cref="SweepAcrossACommitAsync"/> with the store's faults and the replicas' backpressure off, until its
        /// variant's path is taken, so the <c>sweep-drain</c> mutant's deletion of a drain upload in flight is reached by
        /// construction on every seed.
        /// </summary>
        private async Task HoldCommitsAsync(bool duplicated)
        {
            var faults = (_objects.UnknownOutcomeRate, _objects.RetriedAnswerRate, _objects.ReadFailureRate, SweepStore.Rate);
            var saturated = _ledgers.Transport.Saturated;
            (_objects.UnknownOutcomeRate, _objects.RetriedAnswerRate, _objects.ReadFailureRate, SweepStore.Rate) = (0, 0, 0, 0);
            _ledgers.Transport.Saturated = static (_, _) => false;
            try
            {
                for (var attempt = 0; attempt < Attempts; attempt++)
                {
                    if (await SweepAcrossACommitAsync(duplicated))
                    {
                        return;
                    }
                }

                Counts.Violations.Add(Invariant($"No held commit took its path in {Attempts} attempts."));
            }
            finally
            {
                (_objects.UnknownOutcomeRate, _objects.RetriedAnswerRate, _objects.ReadFailureRate, SweepStore.Rate) = faults;
                _ledgers.Transport.Saturated = saturated;
            }
        }

        /// <summary>
        /// A drain uploads the segment holding a new row and submits its change, which the scenario holds while one sweeper
        /// sweeps twice, then submits to a committer: the upload is unreferenced throughout and its reference condition
        /// holds, so neither sweep marks it, while the <c>sweep-drain</c> mutant deletes it before the commit
        /// references it. With <paramref name="duplicated"/>, a competing drain of the same segment commits first, so the
        /// held upload's watermark passes while the ledger stays open: the first sweep marks it, the second deletes it, and
        /// its commit then finds the competing drain's goal.
        /// </summary>
        /// <returns>Whether the variant's path was taken: the held commit applied, or the competing drain's did.</returns>
        private async Task<bool> SweepAcrossACommitAsync(bool duplicated)
        {
            var key = _sweepRandom.Choose(Keys);
            await ProposeAsync(key, RowKind.Append);
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            Slot tail;
            try
            {
                tail = await DrainToTheRowsSegmentAsync(key, ledger) + 1;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
            {
                // The ledger closed, a drain below the segment did not reach it, or a drain's upload of changed content
                // was refused, its answer lost.
                return false;
            }

            // A worker of the writer's process, whose reader knows the tail the marker raised, drains the new row's segment
            // from the manifest's watermark.
            var worker = NewWorker();
            var held = new HeldCall<ManifestChange, ManifestCommitOutcome>();

            // The worker calls the delegate from the code under test, so it only hands the change to the held call.
            var drain = worker.DrainAsync(ledger, tail, (change, _) => held.CallAsync(change));
            var competed = false;
            if (await held.WaitAsync(drain))
            {
                Submitted(await held.Submitted);
                if (duplicated)
                {
                    try
                    {
                        var competing = await NewWorker()
                            .DrainAsync(ledger, tail, CommitCompeting);
                        competed = competing.Value is DrainOutcome.Drained { Outcome.Value: ManifestCommitOutcome.Applied };
                    }
                    catch (Exception exception) when (exception is IOException or TimeoutException)
                    {
                        // The competing drain failed; the held upload's condition may still hold.
                    }
                }

                await SweepToCompletionAsync(_sweepers[0]);
                await SweepToCompletionAsync(_sweepers[0]);
                await held.AnswerAsync(_committers[0].CommitAsync(await held.Submitted, CancellationToken.None));
            }

            DrainOutcome outcome;
            try
            {
                outcome = await drain;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                // The upload or the held commit failed; a later drain of the ledger decides it.
                return false;
            }

            if (duplicated)
            {
                if (competed)
                {
                    Counts.Add(Paths.HeldDuplicated);
                }

                return competed;
            }

            var applied = outcome.Value is DrainOutcome.Drained { Outcome.Value: ManifestCommitOutcome.Applied };
            if (applied)
            {
                Counts.Add(Paths.HeldInFlight);
            }

            return applied;
        }

        /// <summary>
        /// Submits the competing drain's change to the second committer. The worker calls it from the code under test, so it
        /// schedules and never awaits.
        /// </summary>
        private Task<ManifestCommitOutcome> CommitCompeting(ManifestChange change, CancellationToken cancellationToken)
        {
            Submitted(change);
            return _committers[1].CommitAsync(change, cancellationToken);
        }

        /// <summary>Reads random keys' histories through the process's history reader, and now and then its store directly.</summary>
        private async Task ReadAsync(ReaderProcess process)
        {
            for (var count = 0; count < ReadsPerRound; count++)
            {
                await DelayAsync(5);
                var key = _random.Choose(Keys);
                if (_random.Chance(0.3))
                {
                    var map = _random.Next(3) switch
                    {
                        0 => process.Held,
                        1 => process.View.Current,
                        _ => NodeView.Current,
                    };

                    await ReadStoreAsync(process, key, map);
                }
                else
                {
                    await ReadHistoryAsync(process, key);
                }
            }
        }

        /// <summary>
        /// Reads the store with <paramref name="map"/>, a value one of the process's readers holds: one taken at the start of
        /// the round, the process's current one, or a newer one another reader learned, so the store's view runs ahead of
        /// some of them. On <c>Stale</c>, it refreshes the process's view and reads with its value.
        /// </summary>
        /// <summary>
        /// Reads <paramref name="key"/> from the process's store with <paramref name="map"/>, or, under a mutant, with
        /// <paramref name="storeMap"/>, and records the answer against <paramref name="map"/>.
        /// </summary>
        private async Task ReadStoreAsync(ReaderProcess process, string key, LedgerMapValue map, LedgerMapValue? storeMap = null)
        {
            var bytes = Encoding.UTF8.GetBytes(key);
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                ArchiveStoreRead answer;
                try
                {
                    answer = await process.Store.ReadAsync(bytes, storeMap ?? map, CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    continue;
                }
                catch (ArchiveCorruptException exception)
                {
                    Counts.Violations.Add(Invariant($"{StoreOracle}{process.Name}'s store read of {key} found the store corrupt: {exception.Message}"));
                    return;
                }

                switch (answer.Value)
                {
                    case ArchiveStoreRead.Stale:
                        Counts.Add(Paths.StoreStale);
                        map = await process.View.ReadAsync(CancellationToken.None);
                        storeMap = null;
                        continue;
                    case ArchiveStoreRead.Absent:
                        _storeReads.Add(new StoreReadRecord(process.Name, key, map, null, process.Store.ViewEpoch));
                        return;
                    case ArchiveStoreRead.Read read:
                        using (read)
                        {
                            _storeReads.Add(new StoreReadRecord(process.Name, key, map, ArchiveFold.Of(read.Prefix), process.Store.ViewEpoch) { VersionBase = read.Prefix.VersionBase });
                        }

                        return;
                    case null:
                    default:
                        throw new UnreachableException();
                }
            }
        }

        /// <summary>
        /// The chain checker and the version checker after every step (verification.md §3.2): each read since the last
        /// step, whose rows the chain checker judged as it answered, and the version base of each read and store answer
        /// since, which names one state of its key, so a rebase to it is fresh (HI-I4).
        /// </summary>
        private string? CheckChainsAndVersions()
        {
            for (; _readsChecked < _reads.Count; _readsChecked++)
            {
                var read = _reads[_readsChecked];
                if (read.Chain is { } chain)
                {
                    return Invariant($"{read.Reader}'s read of {read.Key}: the chain checker found {chain}");
                }

                if (read.VersionBase is var (versionBase, state) && _versions.State(Encoding.UTF8.GetBytes(read.Key), versionBase, state) is { } repeated)
                {
                    return Invariant($"HI-I4: {read.Reader}'s read of {read.Key}: {repeated}");
                }
            }

            for (; _storeReadsChecked < _storeReads.Count; _storeReadsChecked++)
            {
                var read = _storeReads[_storeReadsChecked];
                if (read.VersionBase is { } versionBase && !HasClosedSupersederInMap(read) && _versions.State(Encoding.UTF8.GetBytes(read.Key), versionBase, read.Prefix!.ToString()) is { } repeated)
                {
                    return Invariant($"HI-I4: {read.Reader}'s store read of {read.Key}: {repeated}");
                }
            }

            return null;
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
                    var rows = RowsOf(read.Rows);
                    var hasClosedSuperseder = rows.Any(row => row.Kind is RowKind.Replace or RowKind.Delete && row.Tag.Ledger != read.OpenLedger);
                    _reads.Add(new HistoryReadRecord(process.Name, key, calledAt, ++_steps, rows, ArchiveOracle.Fold(read.Prefix, read.Rows), read.Through.ToDictionary(), read.Linearizable)
                    {
                        Chain = TokenChainChecker.Check(process.Store, read.Prefix?.Token ?? default, ChainRow.Of(read.Rows)),
                        VersionBase = read.Prefix is { } prefix && !hasClosedSuperseder ? (prefix.VersionBase, ArchiveFold.Of(prefix).ToString()) : null,
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
        /// A key's ledger gains a row which the reader process then reads, so its store's view predates the row's drain;
        /// the ledger closes, drains to its <c>T</c>, and drops. Then either the reader reads the key again with a map which
        /// no longer lists the ledger, which its store's view does not cover, and the <c>uncovered-drop</c> mutant misses the
        /// row; or, with <paramref name="staleMap"/>, a store read with a newer map advances the store's view past the
        /// ledger's completion, and the reader, whose map still lists the ledger open, reads <c>Stale</c> and again (H1).
        /// </summary>
        private async Task ReadAcrossADrainAndDropAsync(bool staleMap)
        {
            var key = _random.Choose(Keys);
            var reader = _readers[1];
            await ProposeAsync(key, RowKind.Append);
            _ = await reader.View.ReadAsync(CancellationToken.None);
            await ReadHistoryAsync(reader, key);
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key)).Id;
            await CloseAsync(key);
            if (!await CompleteAndDropAsync(ledger))
            {
                Counts.Violations.Add(Invariant($"Ledger {ledger} never dropped in the read-across-a-drop schedule."));
            }

            if (staleMap)
            {
                await ReadStoreAsync(reader, key, NodeView.Current);
            }
            else
            {
                _ = await reader.View.ReadAsync(CancellationToken.None);
            }

            await ReadHistoryAsync(reader, key);
        }

        /// <summary>
        /// Completes and drops every closed ledger the writer's view lists, until <paramref name="ledger"/> drops: in passes
        /// under the faults, and then, if it has not dropped, in passes with every fault off. A schedule which reads across
        /// the drop needs it, and under the faults alone no number of passes assures it: a drain which fails restarts its
        /// drainer half the time, from the process's manifest view, which may predate many of its commits, so a lower
        /// ledger the drop waits on (LG-I10) may never complete. With the faults off, every drain applies.
        /// </summary>
        private async Task<bool> CompleteAndDropAsync(LedgerId ledger) =>
            await DropPassesAsync(ledger) || await WithoutFaultsAsync(() => DropPassesAsync(ledger));

        private async Task<bool> DropPassesAsync(LedgerId ledger)
        {
            for (var pass = 0; pass < Attempts; pass++)
            {
                await CompleteClosedAsync();
                await DropOnceAsync();
                if ((await _ledgers.Map.ReadAsync(CancellationToken.None)).Get(ledger) is null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Runs a scripted step with every fault off, as the final phase runs: the store's, the map register's, lost commit
        /// answers, the sweeps', and the replicas' backpressure; then restores them, so the random rounds go on drawing the
        /// faults.
        /// </summary>
        private async Task<T> WithoutFaultsAsync<T>(Func<Task<T>> step)
        {
            var store = (_objects.UnknownOutcomeRate, _objects.RetriedAnswerRate, _objects.ReadFailureRate, _objects.ChangedContentRate);
            var (register, loss, sweeps, saturated) = (_register.UnknownOutcomeRate, _lossRate, SweepStore.Rate, _ledgers.Transport.Saturated);
            (_objects.UnknownOutcomeRate, _objects.RetriedAnswerRate, _objects.ReadFailureRate, _objects.ChangedContentRate) = (0, 0, 0, 0);
            (_register.UnknownOutcomeRate, _lossRate, SweepStore.Rate) = (0, 0, 0);
            _ledgers.Transport.Saturated = static (_, _) => false;
            try
            {
                return await step();
            }
            finally
            {
                (_objects.UnknownOutcomeRate, _objects.RetriedAnswerRate, _objects.ReadFailureRate, _objects.ChangedContentRate) = store;
                (_register.UnknownOutcomeRate, _lossRate, SweepStore.Rate) = (register, loss, sweeps);
                _ledgers.Transport.Saturated = saturated;
            }
        }

        private async Task CompleteClosedAsync()
        {
            foreach (var ledger in (await _ledgers.Map.ReadAsync(CancellationToken.None)).Ledgers.Where(static ledger => !ledger.IsOpen))
            {
                _ = await DrainLedgerAsync(_drainers[0], ledger, toEnd: true);
            }
        }

        private async Task CompleteAndDropClosedAsync()
        {
            for (var pass = 0; pass < 10 && _ledgers.Map.Current.Ledgers.Any(static ledger => !ledger.IsOpen); pass++)
            {
                await CompleteClosedAsync();
                await DropOnceAsync();
                _ = await _ledgers.Map.ReadAsync(CancellationToken.None);
            }
        }

        /// <summary>
        /// The positive drop path, every seed: a fresh key's row, its empty Delete, and a later row, in a ledger whose lower
        /// ledgers are all dropped and which then closes and completes, so the Delete may supersede (LSM-I5), compact with
        /// nothing older outside, so the Delete drops; a store of a fresh process then answers the later row alone, as the
        /// store oracle accepts only for a recorded drop.
        /// </summary>
        private async Task ExpireAsync()
        {
            const string key = "x-expire";
            var partition = await DrainEachAsync(key, (key, RowKind.Append), (key, RowKind.Delete), (key, RowKind.Append));
            await CloseAndCompleteAsync(key);
            _ = await CompactWholeAsync(partition);
            var fresh = _readers[0] with { Store = NewStore() };
            await ReadStoreAsync(fresh, key, await NodeView.ReadAsync(CancellationToken.None));
            await ReadHistoryAsync(_readers[0], key);
        }

        /// <summary>
        /// Every seed: a fresh key's row stays undrained in a closed ledger while its successor's empty Delete of the key,
        /// whose ledger closes and completes, compacts, so the Delete stays; <c>drop-ignoring-live</c> drops it, and once
        /// both ledgers drop, the history read resurrects the row.
        /// </summary>
        private async Task ExpireOverAnUndrainedRowAsync()
        {
            const string key = "x-live";
            await ProposeAsync(key, RowKind.Append);
            await ReplaceOpenAsync(key);
            var partition = await DrainEachAsync(key, (key, RowKind.Append), (key, RowKind.Delete), (key, RowKind.Append));
            await CloseAndCompleteAsync(key);
            _ = await CompactWholeAsync(partition);
            await DropTheKeysLedgersAsync(key);
            await ReadHistoryAsync(_readers[0], key);
        }

        /// <summary>
        /// Every seed: a fresh key's drained row is pushed below the partition, and then its successor's empty Delete of the
        /// key, whose ledger closes and completes, compacts there, so the Delete stays; <c>drop-ignoring-sections</c> drops
        /// it, and once its ledgers drop, the history read answers the pushed row.
        /// </summary>
        private async Task ExpireOverAnOlderSectionAsync()
        {
            const string key = "x-sections";
            await ProposeAsync(key, RowKind.Append);
            var ledger = await ReplaceOpenAsync(key);
            _ = await _drainers[0].View.ReadAsync(CancellationToken.None);
            _ = await DrainLedgerAsync(_drainers[0], _ledgers.Ledger(ledger.Id), toEnd: true);
            var partition = await DrainEachAsync(key, ("x-filler", RowKind.Append), ("x-filler", RowKind.Append));
            _ = await CompactWholeAsync(partition);
            _ = await DrainEachAsync(key, (key, RowKind.Append), (key, RowKind.Delete), (key, RowKind.Append));
            await CloseAndCompleteAsync(key);
            _ = await CompactWholeAsync(partition);
            await DropTheKeysLedgersAsync(key);
            await ReadHistoryAsync(_readers[0], key);
        }

        /// <summary>
        /// Every seed: a fresh key's row stays undrained in a closed ledger while its successor drains the key's empty
        /// Delete and closes and completes; the closed ledger then completes and drops, and a compaction from the manifest
        /// value read before, with the map after, keeps the Delete, since the map no longer gives the ledger's range;
        /// <c>drop-trusting-unlisted</c> drops it, and once the successor drops too, the history read answers the first
        /// ledger's row.
        /// </summary>
        private async Task ExpireOverADroppedLedgerAsync()
        {
            const string key = "x-unlisted";
            await ProposeAsync(key, RowKind.Append);
            var ledger = await ReplaceOpenAsync(key);
            var partition = await DrainEachAsync(key, (key, RowKind.Append), (key, RowKind.Delete), (key, RowKind.Append));
            await CloseAndCompleteAsync(key);
            var lagging = (await _evidence.ReadAsync(CancellationToken.None)).Value;
            if (!await CompleteAndDropAsync(ledger.Id))
            {
                Counts.Violations.Add(Invariant($"Ledger {ledger.Id} never dropped in the dropped-ledger expiry schedule."));
            }

            _ = await CompactWholeAsync(partition, lagging);
            await DropTheKeysLedgersAsync(key);
            await ReadHistoryAsync(_readers[0], key);
        }

        /// <summary>
        /// Closes the open ledger containing <paramref name="key"/>, and completes and drops it with every closed ledger
        /// below it, so a reader takes the key's compacted rows from the store's prefix rather than scanning them live
        /// (archive-lsm.md §2.8): an expiry schedule's history read then meets what its compaction kept.
        /// </summary>
        private async Task DropTheKeysLedgersAsync(string key)
        {
            var closed = await ReplaceOpenAsync(key);
            if (!await CompleteAndDropAsync(closed.Id))
            {
                Counts.Violations.Add(Invariant($"Ledger {closed.Id} never dropped in {key}'s expiry schedule."));
            }
        }

        /// <summary>
        /// Every seed: a store reads a fresh key whose one row, an empty Delete, is undrained in a closed ledger; the ledger
        /// drains and drops, and the Delete compacts away; the same store then reads the key with the map after, so its view
        /// advances past the compaction and answers no rows, and a read with the map from before the ledger closed, which
        /// lists it open, answers Stale. <c>uncovered-expiry</c> passes the second read the map value of the
        /// first, so the store answers from its view from before the Delete drained, which the store oracle flags.
        /// </summary>
        private async Task ExpireUnderAnOlderViewAsync()
        {
            const string key = "x-uncovered";
            var open = await NodeView.ReadAsync(CancellationToken.None);
            await ProposeAsync(key, RowKind.Delete, empty: true);
            var ledger = await ReplaceOpenAsync(key);
            var process = _readers[0] with { Store = NewStore() };
            var before = await NodeView.ReadAsync(CancellationToken.None);
            await ReadStoreAsync(process, key, before);
            _ = await _drainers[0].View.ReadAsync(CancellationToken.None);
            _ = await DrainLedgerAsync(_drainers[0], _ledgers.Ledger(ledger.Id), toEnd: true);
            if (!await CompleteAndDropAsync(ledger.Id))
            {
                Counts.Violations.Add(Invariant($"Ledger {ledger.Id} never dropped in the older-view expiry schedule."));
            }

            var partition = await DrainEachAsync(key, ("x-filler", RowKind.Append), ("x-filler", RowKind.Append));
            _ = await CompactWholeAsync(partition);
            var after = await NodeView.ReadAsync(CancellationToken.None);
            await ReadStoreAsync(process, key, after, _mutant == Mutant.UncoveredExpiry ? before : after);

            // A map value from before the ledger closed lists it open, which the view shows complete: Stale.
            await ReadStoreAsync(process, key, open);
        }

        /// <summary>
        /// Closes the open ledger containing <paramref name="key"/> and drains it to its <c>T</c>, completing it without a
        /// drop, so its <c>Replace</c> and <c>Delete</c> rows may supersede older rows in a compaction (LSM-I5).
        /// </summary>
        private async Task CloseAndCompleteAsync(string key)
        {
            var closed = await ReplaceOpenAsync(key);
            _ = await _drainers[0].View.ReadAsync(CancellationToken.None);
            _ = await DrainLedgerAsync(_drainers[0], _ledgers.Ledger(closed.Id), toEnd: true);
        }

        /// <summary>Replaces the open ledger containing <paramref name="key"/> and returns the ledger it closed.</summary>
        private async Task<LedgerMetadata> ReplaceOpenAsync(string key)
        {
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            await TransitionAsync(ledger.Id, () => _ledgers.ReplaceAsync(ledger.Id, CancellationToken.None));
            return ledger;
        }

        /// <summary>
        /// Proposes each row to the open ledger containing <paramref name="key"/>, ending the ledger's open segment after
        /// each with a marker and draining it, in the writer's process, to the marker's slot, as a failure-triggered drain
        /// does, so each lands a section of its own, and returns the partition of those sections.
        /// </summary>
        private async Task<KeyRange> DrainEachAsync(string key, params (string Key, RowKind Kind)[] rows)
        {
            var worker = NewWorker();
            foreach (var (rowKey, kind) in rows)
            {
                await ProposeAsync(rowKey, kind, empty: kind == RowKind.Delete);
                var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
                var marker = await _ledgers.EndSegmentAsync(ledger.Id, CancellationToken.None);
                await DrainToMarkerAsync(worker, ledger, marker);
            }

            return DrainWorker.GridPartition(_ledgers.OpenContaining(Encoding.UTF8.GetBytes(key)).Range, _options.GridBits);
        }

        /// <summary>
        /// Drains <paramref name="ledger"/>'s closed segments to <paramref name="marker"/>'s slot through the first
        /// committer, as a failure-triggered drain does, and requires the drains to end there (archive §8.1).
        /// </summary>
        private async Task DrainToMarkerAsync(DrainWorker worker, LedgerMetadata ledger, Slot marker)
        {
            var (outcome, watermark) = await DrainUntilAsync(worker, ledger, marker);
            if (watermark < marker)
            {
                Counts.Violations.Add(Invariant($"A drain of ledger {ledger.Id} to its marker's slot {marker} answered {outcome}, its watermark at {watermark}."));
            }
            else if (outcome.Value is DrainOutcome.Drained { Change.Below: var below })
            {
                if (below != marker)
                {
                    Counts.Violations.Add(Invariant($"A drain of ledger {ledger.Id} to its marker's slot {marker} ended at {below}."));
                }

                Counts.Add(Paths.MarkerDrains);
            }
        }

        /// <summary>
        /// Drains <paramref name="ledger"/>'s closed segments below <paramref name="end"/>, a segment start, through the
        /// first committer, one segment a drain with the bound just past <paramref name="end"/>, as the composition's loop
        /// for a failure-triggered drain does (archive §8.1), recording each; returns the last answer and the watermark the
        /// manifest then records.
        /// </summary>
        private async Task<(DrainOutcome Outcome, Slot Watermark)> DrainUntilAsync(DrainWorker worker, LedgerMetadata ledger, Slot end)
        {
            DrainOutcome outcome = DrainOutcome.Covered.Instance;
            for (var drain = 0; drain < MaxSegmentsDrained; drain++)
            {
                outcome = await worker.DrainAsync(ledger, end + 1, Checked(_committers[0]), CancellationToken.None);
                if (outcome.Value is not DrainOutcome.Drained drained)
                {
                    break;
                }

                RecordDrain(0, drained, afterRestart: false);
                if (drained.Outcome.Value is not ManifestCommitOutcome.Applied || drained.Change.Below >= end)
                {
                    break;
                }
            }

            var manifest = (await _evidence.ReadAsync(CancellationToken.None)).Value;
            return (outcome, manifest.IsComplete(ledger.Id) ? end : manifest.RecordedWatermark(ledger.Id));
        }

        /// <summary>
        /// Ends the open segment of <paramref name="ledger"/>, which holds <paramref name="key"/>'s last row, with a marker,
        /// and drains, through the first committer, every closed segment below the one holding the row, which the decided
        /// ledger's starts give, so the next drain of the ledger takes the row's segment; returns the marker's slot.
        /// </summary>
        /// <exception cref="InvalidOperationException">The marker was not durable, or the drains below the row's segment did not reach it.</exception>
        private async Task<Slot> DrainToTheRowsSegmentAsync(string key, LedgerMetadata ledger)
        {
            var row = _reference[key][^1].Tag;
            var marker = await _ledgers.EndSegmentAsync(ledger.Id, CancellationToken.None);
            var starts = await _ledgers.DecidedStartsAsync(ledger, marker + 1, CancellationToken.None)
                ?? throw new InvalidOperationException(Invariant($"Ledger {ledger.Id} dropped."));
            var start = starts.Last(start => start <= row.Slot);
            var (outcome, watermark) = await DrainUntilAsync(NewWorker(), ledger, start);
            return watermark >= start ? marker : throw new InvalidOperationException(Invariant($"A drain of ledger {ledger.Id} to its segment start {start} answered {outcome}, its watermark at {watermark}."));
        }
        /// <summary>
        /// Every seed: one dropper pass over a complete closed ledger whose map write's request is lost fails, so the
        /// corpus takes the dropper's failure and the map's unknown outcomes whatever the random rounds drew; once the
        /// faults stop, the closed ledgers drop.
        /// </summary>
        private async Task DropAcrossALostMapWriteAsync()
        {
            const string key = "x-unknown";
            await ProposeAsync(key, RowKind.Append);
            _ = await ReplaceOpenAsync(key);
            await CompleteClosedAsync();
            var failures = Counts.Scripted.GetValueOrDefault(Paths.DropperFailures);
            _register.LoseNextWrites = 1;
            await DropOnceAsync();
            if (_register.LoseNextWrites != 0 || Counts.Scripted.GetValueOrDefault(Paths.DropperFailures) == failures)
            {
                Counts.Violations.Add("A dropper pass whose map write's request was lost did not fail.");
            }

            _register.LoseNextWrites = 0;
            await CompleteAndDropClosedAsync();
        }

        /// <summary>
        /// Every seed: a segment holding two rows, in one batch, drains past an object bound which holds one row, so its
        /// rows split across two objects, which one change commits.
        /// </summary>
        private async Task DrainASegmentAcrossObjectsAsync()
        {
            const string key = "x-split";
            await ProposeAsync(key, RowKind.Append, count: 2);
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            var tail = await DrainToTheRowsSegmentAsync(key, ledger) + 1;
            var worker = NewWorker(options: _options with { SectionBytes = 1 });
            var outcome = await worker.DrainAsync(ledger, tail, Checked(_committers[0]), CancellationToken.None);
            if (outcome.Value is DrainOutcome.Drained { Outcome.Value: ManifestCommitOutcome.Applied, Change.Sections.Length: >= 2 } drained)
            {
                RecordDrain(0, drained, afterRestart: false);
            }
            else
            {
                Counts.Violations.Add(Invariant($"A drain of a segment of two rows past an object bound of one row answered {outcome}."));
            }
        }

        /// <summary>
        /// Every seed: a sweep whose listing fails fails, so the corpus takes the sweeper's failure whatever the random
        /// rounds drew.
        /// </summary>
        private async Task SweepAcrossAFailedListingAsync()
        {
            var failures = Counts.Scripted.GetValueOrDefault(Paths.SweepFailures);
            if (await SweepOnceAsync(new SweeperProcess(new ListingFails(_objects))) || Counts.Scripted.GetValueOrDefault(Paths.SweepFailures) == failures)
            {
                Counts.Violations.Add("A sweep whose listing failed did not fail.");
            }
        }

        /// <summary>
        /// Every seed: a drain of an open ledger whose replicas all answer its whole-slot reads with backpressure answers
        /// <see cref="ReplicaWriteOutcome.Backpressure"/>, so the corpus takes the path whatever the random rounds drew; the
        /// next drains, once the replicas answer, take every closed segment.
        /// </summary>
        private async Task DrainAgainstBackpressureAsync()
        {
            const string key = "x-pressure";
            await ProposeAsync(key, RowKind.Append);
            var worker = NewWorker();
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            var tail = await _ledgers.EndSegmentAsync(ledger.Id, CancellationToken.None) + 1;
            var saturated = _ledgers.Transport.Saturated;
            _ledgers.Transport.Saturated = (id, _) => id == ledger.Id;
            DrainOutcome outcome;
            try
            {
                outcome = await worker.DrainAsync(ledger, tail, Checked(_committers[0]), CancellationToken.None);
            }
            finally
            {
                _ledgers.Transport.Saturated = saturated;
            }

            if (outcome.Value is ReplicaWriteOutcome.Backpressure)
            {
                Counts.Add(Paths.DrainBackpressure);
            }
            else
            {
                Counts.Violations.Add(Invariant($"A drain whose replicas all answered backpressure answered {outcome}."));
            }

            for (var drain = 0; drain < MaxDrainsPerLedger; drain++)
            {
                outcome = await worker.DrainAsync(ledger, tail, Checked(_committers[0]), CancellationToken.None);
                if (outcome.Value is not DrainOutcome.Drained drained)
                {
                    break;
                }

                RecordDrain(0, drained, afterRestart: false);
            }
        }

        /// <summary>
        /// Every seed: a drain whose commit applied and whose answer was lost fails, its outcome unknown, and the same
        /// worker's next drain submits its pending change again, which the committer answers <c>Applied</c>, so the corpus
        /// takes the resubmission whatever the random rounds drew.
        /// </summary>
        private async Task ResubmitADrainWhoseAnswerWasLostAsync()
        {
            const string key = "x-resubmit";
            await ProposeAsync(key, RowKind.Append);
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            var tail = await _ledgers.EndSegmentAsync(ledger.Id, CancellationToken.None) + 1;
            var worker = NewWorker();
            var submitted = new List<ManifestChange.Drain>();
            try
            {
                var answered = await worker.DrainAsync(ledger, tail, (change, token) => Commit(change, token, lose: true), CancellationToken.None);
                Counts.Violations.Add(Invariant($"A drain whose commit answer was lost answered {answered}."));
                return;
            }
            catch (IOException)
            {
                // The commit applied and its answer was lost: the worker keeps its change pending.
            }

            var again = await worker.DrainAsync(ledger, tail, (change, token) => Commit(change, token, lose: false), CancellationToken.None);
            if (again.Value is DrainOutcome.Drained { Outcome.Value: ManifestCommitOutcome.Applied } drained && submitted is [var first, var second] && first == second)
            {
                Counts.Add(Paths.Resubmitted);
                RecordDrain(0, drained, afterRestart: false);
            }
            else
            {
                Counts.Violations.Add(Invariant($"A drain after its lost answer submitted [{string.Join("; ", submitted)}] and answered {again}."));
            }

            Task<ManifestCommitOutcome> Commit(ManifestChange change, CancellationToken cancellationToken, bool lose)
            {
                submitted.Add(Assert.IsType<ManifestChange.Drain>(change.Value));
                Submitted(change);
                if (lose)
                {
                    Counts.Add(Paths.LostAnswers);
                }

                return AnswerLoss.AfterCompletionAsync(_committers[0].CommitAsync(change, cancellationToken), lose);
            }
        }

        /// <summary>
        /// Every seed: a drainer whose view still lists a ledger open drains it once the ledger has closed and completed, so
        /// its drain, bounded by the tail its reader learns, answers <c>Covered</c>, the archive holding every row below it,
        /// as a lagging view's drain does in the random rounds.
        /// </summary>
        private async Task DrainACompletedLedgerFromALaggingViewAsync()
        {
            const string key = "x-lagging";
            await ProposeAsync(key, RowKind.Append);
            var open = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            await CloseAsync(key);
            var completed = false;
            for (var pass = 0; pass < Attempts && !completed; pass++)
            {
                completed = (await _ledgers.Map.ReadAsync(CancellationToken.None)).Get(open.Id) is { IsOpen: false } closed
                    && await DrainLedgerAsync(_drainers[0], closed, toEnd: true);
            }

            if (!completed)
            {
                Counts.Violations.Add(Invariant($"Ledger {open.Id} never completed in the lagging-view schedule."));
                return;
            }

            var covered = Counts.Scripted.GetValueOrDefault(Paths.CoveredFromALaggingView);
            _ = await DrainLedgerAsync(_drainers[0], open, toEnd: true);
            if (Counts.Scripted.GetValueOrDefault(Paths.CoveredFromALaggingView) == covered)
            {
                Counts.Violations.Add(Invariant($"A drain of completed ledger {open.Id} through the open metadata a lagging view lists answered other than Covered."));
            }
        }

        /// <summary>
        /// Every seed: a worker restarts with its process's manifest view, read before another worker drained the segment
        /// at that view's watermark, so its first drain, from the view's watermark, takes the same whole segment, and its
        /// commit, through the committer which carried the other's, finds the segment drained and answers <c>Applied</c>
        /// without carrying it; its next drain resumes from that segment's end and applies.
        /// </summary>
        private async Task DrainAfterARestartFromAStaleViewAsync()
        {
            const string key = "x-restart";
            await ProposeAsync(key, RowKind.Append);
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            _ = await DrainToTheRowsSegmentAsync(key, ledger);
            var stale = new ManifestView(_objects);
            var from = (await stale.ReadAsync(CancellationToken.None)).Value.RecordedWatermark(ledger.Id);
            await ProposeAsync(key, RowKind.Append);
            var tail = await _ledgers.EndSegmentAsync(ledger.Id, CancellationToken.None) + 1;
            var other = NewWorker();
            var first = await other.DrainAsync(ledger, tail, Checked(_committers[0]), CancellationToken.None);
            var restarted = NewWorker(stale);
            var duplicate = await restarted.DrainAsync(ledger, tail, Checked(_committers[0]), CancellationToken.None);
            var resumed = await restarted.DrainAsync(ledger, tail, Checked(_committers[1]), CancellationToken.None);
            if (first.Value is DrainOutcome.Drained { Outcome.Value: ManifestCommitOutcome.Applied } drained
                && duplicate.Value is DrainOutcome.Drained { Outcome.Value: ManifestCommitOutcome.Applied } again
                && again.Change.From == from && again.Change.Below == drained.Change.Below
                && resumed.Value is DrainOutcome.Drained { Outcome.Value: ManifestCommitOutcome.Applied } next
                && next.Change.From == drained.Change.Below)
            {
                RecordDrain(0, drained, afterRestart: false);
                RecordDrain(0, again, afterRestart: true);
                RecordDrain(1, next, afterRestart: false);
            }
            else
            {
                Counts.Violations.Add(Invariant($"A restarted worker draining ledger {ledger.Id} from a stale watermark {from} answered {duplicate}, then {resumed}, after another drain answered {first}: not the other's segment found drained and then the next applied."));
            }
        }
        /// <summary>
        /// Every seed: the reader process reads a key, so its store's view predates the row's drain, and learns its ledger
        /// closed; the ledger drains and drops, and its replicas go. The reader, whose map still lists the ledger closed,
        /// reads the key again: its store bounds the ledger below the row, the scan of the dropped replica answers
        /// <c>Dropped</c>, and the reader reads again.
        /// </summary>
        private async Task ReadADroppedLedgerFromAStaleStoreAsync()
        {
            const string key = "x-dropped";
            var reader = _readers[1];
            await ProposeAsync(key, RowKind.Append);
            _ = await reader.View.ReadAsync(CancellationToken.None);
            await ReadHistoryAsync(reader, key);
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key)).Id;
            await CloseAsync(key);
            _ = await reader.View.ReadAsync(CancellationToken.None);
            if (!await CompleteAndDropAsync(ledger))
            {
                Counts.Violations.Add(Invariant($"Ledger {ledger} never dropped in the read-a-dropped-ledger schedule."));
            }

            var dropped = reader.History.Counts.Dropped.Value;
            await ReadHistoryAsync(reader, key);
            if (reader.History.Counts.Dropped.Value == dropped)
            {
                Counts.Violations.Add(Invariant($"A read of {key} whose map lists ledger {ledger}, dropped, closed never met its dropped replica."));
            }
        }

        /// <summary>
        /// Every seed: a worker whose manifest view predates a closed ledger's completion drains the ledger once it dropped
        /// and its replicas went, so its scan meets a dropped replica, which the random rounds' drains meet only when a drop
        /// races them.
        /// </summary>
        private async Task DrainADroppedLedgerAsync()
        {
            const string key = "x-gone";
            await ProposeAsync(key, RowKind.Append);
            var stale = new ManifestView(_objects);
            _ = await stale.ReadAsync(CancellationToken.None);
            var ledger = _ledgers.Ledger((await ReplaceOpenAsync(key)).Id);
            if (!await CompleteAndDropAsync(ledger.Id))
            {
                Counts.Violations.Add(Invariant($"Ledger {ledger.Id} never dropped in the drain-a-dropped-ledger schedule."));
                return;
            }

            var outcome = await NewWorker(stale).DrainAsync(ledger, ((LedgerStatus.Closed)ledger.Status.Value).T, Checked(_committers[0]), CancellationToken.None);
            if (outcome.Value is ReplicaWriteOutcome.Dropped)
            {
                Counts.Add(Paths.DrainDropped);
            }
            else
            {
                Counts.Violations.Add(Invariant($"A drain of ledger {ledger.Id}, dropped, from a manifest view before its completion answered {outcome}."));
            }
        }

        /// <summary>
        /// Every seed: a push of a partition whose rows lie in both children, one section of which holds blocks of both, so
        /// a child reads a strict subset of that section's blocks, fails at its second child's upload, after its first
        /// child's object is uploaded, and commits nothing; a push of the same sections then compacts.
        /// </summary>
        private async Task PushFailingAfterItsFirstChildAsync()
        {
            const string first = "x-push";
            var partition = DrainWorker.GridPartition(_ledgers.OpenContaining(Encoding.UTF8.GetBytes(first)).Range, _options.GridBits);
            var other = partition.Children.Lower.Contains(Encoding.UTF8.GetBytes(first)) ? partition.Children.Upper : partition.Children.Lower;
            var second = KeyIn(other, "-push");
            await ProposeAsync(first, RowKind.Append);
            await ProposeAsync(second, RowKind.Append);
            _ = await DrainEachAsync(first, (first, RowKind.Append));
            _ = await DrainEachAsync(first, (second, RowKind.Append), (first, RowKind.Append));
            var manifest = (await _evidence.ReadAsync(CancellationToken.None)).Value;
            var map = await NodeView.ReadAsync(CancellationToken.None);
            var sections = manifest.SectionsOf(partition);
            var options = _options with { GridBits = 1, MaxSectionsPerPartition = 2, SectionBytes = (sections.Sum(static section => section.Length) + 3) / 4 };
            var plan = Assert.IsType<CompactionPlan>(CompactionWorker.Plan(partition, sections, options));
            foreach (var input in plan.Inputs)
            {
                var metadata = RunObjectReader.SectionOf(Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(_objects, input, CancellationToken.None)), input);
                if (plan.OutputPartitions().Any(child => metadata.Covering(child) is { Count: > 0 } blocks && blocks.Count < metadata.Index.Blocks.Length))
                {
                    Counts.Add(Paths.SubsetReads);
                    break;
                }
            }

            // Through the corpus's own compaction path, so its handling of a failed compaction runs on every seed.
            var failing = new SecondCreateFails(_objects);
            var failed = Counts.Scripted.GetValueOrDefault(Paths.CompactionFailures);
            var outcome = await CompactAndCommitAsync(new CompactionWorker(failing, new TailCache(failing, options), new ManifestView(failing), options), options, partition, manifest, map);
            if (outcome is not null || failing.Creates != 2 || Counts.Scripted.GetValueOrDefault(Paths.CompactionFailures) == failed)
            {
                Counts.Violations.Add(Invariant($"A push whose second upload failed answered {outcome} after {failing.Creates} creates."));
            }
            else
            {
                Counts.Add(Paths.FailuresAfterUpload);
            }

            _ = await CompactWholeAsync(partition);
        }

        /// <summary>Returns the first key of one character and <paramref name="suffix"/> which <paramref name="range"/> contains.</summary>
        private static string KeyIn(KeyRange range, string suffix)
        {
            for (var character = 0x21; character < 0xd800; character++)
            {
                var key = (char)character + suffix;
                if (range.Contains(Encoding.UTF8.GetBytes(key)))
                {
                    return key;
                }
            }

            throw new InvalidOperationException(Invariant($"No key of one character and '{suffix}' lies in {range}."));
        }

        /// <summary>
        /// Compacts every section of <paramref name="partition"/>, pushing them one bit deeper, from a fresh manifest value
        /// or <paramref name="lagging"/>, with a fresh map, and commits the change.
        /// </summary>
        private async Task<ManifestCommitOutcome?> CompactWholeAsync(KeyRange partition, ManifestValue? lagging = null)
        {
            var manifest = lagging ?? (await _evidence.ReadAsync(CancellationToken.None)).Value;
            var map = await NodeView.ReadAsync(CancellationToken.None);
            var sections = manifest.SectionsOf(partition);
            if (sections.Length < 3)
            {
                Counts.Violations.Add(Invariant($"An expiry schedule found {sections.Length} sections of {partition}, too few to compact."));
                return null;
            }

            // s at a quarter of the sections' total, so they reach F × s and fit the cap of 2 × F × s.
            var options = _options with { GridBits = 1, MaxSectionsPerPartition = 2, SectionBytes = (sections.Sum(static section => section.Length) + 3) / 4 };
            return await CompactAndCommitAsync(NewCompactor(options), options, partition, manifest, map, decide: true);
        }

        /// <summary>
        /// Every seed: a compaction uploads its output sections and holds its commit while two sweeps run, then commits
        /// them. Under the healthy sweeper, the compaction outputs' input sections remain listed so both sweeps keep the
        /// outputs pending; under <c>sweep-compaction</c>, the two sweeps mark and delete the uploaded outputs before the
        /// commit references them (LSM-I6).
        /// </summary>
        private async Task HoldCompactionCommitAsync()
        {
            const string key = "x-sweep-compaction";
            var partition = await DrainEachAsync(key, (key, RowKind.Append), (key, RowKind.Append), (key, RowKind.Append));
            var manifest = (await _evidence.ReadAsync(CancellationToken.None)).Value;
            var map = await NodeView.ReadAsync(CancellationToken.None);
            var sections = manifest.SectionsOf(partition);
            var options = _options with { GridBits = 1, MaxSectionsPerPartition = 2, SectionBytes = (sections.Sum(static section => section.Length) + 3) / 4 };
            var outcome = await NewCompactor(options).CompactAsync(partition, manifest, map, CancellationToken.None);
            if (outcome.Value is not CompactionOutcome.Compacted { Change: var change } || change.Outputs.IsEmpty)
            {
                Counts.Violations.Add(Invariant($"The held-compaction schedule produced {outcome}, not a compaction with outputs."));
                return;
            }

            _uploaded.Add(change);
            await SweepToCompletionAsync(_sweepers[0]);
            await SweepToCompletionAsync(_sweepers[0]);
            var committed = await _committers[0].CommitAsync(change, CancellationToken.None);
            if (committed.Value is not ManifestCommitOutcome.Applied)
            {
                Counts.Violations.Add(Invariant($"The held-compaction commit answered {committed}."));
            }
        }

        /// <summary>
        /// Every seed: a drainer scans an open ledger's segment and holds its drain commit while a competing drainer drains
        /// that segment and a second segment, raising the ledger's watermark past the held drain's end; then the held drain
        /// commits. Under the healthy committer, its goal holds so the manifest stays unchanged; under <c>stale-drain</c>,
        /// the stale drain overwrites the higher watermark (LSM-I1).
        /// </summary>
        private async Task CommitAStaleDrainAsync()
        {
            const string key = "x-stale-drain";
            await ProposeAsync(key, RowKind.Append);
            var ledger = _ledgers.OpenContaining(Encoding.UTF8.GetBytes(key));
            var marker1 = await DrainToTheRowsSegmentAsync(key, ledger);
            var held = new HeldCall<ManifestChange, ManifestCommitOutcome>();
            var drain1 = NewWorker().DrainAsync(ledger, marker1 + 1, (change, _) => held.CallAsync(change), CancellationToken.None);
            if (!await held.WaitAsync(drain1))
            {
                Counts.Violations.Add(Invariant($"The stale-drain schedule's first drain finished without submitting: {await drain1}."));
                return;
            }

            var change1 = Assert.IsType<ManifestChange.Drain>((await held.Submitted).Value);
            Submitted(change1);
            await DrainToMarkerAsync(NewWorker(), ledger, marker1);
            await ProposeAsync(key, RowKind.Append);
            var marker2 = await _ledgers.EndSegmentAsync(ledger.Id, CancellationToken.None);
            await DrainToMarkerAsync(NewWorker(), ledger, marker2);
            await held.AnswerAsync(_committers[0].CommitAsync(change1, CancellationToken.None));
            var result = await drain1;
            if (result.Value is not DrainOutcome.Drained { Outcome.Value: ManifestCommitOutcome.Applied })
            {
                Counts.Violations.Add(Invariant($"The stale-drain schedule's held drain answered {result}."));
            }
        }

        /// <summary>
        /// Every seed: a store reads a dropped ledger's row from a section, pinning its view to the manifest listing that
        /// section; a compaction then removes the section and two sweeps delete it, and the same store reads the key again
        /// with the same map. Under the healthy store, the missing section answers <c>Stale</c>, advances the view, and
        /// reads the compacted output; under <c>ignore-missing</c>, the store skips the deleted section and misses the row
        /// (LSM-I4, LSM-I5).
        /// </summary>
        private async Task ReadAcrossASweptSectionAsync()
        {
            const string key = "x-missing";
            await ProposeAsync(key, RowKind.Append);
            var first = await ReplaceOpenAsync(key);
            if (!await CompleteAndDropAsync(first.Id))
            {
                Counts.Violations.Add(Invariant($"Ledger {first.Id} never dropped in the missing-section schedule."));
                return;
            }

            var afterDrop = await NodeView.ReadAsync(CancellationToken.None);
            var process = _readers[0] with { Store = NewStore() };
            await ReadStoreAsync(process, key, afterDrop);
            var partition = await DrainEachAsync(key, ("x-filler", RowKind.Append), ("x-filler", RowKind.Append));
            _ = await CompactWholeAsync(partition);
            await SweepToCompletionAsync(_sweepers[0]);
            await SweepToCompletionAsync(_sweepers[0]);
            await ReadStoreAsync(process, key, afterDrop);
        }

        /// <summary>
        /// Every seed: a key's row stays undrained in a closed ledger while a later row in the open successor ledger drains
        /// into a section; then a history read and a store read run. Under the healthy store, both ledgers remain listed so
        /// the store answers <c>Absent</c> and the history read scans both live; under <c>hold-by-watermark</c>, the store
        /// holds the successor's drained row without holding the earlier undrained ledger's row, breaking both the merge
        /// chain and the store oracle (LSM-I4, LSM-I5).
        /// </summary>
        private async Task ReadWithOutOfOrderDrainAsync()
        {
            const string key = "x-watermark";
            await ProposeAsync(key, RowKind.Append);
            _ = await ReplaceOpenAsync(key);
            _ = await DrainEachAsync(key, (key, RowKind.Append));
            var process = _readers[0] with { Store = NewStore() };
            await ReadStoreAsync(process, key, await NodeView.ReadAsync(CancellationToken.None));
            await ReadHistoryAsync(NewReader("watermark-reader", _readers[0].View, _readers[0].Reader, owned: false), key);
        }

        /// <summary>
        /// Every seed: a key's row in a dropped ledger compacts with a <c>Replace</c> of the same key drained from the open
        /// successor ledger, then a fresh store reads the key while the successor is still open. Under the healthy
        /// compactor, an incomplete ledger's <c>Replace</c> supersedes nothing so the dropped ledger's row survives; under
        /// <c>supersede-by-open</c>, the compaction drops the older row and the store answers <c>Absent</c> (LSM-I4, LSM-I5).
        /// </summary>
        private async Task CompactWithAnOpenReplaceAsync()
        {
            const string key = "x-supersede-open";
            await ProposeAsync(key, RowKind.Append);
            var first = await ReplaceOpenAsync(key);
            if (!await CompleteAndDropAsync(first.Id))
            {
                Counts.Violations.Add(Invariant($"Ledger {first.Id} never dropped in the supersede-by-open schedule."));
                return;
            }

            var partition = await DrainEachAsync(key, (key, RowKind.Replace), ("x-filler", RowKind.Append));
            _ = await CompactWholeAsync(partition);
            await ReadStoreAsync(_readers[0] with { Store = NewStore() }, key, await NodeView.ReadAsync(CancellationToken.None));
        }

        /// <summary>
        /// Every seed: a key's row in a dropped ledger is captured in a map value that lists the successor ledger open; the
        /// successor then drains a <c>Replace</c>, completes, drops, and compacts, legitimately superseding the first row; a
        /// fresh store then reads the key with the stale map value. Under the healthy store, the predates check sees the
        /// map's open ledger complete in the view and answers <c>Stale</c>; under <c>no-predates-check</c>, the store
        /// filters out the successor's <c>Replace</c> and answers <c>Absent</c> (LSM-I4, LSM-I5).
        /// </summary>
        private async Task ReadWithAStaleOpenMapAsync()
        {
            const string key = "x-predates";
            await ProposeAsync(key, RowKind.Append);
            var first = await ReplaceOpenAsync(key);
            if (!await CompleteAndDropAsync(first.Id))
            {
                Counts.Violations.Add(Invariant($"Ledger {first.Id} never dropped in the predates-check schedule."));
                return;
            }

            var staleOpenMap = await NodeView.ReadAsync(CancellationToken.None);
            var partition = await DrainEachAsync(key, (key, RowKind.Replace), ("x-filler", RowKind.Append));
            await DropTheKeysLedgersAsync(key);
            _ = await CompactWholeAsync(partition);
            await ReadStoreAsync(_readers[0] with { Store = NewStore() }, key, staleOpenMap);
        }

        /// <summary>
        /// Every seed: dropped ledgers' rows are compacted behind a <c>Replace</c> or <c>Delete</c> in a closed, complete
        /// ledger that the read's map still lists — both wholly (the store answers <c>Absent</c>) and partially across
        /// sections (the store answers the fold of the remaining section). The store oracle accepts the compliant answers
        /// (archive-lsm.md §8.6) and each history read folds to the superseding ledger's state.
        /// </summary>
        private async Task CompactWithAClosedCompleteReplaceAsync()
        {
            const string replaceKey = "x-supersede-closed";
            await ProposeAsync(replaceKey, RowKind.Append);
            var firstReplace = await ReplaceOpenAsync(replaceKey);
            if (!await CompleteAndDropAsync(firstReplace.Id))
            {
                Counts.Violations.Add(Invariant($"Ledger {firstReplace.Id} never dropped in the supersede-by-closed schedule."));
                return;
            }

            var replacePartition = await DrainEachAsync(replaceKey, (replaceKey, RowKind.Replace), ("x-filler", RowKind.Append));
            await CloseAndCompleteAsync(replaceKey);
            _ = await CompactWholeAsync(replacePartition);
            await ReadStoreAsync(_readers[0] with { Store = NewStore() }, replaceKey, await NodeView.ReadAsync(CancellationToken.None));
            await ReadHistoryAsync(_readers[0], replaceKey);

            const string deleteKey = "x-supersede-delete";
            await ProposeAsync(deleteKey, RowKind.Append);
            var firstDelete = await ReplaceOpenAsync(deleteKey);
            if (!await CompleteAndDropAsync(firstDelete.Id))
            {
                Counts.Violations.Add(Invariant($"Ledger {firstDelete.Id} never dropped in the supersede-by-closed-delete schedule."));
                return;
            }

            var deletePartition = await DrainEachAsync(deleteKey, (deleteKey, RowKind.Delete), ("x-filler", RowKind.Append));
            await CloseAndCompleteAsync(deleteKey);
            _ = await CompactWholeAsync(deletePartition);
            await ReadStoreAsync(_readers[0] with { Store = NewStore() }, deleteKey, await NodeView.ReadAsync(CancellationToken.None));
            await ReadHistoryAsync(_readers[0], deleteKey);

            const string partialKey = "x-supersede-partial";
            var partialPartition = await DrainEachAsync(partialKey, (partialKey, RowKind.Append), ("x-filler", RowKind.Append), ("x-filler", RowKind.Append));
            _ = await CompactWholeAsync(partialPartition);
            await ProposeAsync(partialKey, RowKind.Append);
            var firstPartial = await ReplaceOpenAsync(partialKey);
            if (!await CompleteAndDropAsync(firstPartial.Id))
            {
                Counts.Violations.Add(Invariant($"Ledger {firstPartial.Id} never dropped in the partial supersede-by-closed schedule."));
                return;
            }

            _ = await DrainEachAsync(partialKey, (partialKey, RowKind.Replace), ("x-filler", RowKind.Append));
            await CloseAndCompleteAsync(partialKey);
            _ = await CompactWholeAsync(partialPartition);
            var beforePartialRead = _storeReads.Count;
            await ReadStoreAsync(_readers[0] with { Store = NewStore() }, partialKey, await NodeView.ReadAsync(CancellationToken.None));
            if (_storeReads.Count <= beforePartialRead || _storeReads[^1].Prefix is null)
            {
                Counts.Violations.Add("The partial supersede-by-closed schedule did not retain an older section in the child partition.");
            }

            await ReadHistoryAsync(_readers[0], partialKey);
        }

        /// <summary>
        /// With every fault off, completes and drops every closed ledger, and runs the expiry schedules; then reads every key
        /// in each reader process, and the writer's linearizable reads give each key's whole reference from its base.
        /// </summary>
        private async Task SettleAsync()
        {
            (_objects.UnknownOutcomeRate, _objects.RetriedAnswerRate, _objects.ReadFailureRate, _objects.ChangedContentRate) = (0, 0, 0, 0);
            _register.UnknownOutcomeRate = 0;
            _ledgers.Transport.Saturated = static (_, _) => false;
            _lossRate = 0;
            SweepStore.Rate = 0;
            await CompleteAndDropClosedAsync();
            Counts.Scripting = true;
            if (_mutant == Mutant.None)
            {
                // Each step forces a path, a fault or a race, which the random rounds may not draw on every seed.
                await DropAcrossALostMapWriteAsync();
                await DrainAgainstBackpressureAsync();
                await DrainASegmentAcrossObjectsAsync();
                await SweepAcrossAFailedListingAsync();
                await PushFailingAfterItsFirstChildAsync();
                await DrainAfterARestartFromAStaleViewAsync();
                await ResubmitADrainWhoseAnswerWasLostAsync();
                await DrainACompletedLedgerFromALaggingViewAsync();
                await ReadADroppedLedgerFromAStaleStoreAsync();
                await DrainADroppedLedgerAsync();
                await HoldCompactionCommitAsync();
                await CommitAStaleDrainAsync();
                await ReadAcrossASweptSectionAsync();
                await ReadWithOutOfOrderDrainAsync();
                await CompactWithAnOpenReplaceAsync();
                await ReadWithAStaleOpenMapAsync();
                await CompactWithAClosedCompleteReplaceAsync();
                await CompleteAndDropClosedAsync();
            }

            if (_compacting)
            {
                // Each schedule leaves no incomplete ledger below the next one's, except the last. An expiry mutant runs
                // only the schedule which meets it.
                if (_mutant is Mutant.None or Mutant.NeverDrop)
                {
                    await ExpireAsync();
                }

                if (_mutant is Mutant.None or Mutant.DropIgnoringSections)
                {
                    await ExpireOverAnOlderSectionAsync();
                }

                if (_mutant is Mutant.None or Mutant.DropTrustingUnlisted)
                {
                    await ExpireOverADroppedLedgerAsync();
                }

                if (_mutant is Mutant.None or Mutant.UncoveredExpiry)
                {
                    await ExpireUnderAnOlderViewAsync();
                }

                if (_mutant is Mutant.None or Mutant.DropIgnoringLive)
                {
                    await ExpireOverAnUndrainedRowAsync();
                }

                await CompleteAndDropClosedAsync();
            }

            Counts.Scripting = false;

            if (_ledgers.Map.Current.Ledgers.Any(static ledger => !ledger.IsOpen))
            {
                Counts.Violations.Add("A closed ledger never dropped once the faults stopped.");
            }

            if (Sweeps)
            {
                // Every delayed write has landed, or never will, before the final sweeps.
                await Task.Delay(_objects.MaxLandingDelay + TimeSpan.FromMilliseconds(1), _environment.TimeProvider);
                await SweepToCompletionAsync(_sweepers[0]);
                await SweepToCompletionAsync(_sweepers[0]);
                await VerifyNoGarbageAsync();
            }

            foreach (var key in _reference.Keys)
            {
                foreach (var reader in _readers)
                {
                    await ReadHistoryAsync(reader, key);
                }

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
        /// HI-I1: each read's live rows are the reference's rows of the ledgers it read below their ends, and its prefix
        /// with the rows not held folds to the reference over its <c>Through</c>; over that cut, no row proposed after the
        /// read ended, every row of a ledger closed before it began, and, for a linearizable read, every row answered durable
        /// before it began.
        /// </summary>
        private void VerifyReads()
        {
            var proposals = _proposals.ToDictionary(static proposal => proposal.Row.Tag);
            foreach (var read in _reads)
            {
                List<Decided> reference = [.. _reference[read.Key].OrderBy(static row => row.Tag)];
                var highest = read.Through.Count == 0 ? (LedgerId?)null : read.Through.Keys.Max();
                List<Decided> cut = [.. reference.Where(row => read.Through.TryGetValue(row.Tag.Ledger, out var end) ? row.Tag.Slot < end : row.Tag.Ledger < highest)];
                var expected = Fold(cut);
                var live = cut.Where(row => read.Through.ContainsKey(row.Tag.Ledger)).ToArray();
                if (!expected.SameAs(read.Fold) || !live.Select(static row => row.Tag).SequenceEqual(read.Rows.Select(static row => row.Tag)))
                {
                    Counts.Violations.Add(Invariant($"{ReadOracle}{read.Reader}'s read of {read.Key} between steps {read.CalledAt} and {read.AnsweredAt} scanned [{Show(read.Rows)}] and folds to {read.Fold}, not [{Show(live)}] and {expected}, which the reference over its Through gives."));
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
                    Counts.Violations.Add(Invariant($"{ReadOracle}{read.Reader}'s read of {read.Key} (linearizable {read.Linearizable}) misses rows decided before it began: [{Show(missed)}]."));
                }
            }
        }

        /// <summary>
        /// Each store answer's prefix folds exactly the rows of the ledgers its map dropped, or the store answers
        /// <c>Absent</c> when they are none (archive-lsm.md §2.8): or, for a key whose rows a compaction dropped with an empty
        /// <c>Delete</c>, an expiry, the rows after it, which a store whose view holds the drop answers; or, when a closed,
        /// complete ledger the read's map still lists holds a <c>Replace</c> or <c>Delete</c> superseding rows of dropped
        /// ledgers, the fold of the dropped-ledger rows whose sections remain in the store's view (archive-lsm.md §8.6).
        /// </summary>
        private void VerifyStoreReads()
        {
            foreach (var read in _storeReads)
            {
                var map = read.Map;
                List<Decided> covered = [.. _reference[read.Key]
                    .Where(row => row.Tag.Ledger < map.NextLedgerId && map.Get(row.Tag.Ledger) is null)
                    .OrderBy(static row => row.Tag)];
                var expected = covered.Count == 0 ? null : Fold(covered);

                // A compaction drops an empty Delete, an expiry, with every older row of its key once nothing older lies
                // outside it (LSM-I5), and a store whose view no longer holds it then answers the rows after it: a view which
                // a manifest value at or below its epoch shows the Delete drained into, and its own then compacted away. The
                // rows after it fold as the Delete and they do, but none after it reads Absent.
                var expiry = covered.LastOrDefault(static row => row.Kind is RowKind.Replace or RowKind.Delete);
                if (read.Prefix is null && expected is not null
                    && expiry is { Kind: RowKind.Delete, Payload: "" }
                    && covered[^1] == expiry
                    && _droppedExpiries.Contains(expiry.Tag)
                    && read.ViewEpoch is { } epoch
                    && ExpiredBy(read.Key, expiry.Tag, epoch))
                {
                    Counts.Add(Paths.ExpiredReads);
                }
                else if (expected is not null
                    && (read.Prefix is null || !expected.SameAs(read.Prefix))
                    && read.ViewEpoch is { } viewEpoch
                    && SupersededByClosedListedLedger(read, covered, viewEpoch))
                {
                    Counts.Add(Paths.SupersededReads);
                }
                else if (expected is null ? read.Prefix is not null : read.Prefix is null || !expected.SameAs(read.Prefix))
                {
                    Counts.Violations.Add(Invariant($"{StoreOracle}{read.Reader}'s store answer for {read.Key} at map epoch {map.Epoch} folds to {read.Prefix?.ToString() ?? "Absent"}, not {expected?.ToString() ?? "Absent"}."));
                }
            }
        }

        /// <summary>
        /// Whether <paramref name="read"/>'s map still lists a closed ledger that the store's view shows complete and that
        /// holds a <c>Replace</c> or <c>Delete</c> for <paramref name="read"/>'s key, so a compaction may drop superseded
        /// rows of dropped ledgers (archive-lsm.md §8.6).
        /// </summary>
        private bool HasClosedSupersederInMap(StoreReadRecord read) =>
            read.ViewEpoch is { } epoch
            && ManifestAt(epoch) is var view
            && _reference[read.Key].Any(row =>
                row.Kind is RowKind.Replace or RowKind.Delete
                && read.Map.Get(row.Tag.Ledger) is { IsOpen: false }
                && view.IsComplete(row.Tag.Ledger));

        /// <summary>
        /// Whether a closed, complete ledger that <paramref name="read"/>'s map still lists holds a <c>Replace</c> or
        /// <c>Delete</c> superseding rows of <paramref name="covered"/>, and <paramref name="read"/>'s prefix equals the
        /// fold of the <paramref name="covered"/> rows whose sections remain in the store's manifest view at
        /// <paramref name="epoch"/> (archive-lsm.md §8.6, LSM-I4, LSM-I5).
        /// </summary>
        private bool SupersededByClosedListedLedger(StoreReadRecord read, List<Decided> covered, ulong epoch)
        {
            var view = ManifestAt(epoch);
            if (!LsmArchiveStore.Covers(view, read.Map))
            {
                return false;
            }

            if (!_reference[read.Key].Any(row =>
                    row.Kind is RowKind.Replace or RowKind.Delete
                    && row.Tag > covered[^1].Tag
                    && read.Map.Get(row.Tag.Ledger) is { IsOpen: false }
                    && view.IsComplete(row.Tag.Ledger)))
            {
                return false;
            }

            var bytes = Encoding.UTF8.GetBytes(read.Key);
            var inView = view.SectionsContaining(bytes)
                .SelectMany(SectionRowsOf)
                .Where(row => row.Key == read.Key)
                .Select(static row => row.Tag)
                .ToHashSet();
            List<Decided> remaining = [.. covered.Where(row => inView.Contains(row.Tag))];
            if (remaining.Count == covered.Count)
            {
                return false;
            }

            var expected = remaining.Count == 0 ? null : Fold(remaining);
            return expected is null
                ? read.Prefix is null
                : read.Prefix is not null && expected.SameAs(read.Prefix);
        }

        /// <summary>
        /// Counts the applied drains which found their segment drained by another, their sections never listed at the
        /// epoch which answered them, after a restart among them, and the committer epochs which answered both such a
        /// duplicate and a drain they carried.
        /// </summary>
        private void CountDuplicates()
        {
            var manifests = _objects.AppliedWritesOf(ObjectName.Manifest).Select(Decode).ToDictionary(static value => value.Epoch);
            var carried = new HashSet<(int, ulong)>();
            var duplicated = new HashSet<(int, ulong)>();
            foreach (var drain in _appliedDrains.Where(static drain => !drain.Change.Sections.IsEmpty))
            {
                if (manifests.TryGetValue(drain.Epoch, out var value) && value.Sections.Contains(drain.Change.Sections[0]))
                {
                    _ = carried.Add((drain.Committer, drain.Epoch));
                    continue;
                }

                _ = duplicated.Add((drain.Committer, drain.Epoch));
                Counts.Add(Paths.Duplicates, scripted: drain.Scripted);
                if (drain.AfterRestart)
                {
                    Counts.Add(Paths.DuplicateAfterRestart, scripted: drain.Scripted);
                }
            }

            Counts.Add(Paths.MixedBatches, carried.Intersect(duplicated).Count());
        }

        private void CountPaths()
        {
            CountDuplicates();
            Counts.Add(Paths.MapUnknown, _register.UnknownOutcomes);
            Counts.Add(Paths.ReplicaBackpressure, _ledgers.Transport.Answered);
            foreach (var reader in _readers)
            {
                Counts.Add(Paths.ReaderStale, reader.History.Counts.Stale.Value);
                Counts.Add(Paths.ReaderDropped, reader.History.Counts.Dropped.Value);
                Counts.Add(Paths.ReaderBackpressured, reader.History.Counts.Backpressured.Value);
            }

            static string Of(Dictionary<string, int> paths) =>
                string.Join(", ", paths.OrderBy(static path => path.Key, StringComparer.Ordinal).Select(static path => Invariant($"{path.Key} {path.Value}")));
            _environment.Trace.Record("archive paths: " + Of(Counts.Paths) + "; scripted: " + Of(Counts.Scripted));
        }

        private Task DelayAsync(int maximumMilliseconds) =>
            Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(maximumMilliseconds)), _environment.TimeProvider);
    }

    /// <summary>
    /// The <c>uncovered-drop</c> mutant: it reads the store with the map value of its caller's previous read, so its view
    /// need not show complete a ledger the caller's map dropped (LSM-I4).
    /// </summary>
    private sealed class LaggingMapStore(IArchiveStore inner) : IArchiveStore
    {
        private LedgerMapValue? _previous;

        public ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken)
        {
            var lagging = _previous ?? map;
            _previous = map;
            return inner.ReadAsync(key, lagging, cancellationToken);
        }

        public ArchiveStoreKind Kind => inner.Kind;

        public int? MaxAppendBytes => inner.MaxAppendBytes;

        public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
            inner.StoreRecordAsync(parameters, cancellationToken);

        public ArchivePlan Begin(RowToken token) => inner.Begin(token);

        public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count) => inner.Holds(prefix, write, count);

        public bool IsReplaceRequested(RowToken token) => inner.IsReplaceRequested(token);

        public bool AdmitsKey(ReadOnlySpan<byte> key) => inner.AdmitsKey(key);
    }

    /// <summary>
    /// The sweepers' path to the object store, which fails a listing partway, after the objects it listed, or a delete
    /// before it reaches the store, each at <see cref="Rate"/>, so a sweep fails while drains and compactions race it.
    /// </summary>
    private sealed class FlakySweepStore(IObjectStore inner, SimulationRandom random) : IObjectStore
    {
        /// <summary>Gets or sets the chance of each fault.</summary>
        public double Rate { get; set; } = SweepFaultRate;

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public async IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var listed in inner.ListAsync(prefix, startAfter, cancellationToken))
            {
                yield return listed;
                if (random.Chance(Rate))
                {
                    throw new IOException("A fault ended the listing partway.");
                }
            }
        }

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            random.Chance(Rate) ? ValueTask.FromException(new IOException("A delete failed before it reached the store.")) : inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }

    /// <summary>An object store whose listings fail before they reach the store.</summary>
    private sealed class ListingFails(IObjectStore inner) : IObjectStore
    {
        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) => FailAsync();

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) => inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);

        /// <summary>Yields no object, and then fails, as a listing whose first page's request fails does.</summary>
        private static async IAsyncEnumerable<ListedObject> FailAsync()
        {
            await Task.CompletedTask;
            foreach (var listed in Array.Empty<ListedObject>())
            {
                yield return listed;
            }

            throw new IOException("A scripted listing failure.");
        }
    }

    /// <summary>
    /// An object store whose second create fails before it reaches the store, as a push's second child's upload does in
    /// the scripted failure. The worker calls it from the code under test, so it never awaits.
    /// </summary>
    private sealed class SecondCreateFails(IObjectStore inner) : IObjectStore
    {
        public int Creates { get; private set; }

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            ++Creates == 2 ? ValueTask.FromException<ObjectCreateOutcome>(new IOException("A scripted create failure.")) : inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) => inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }
}
