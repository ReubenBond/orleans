using System.Globalization;
using System.Text;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Exports Clockwork simulation traces of unified single-pass <c>DrainScan</c> message draining across both LSM
/// (<see cref="DrainWorker"/>) and Archive Logs (<see cref="ArchiveLogDrainWorker"/>), plus named negative-control
/// mutant traces, to <c>DURABLE_STORAGE_MESSAGE_DRAIN_TRACES</c> for replay against
/// <c>test/Durable.Storage/specs/message-drain/MessageDrain.qnt</c> (verification.md §5.1, §8.1).
/// </summary>
public sealed class LocalDrainTraceTests
{
    private const int FirstSeed = 1;
    private const int SeedCount = 3;
    private static readonly MessageChannel Channel = SimulatedWakeupMessageProcessor.DefaultChannel;

    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    /// <summary>Exports a conforming LSM single-pass message-drain simulation trace and verifies every recorded step.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void LsmMessageDrainRecordsAValidTrace(int seed)
    {
        Trace? trace = null;
        _ = SeededSimulation.Run(seed, async environment => trace = await SimulateLsmAsync(environment, seed, dropBeforeMessagesCommittedMutant: false));
        Assert.NotNull(trace);
        Assert.Contains("commitSegment(1)", trace.Actions);
        Assert.Contains("dropLedger", trace.Actions);
        Write(trace, string.Create(CultureInfo.InvariantCulture, $"MessageDrainTraceLsm{seed}"));
    }

    /// <summary>Exports a conforming Archive-Logs single-pass message-drain simulation trace and verifies every recorded step.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void ArchiveLogsMessageDrainRecordsAValidTrace(int seed)
    {
        Trace? trace = null;
        _ = SeededSimulation.Run(seed, async environment => trace = await SimulateLogsAsync(environment, seed));
        Assert.NotNull(trace);
        Assert.Contains("commitSegment(1)", trace.Actions);
        Assert.Contains("dropLedger", trace.Actions);
        Write(trace, string.Create(CultureInfo.InvariantCulture, $"MessageDrainTraceLogs{seed}"));
    }

    /// <summary>
    /// Negative-control mutant trace <c>drop-before-messages-committed</c>: <see cref="DrainWorker.DropBeforeMessagesCommittedMutant"/>
    /// commits the manifest and drops the ledger without committing messages, which <c>MessageDrain.qnt</c> must reject.
    /// </summary>
    [Fact]
    public void DropBeforeMessagesCommittedMutantRecordsATraceTheModelRefuses()
    {
        Trace? trace = null;
        _ = SeededSimulation.Run(1, async environment => trace = await SimulateLsmAsync(environment, 1, dropBeforeMessagesCommittedMutant: true));
        Assert.NotNull(trace);
        Assert.Contains("commitSegment(1)", trace.Actions);
        Write(trace, "MessageDrainTraceMutantDropBeforeMessagesCommitted1");
    }

    /// <summary>
    /// Negative-control mutant trace <c>drop-before-commit</c>: attempts <c>dropLedger</c> before committing the segment,
    /// which <c>MessageDrain.qnt</c> must reject.
    /// </summary>
    [Fact]
    public void DropBeforeCommitMutantRecordsATraceTheModelRefuses()
    {
        var trace = new Trace(Mode: "lsm", Seg0: ["S", "K"]);
        trace.Actions.Add("primaryWrite");
        trace.Actions.Add("primaryWrite");
        trace.Actions.Add("closeLedger");
        trace.Actions.Add("dropLedger");
        trace.Actions.Add("commitSegment(1)");
        Write(trace, "MessageDrainTraceMutantDropBeforeCommit1");
    }

