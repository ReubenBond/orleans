using System.Globalization;
using System.Text;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Exports Clockwork simulation traces of both LSM and Archive-Logs local-stream draining (plus named negative-control
/// mutant traces) to <c>DURABLE_STORAGE_LOCAL_DRAIN_TRACES</c> for replay against
/// <c>test/Durable.Storage/specs/local-drain/LocalDrain.qnt</c> (verification.md §5.1, §8.1).
/// </summary>
public sealed class LocalDrainTraceTests
{
    private const int FirstSeed = 1;
    private const int SeedCount = 3;
    private static readonly LocalStreamName Stream = SimulatedWakeupStreamProcessor.DefaultStream;

    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    /// <summary>Exports a conforming LSM local-drain simulation trace and verifies every recorded step.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void LsmLocalDrainRecordsAValidTrace(int seed)
    {
        Trace? trace = null;
        _ = SeededSimulation.Run(seed, async environment => trace = await SimulateLsmAsync(environment, seed, dropBeforeBarrierMutant: false));
        Assert.NotNull(trace);
        Assert.Contains("lsmCompleteDrain(1)", trace.Actions);
        Assert.Contains("dropLedger", trace.Actions);
        Write(trace, string.Create(CultureInfo.InvariantCulture, $"LocalDrainTraceLsm{seed}"));
    }

    /// <summary>Exports a conforming Archive-Logs local-drain simulation trace and verifies every recorded step.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void ArchiveLogsLocalDrainRecordsAValidTrace(int seed)
    {
        Trace? trace = null;
        _ = SeededSimulation.Run(seed, async environment => trace = await SimulateLogsAsync(environment, seed));
        Assert.NotNull(trace);
        Assert.Contains("logsDrainKeyed", trace.Actions);
        Assert.Contains("logsMarkComplete(1)", trace.Actions);
        Assert.Contains("dropLedger", trace.Actions);
        Write(trace, string.Create(CultureInfo.InvariantCulture, $"LocalDrainTraceLogs{seed}"));
    }

    /// <summary>
    /// Negative-control mutant trace <c>drop-before-barrier</c>: <see cref="DrainWorker.DropBeforeLocalDrainedMutant"/>
    /// marks the ledger complete and drops it before <c>EnsureLocalStreamsDrainedAsync</c> completes, which
    /// <c>LocalDrain.qnt</c> must reject.
    /// </summary>
    [Fact]
    public void DropBeforeBarrierMutantRecordsATraceTheModelRefuses()
    {
        Trace? trace = null;
        _ = SeededSimulation.Run(1, async environment => trace = await SimulateLsmAsync(environment, 1, dropBeforeBarrierMutant: true));
        Assert.NotNull(trace);
        Assert.Contains("lsmCompleteDrain(1)", trace.Actions);
        Write(trace, "LocalDrainTraceMutantDropBeforeBarrier1");
    }

    /// <summary>
    /// Negative-control mutant trace <c>complete-on-dropped</c>: attempts <c>lsmCompleteDrain</c> after the ledger was
    /// already dropped without barrier completion, which <c>LocalDrain.qnt</c> must reject.
    /// </summary>
    [Fact]
    public void CompleteOnDroppedMutantRecordsATraceTheModelRefuses()
    {
        var trace = new Trace(Mode: "lsm", Slots: ["S", "K"]);
        trace.Actions.Add("primaryWrite");
        trace.Actions.Add("primaryWrite");
        trace.Actions.Add("closeLedger");
        // Mutant erroneously skips barrier and drops/completes out of order.
        trace.Actions.Add("dropLedger");
        trace.Actions.Add("lsmCompleteDrain(1)");
        Write(trace, "LocalDrainTraceMutantCompleteOnDropped1");
    }

    /// <summary>
    /// Negative-control mutant trace <c>skip-unknown-stream</c>: runs <see cref="LocalStreamDrainCoordinator.SkipUnknownStreamMutant"/>
    /// over a ledger containing an unregistered local stream and emits a trace claiming <c>lsmCompleteDrain</c> without
    /// durably committing the wakeup, which <c>LocalDrain.qnt</c> must reject.
    /// </summary>
    [Fact]
    public void SkipUnknownStreamMutantRecordsATraceTheModelRefuses()
    {
        Trace? trace = null;
        _ = SeededSimulation.Run(1, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None);
            await ledgers.BootstrapAsync(CancellationToken.None);

            var ledger = ledgers.OpenContaining([1]).Id;
            var unknownKey = LedgerLocalKey.Encode(ledger, KeyRange.Root, LocalStreamName.Parse("sim.unregistered"));
            await ProposeMixedAsync(ledgers, ledger, [1], "x"u8.ToArray(), unknownKey, SimulatedWakeupStreamProcessor.Encode(1, set: true));
            await ledgers.ReplaceAsync(ledger, CancellationToken.None);
            var meta = ledgers.Ledger(ledger);
            var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

            var processor = new SimulatedWakeupStreamProcessor();
            var mutantCoord = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [processor])
            {
                SkipUnknownStreamMutant = true,
            };
            var outcome = await mutantCoord.EnsureLocalStreamsDrainedAsync(meta, tail, CancellationToken.None);
            Assert.IsType<LocalDrainBarrierOutcome.Drained>(outcome.Value);
            Assert.Empty(processor.CommittedKeys);

            // Trace reflects what the mutant did: wrote S, closed, started barrier, skipped commit (0 backing writes), then completed.
            trace = new Trace(Mode: "lsm", Slots: ["S"]);
            trace.Actions.Add("primaryWrite");
            trace.Actions.Add("closeLedger");
            trace.Actions.Add("startBarrier(1)");
            trace.Actions.Add("scanSlot(1)");
            trace.Actions.Add("lsmCompleteDrain(1)");
        });

        Assert.NotNull(trace);
        Write(trace, "LocalDrainTraceMutantSkipUnknownStream1");
    }

    private static async Task<Trace> SimulateLsmAsync(SimulationEnvironment environment, int seed, bool dropBeforeBarrierMutant)
    {
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
        var objects = new InMemoryObjectStore();
        var manifest = new ManifestView(objects);
        await using var manifestCommitter = new ManifestCommitter(objects, manifest, SimulationEnvironment.Clock, TimeSpan.Zero);
        await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None);
        await ledgers.BootstrapAsync(CancellationToken.None);

        var slots = BuildSlots(seed);
        var trace = new Trace("lsm", slots);

        var ledger = ledgers.OpenContaining([1]).Id;
        var localKey = LedgerLocalKey.Encode(ledger, KeyRange.Root, Stream);
        foreach (var op in slots)
        {
            await WriteSlotOpAsync(ledgers, ledger, localKey, op);
            trace.Actions.Add("primaryWrite");
        }

        await ledgers.ReplaceAsync(ledger, CancellationToken.None);
        trace.Actions.Add("closeLedger");

        var meta = ledgers.Ledger(ledger);
        var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;
        var hasSurviving = HasSurvivingWakeup(slots);

        var processor = new SimulatedWakeupStreamProcessor();
        var coordinator = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [processor]);
        var worker = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, localDrainBarrier: coordinator)
        {
            DropBeforeLocalDrainedMutant = dropBeforeBarrierMutant,
        };

        if (!dropBeforeBarrierMutant)
        {
            trace.Actions.Add("startBarrier(1)");
            for (var i = 0; i < slots.Length; i++)
            {
                trace.Actions.Add("scanSlot(1)");
            }

            trace.Actions.Add("beginCommit(1)");
            if (hasSurviving)
            {
                trace.Actions.Add("finishCommit(1)");
            }
        }

        var outcome = await worker.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None);
        var drained = Assert.IsType<DrainOutcome.Drained>(outcome.Value);
        Assert.True(drained.Change.Complete);
        trace.Actions.Add("lsmCompleteDrain(1)");

        var dropped = await ledgers.Committer.CommitAsync(new LedgerMapChange.Drop(ledger), CancellationToken.None);
        Assert.IsType<LedgerMapCommitOutcome.Applied>(dropped.Value);
        trace.Actions.Add("dropLedger");

        if (hasSurviving && !dropBeforeBarrierMutant)
        {
            Assert.Contains((byte)1, processor.CommittedKeys);
            trace.Actions.Add("backingFireWakeup");
        }
        else if (!hasSurviving)
        {
            Assert.Empty(processor.CommittedKeys);
        }

        return trace;
    }

    private static async Task<Trace> SimulateLogsAsync(SimulationEnvironment environment, int seed)
    {
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
        var objects = new InMemoryObjectStore();
        var logs = new MemoryArchiveLogStore(new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 8, 49_900, 40_000, MemoryArchiveLogStore.NameRule));
        var archiveStore = new LogArchiveStore(logs, objects, ArchiveLogNames.Hexadecimal);
        var record = await archiveStore.StoreRecordAsync(new Dictionary<string, string>(), CancellationToken.None);
        var gate = await ArchiveLogGate.PassAsync(archiveStore, new ArchiveStoreIdentity(ArchiveStoreKind.Logs, ArchiveStorePrefix.Of("sim"), record.Id), new Dictionary<string, string>(), CancellationToken.None);

        await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None, chains: new ArchiveChains(archiveStore));
        await ledgers.BootstrapAsync(CancellationToken.None);

        var slots = BuildSlots(seed);
        var trace = new Trace("logs", slots);

        var ledger = ledgers.OpenContaining([1]).Id;
        var localKey = LedgerLocalKey.Encode(ledger, KeyRange.Root, Stream);
        foreach (var op in slots)
        {
            await WriteSlotOpAsync(ledgers, ledger, localKey, op);
            trace.Actions.Add("primaryWrite");
        }

        await ledgers.ReplaceAsync(ledger, CancellationToken.None);
        trace.Actions.Add("closeLedger");

        var meta = ledgers.Ledger(ledger);
        var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;
        var hasSurviving = HasSurvivingWakeup(slots);

        var processor = new SimulatedWakeupStreamProcessor();
        var coordinator = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [processor]);
        var worker = new ArchiveLogDrainWorker(gate, ledgers.Map, ledgers.Reader, new ArchiveLogOptions(), SimulationEnvironment.Clock, localDrainBarrier: coordinator);

        var outcome = await worker.DrainAsync(meta, tail, CancellationToken.None);
        Assert.IsType<ArchiveLogDrainOutcome.Complete>(outcome.Value);
        trace.Actions.Add("logsDrainKeyed");
        trace.Actions.Add("startBarrier(1)");
        for (var i = 0; i < slots.Length; i++)
        {
            trace.Actions.Add("scanSlot(1)");
        }

        trace.Actions.Add("beginCommit(1)");
        if (hasSurviving)
        {
            trace.Actions.Add("finishCommit(1)");
        }

        trace.Actions.Add("logsMarkComplete(1)");

        var dropped = await worker.SubmitDropAsync(ledger, ledgers.Committer.CommitAsync, CancellationToken.None);
        Assert.IsType<LedgerMapCommitOutcome.Applied>(dropped.Value);
        trace.Actions.Add("dropLedger");

        if (hasSurviving)
        {
            Assert.Contains((byte)1, processor.CommittedKeys);
            trace.Actions.Add("backingFireWakeup");
        }
        else
        {
            Assert.Empty(processor.CommittedKeys);
        }

        return trace;
    }

    private static string[] BuildSlots(int seed) => (seed % 3) switch
    {
        1 => ["S", "K"],
        2 => ["S", "C", "K"],
        _ => ["K", "S", "C", "S"],
    };

    private static bool HasSurvivingWakeup(string[] slots)
    {
        var armed = false;
        foreach (var op in slots)
        {
            if (op == "S")
            {
                armed = true;
            }
            else if (op == "C")
            {
                armed = false;
            }
        }

        return armed;
    }

    private static async Task WriteSlotOpAsync(LiveLedgers ledgers, LedgerId ledger, byte[] localKey, string op)
    {
        if (op == "K")
        {
            _ = await ledgers.ProposeAsync(ledger, [new LiveRow([1], RowKind.Append, "aaaaaaaa"u8.ToArray())], CancellationToken.None);
            return;
        }

        await ProposeMixedAsync(ledgers, ledger, [1], "bbbbbbbb"u8.ToArray(), localKey, SimulatedWakeupStreamProcessor.Encode(1, set: op == "S"));
    }

    private static async Task ProposeMixedAsync(LiveLedgers ledgers, LedgerId ledger, byte[] journalKey, byte[] journalBody, byte[] localKey, byte[] localPayload)
    {
        var writer = ledgers.Registry.TryGet(ledger)!;
        using var batch = await writer.CreateBatchAsync(2, 4096, CancellationToken.None);
        _ = batch.BeginRow(journalKey, RowKind.Append, batch.HeldVersion(journalKey), default, journalBody.Length, journalBody);
        _ = batch.BeginRow(localKey, RowKind.LocalAppend, batch.HeldVersion(localKey), default, localPayload.Length, localPayload);
        Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync(batch, CancellationToken.None)).Value);
    }

    private static void Write(Trace trace, string module)
    {
        if (Environment.GetEnvironmentVariable("DURABLE_STORAGE_LOCAL_DRAIN_TRACES") is { Length: > 0 } directory)
        {
            _ = Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, module + ".qnt"), trace.Render(module));
        }
    }

    private sealed class Trace(string Mode, string[] Slots)
    {
        public List<string> Actions { get; } = [];

        public string Render(string module)
        {
            var sb = new StringBuilder();
            _ = sb.AppendLine("// -*- mode: Bluespec; -*-");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"module {module} {{");
            var slotList = string.Join(", ", Slots.Select(static s => $"\"{s}\""));
            _ = sb.AppendLine("  import LocalDrain(");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    Mode = \"{Mode}\",");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    Slots = [{slotList}],");
            _ = sb.AppendLine("    InitialLsmWatermark = 0,");
            _ = sb.AppendLine("    InitialOlderListed = false,");
            _ = sb.AppendLine("    OlderCanDrop = false,");
            _ = sb.AppendLine("    Drainers = 1,");
            _ = sb.AppendLine("    AllowChunkFlush = false,");
            _ = sb.AppendLine("    MaxFlushes = 0,");
            _ = sb.AppendLine("    MaxCrashes = 1,");
            _ = sb.AppendLine("    Mutation = \"none\"");
            _ = sb.AppendLine("  ).* from \"../../specs/local-drain/LocalDrain\"");
            _ = sb.AppendLine();
            _ = sb.AppendLine("  val invariantCheck: bool = TypeOK and WakeupPreserved and NoDropBeforeLocalDrained and ClearedBeforeCloseWritesNothing");
            _ = sb.AppendLine();
            _ = sb.AppendLine("  run traceReplay =");
            _ = sb.AppendLine("    init.expect(invariantCheck)");
            foreach (var action in Actions)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    .then({action}).expect(invariantCheck)");
            }

            _ = sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