    /// <summary>
    /// Negative-control mutant trace <c>skip-unknown-channel</c>: runs <see cref="DrainWorker.SkipUnknownChannelMutant"/>
    /// over a ledger containing an unregistered channel and emits a trace claiming <c>commitSegment</c> without
    /// publishing a sealed shard, which <c>MessageDrain.qnt</c> must reject.
    /// </summary>
    [Fact]
    public void SkipUnknownChannelMutantRecordsATraceTheModelRefuses()
    {
        Trace? trace = null;
        _ = SeededSimulation.Run(1, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var manifest = new ManifestView(objects);
            await using var manifestCommitter = new ManifestCommitter(objects, manifest, SimulationEnvironment.Clock, TimeSpan.Zero);
            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None);
            await ledgers.BootstrapAsync(CancellationToken.None);

            var ledger = ledgers.OpenContaining([1]).Id;
            await ProposeMixedAsync(ledgers, ledger, [1], "x"u8.ToArray(), MessageChannel.Parse("sim.unregistered"), SimulatedWakeupMessageProcessor.Encode(1, set: true));
            await ledgers.ReplaceAsync(ledger, CancellationToken.None);
            var meta = ledgers.Ledger(ledger);
            var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

            var processor = new SimulatedWakeupMessageProcessor();
            var mutantWorker = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, messageProcessors: [processor])
            {
                SkipUnknownChannelMutant = true,
            };
            var outcome = await mutantWorker.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None);
            Assert.IsType<DrainOutcome.Drained>(outcome.Value);
            Assert.Empty(processor.CommittedKeys);

            // Trace reflects what the mutant did: wrote S, closed, scanned, skipped publishShard, committed & dropped!
            trace = new Trace(Mode: "lsm", Seg0: ["S"]);
            trace.Actions.Add("primaryWrite");
            trace.Actions.Add("closeLedger");
            trace.Actions.Add("scanSegment(1, 0)");
            trace.Actions.Add("commitSegment(1)");
            trace.Actions.Add("dropLedger");
        });

        Assert.NotNull(trace);
        Write(trace, "MessageDrainTraceMutantSkipUnknownChannel1");
    }

    private static async Task<Trace> SimulateLsmAsync(SimulationEnvironment environment, int seed, bool dropBeforeMessagesCommittedMutant)
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
        foreach (var op in slots)
        {
            await WriteSlotOpAsync(ledgers, ledger, op);
            trace.Actions.Add("primaryWrite");
        }

        await ledgers.ReplaceAsync(ledger, CancellationToken.None);
        trace.Actions.Add("closeLedger");

        var meta = ledgers.Ledger(ledger);
        var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;
        var hasSurviving = HasSurvivingWakeup(slots);

        var processor = new SimulatedWakeupMessageProcessor();
        var worker = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, messageProcessors: [processor])
        {
            DropBeforeMessagesCommittedMutant = dropBeforeMessagesCommittedMutant,
        };

        var outcome = await worker.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None);
        var drained = Assert.IsType<DrainOutcome.Drained>(outcome.Value);
        Assert.True(drained.Change.Complete);

        trace.Actions.Add("scanSegment(1, 0)");
        if (!dropBeforeMessagesCommittedMutant)
        {
            trace.Actions.Add("publishShard(1)");
        }

        trace.Actions.Add("commitSegment(1)");

        var dropped = await ledgers.Committer.CommitAsync(new LedgerMapChange.Drop(ledger), CancellationToken.None);
        Assert.IsType<LedgerMapCommitOutcome.Applied>(dropped.Value);
        trace.Actions.Add("dropLedger");

        if (hasSurviving && !dropBeforeMessagesCommittedMutant)
        {
            Assert.Contains((byte)1, processor.CommittedKeys);
            Assert.Equal(1, processor.CreatedShards);
            trace.Actions.Add("shardFireWakeup(0)");
        }
        else if (!hasSurviving)
        {
            Assert.Empty(processor.CommittedKeys);
            Assert.Equal(0, processor.CreatedShards);
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
        foreach (var op in slots)
        {
            await WriteSlotOpAsync(ledgers, ledger, op);
            trace.Actions.Add("primaryWrite");
        }

        await ledgers.ReplaceAsync(ledger, CancellationToken.None);
        trace.Actions.Add("closeLedger");

        var meta = ledgers.Ledger(ledger);
        var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;
        var hasSurviving = HasSurvivingWakeup(slots);

        var processor = new SimulatedWakeupMessageProcessor();
        var worker = new ArchiveLogDrainWorker(gate, ledgers.Map, ledgers.Reader, new ArchiveLogOptions(), SimulationEnvironment.Clock, messageProcessors: [processor]);

        var outcome = await worker.DrainAsync(meta, tail, CancellationToken.None);
        Assert.IsType<ArchiveLogDrainOutcome.Complete>(outcome.Value);

        trace.Actions.Add("scanSegment(1, 0)");
        trace.Actions.Add("publishShard(1)");
        trace.Actions.Add("commitSegment(1)");

        var dropped = await worker.SubmitDropAsync(ledger, ledgers.Committer.CommitAsync, CancellationToken.None);
        Assert.IsType<LedgerMapCommitOutcome.Applied>(dropped.Value);
        trace.Actions.Add("dropLedger");

        if (hasSurviving)
        {
            Assert.Contains((byte)1, processor.CommittedKeys);
            Assert.Equal(1, processor.CreatedShards);
            trace.Actions.Add("shardFireWakeup(0)");
        }
        else
        {
            Assert.Empty(processor.CommittedKeys);
            Assert.Equal(0, processor.CreatedShards);
        }

        return trace;
    }

    private static string[] BuildSlots(int seed) => (seed % 4) switch
    {
        1 => ["S", "K"],
        2 => ["S", "C", "K"],
        3 => ["M"],
        _ => ["K", "S", "C", "S"],
    };

    private static bool HasSurvivingWakeup(string[] slots)
    {
        var armed = false;
        foreach (var op in slots)
        {
            if (op is "S" or "M")
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

    private static async Task WriteSlotOpAsync(LiveLedgers ledgers, LedgerId ledger, string op)
    {
        if (op == "K")
        {
            _ = await ledgers.ProposeAsync(ledger, [new LiveRow([1], RowKind.Append, "aaaaaaaa"u8.ToArray())], CancellationToken.None);
            return;
        }

        if (op == "M")
        {
            var writer = ledgers.Registry.TryGet(ledger)!;
            using var batch = await writer.CreateBatchAsync(1, 4096, CancellationToken.None);
            batch.BeginMessage(Channel, 2, SimulatedWakeupMessageProcessor.Encode(1, set: true));
            Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync(batch, CancellationToken.None)).Value);
            return;
        }

        await ProposeMixedAsync(ledgers, ledger, [1], "bbbbbbbb"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(1, set: op == "S"));
    }

    private static async Task ProposeMixedAsync(LiveLedgers ledgers, LedgerId ledger, byte[] journalKey, byte[] journalBody, MessageChannel channel, byte[] messagePayload)
    {
        var writer = ledgers.Registry.TryGet(ledger)!;
        using var batch = await writer.CreateBatchAsync(2, 4096, CancellationToken.None);
        _ = batch.BeginRow(journalKey, RowKind.Append, batch.HeldVersion(journalKey), default, journalBody.Length, journalBody);
        batch.BeginMessage(channel, messagePayload.Length, messagePayload);
        Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync(batch, CancellationToken.None)).Value);
    }

    private static void Write(Trace trace, string module)
    {
        if (Environment.GetEnvironmentVariable("DURABLE_STORAGE_MESSAGE_DRAIN_TRACES") is { Length: > 0 } directory)
        {
            _ = Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, module + ".qnt"), trace.Render(module));
        }
    }

    private sealed class Trace(string Mode, string[] Seg0)
    {
        public List<string> Actions { get; } = [];

        public string Render(string module)
        {
            var sb = new StringBuilder();
            _ = sb.AppendLine("// -*- mode: Bluespec; -*-");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"module {module} {{");
            var slotList = string.Join(", ", Seg0.Select(static s => $"\"{s}\""));
            _ = sb.AppendLine("  import MessageDrain(");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    Mode = \"{Mode}\",");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    Seg0 = [{slotList}],");
            _ = sb.AppendLine("    Seg1 = [],");
            _ = sb.AppendLine("    Drainers = 1,");
            _ = sb.AppendLine("    MaxCrashes = 1,");
            _ = sb.AppendLine("    Mutation = \"none\"");
            _ = sb.AppendLine("  ).* from \"../../specs/message-drain/MessageDrain\"");
            _ = sb.AppendLine();
            _ = sb.AppendLine("  val invariantCheck: bool = TypeOK and WakeupPreserved and NoDropBeforeMessagesCommitted and ClearedInSegmentWritesNothing and IdempotentShardPublish");
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
