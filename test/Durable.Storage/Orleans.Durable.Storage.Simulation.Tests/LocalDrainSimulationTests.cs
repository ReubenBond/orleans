using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Orleans-free Clockwork simulations for unified single-pass <c>DrainScan</c> message draining across
/// <see cref="DrainWorker"/> (LSM) and <see cref="ArchiveLogDrainWorker"/> (Archive Logs) covering LG-I15 and LG-I16
/// (verification.md §7.1, archive.md §2.3, orleans.md §5.15).
/// </summary>
public sealed class LocalDrainSimulationTests
{
    private static readonly MessageChannel Channel = SimulatedWakeupMessageProcessor.DefaultChannel;

    /// <summary>
    /// LSM single-pass <c>DrainScan</c> commits surviving wakeups before <c>ManifestChange.Drain</c> in one manifest
    /// replace (0 extra manifest commits), writes 0 sealed shards on same-segment clear, handles multi-segment open
    /// draining + cross-segment set-then-clear, and commits message-only segments (<c>Count == 0 &amp;&amp; HasMessages</c>).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LsmSinglePassDrainCommitsSurvivingMessagesAndSkipsClearedAndHandlesMessageOnlySegments(bool drainOpenSegmentFirst)
    {
        _ = SeededSimulation.Run(1, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var manifest = new ManifestView(objects);
            await using var manifestCommitter = new ManifestCommitter(objects, manifest, SimulationEnvironment.Clock, TimeSpan.Zero);
            var writerOptions = new LedgerWriterOptions(1 << 20, 1 << 10, TimeSpan.FromDays(1), LedgerWriterOptions.DefaultMaxProposalBytes);
            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None, writerOptions);
            await ledgers.BootstrapAsync(CancellationToken.None);

            var processor = new SimulatedWakeupMessageProcessor();
            var worker = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, messageProcessors: [processor]);

            var manifestCommits = 0;
            Task<ManifestCommitOutcome> CommitManifestAsync(ManifestChange change, CancellationToken ct)
            {
                manifestCommits++;
                return manifestCommitter.CommitAsync(change, ct);
            }

            // Ledger 1: key 1 survives, key 2 is set then cleared in the SAME segment -> only key 1 is published!
            var ledger1 = ledgers.OpenContaining([1]).Id;
            await ProposeMixedAsync(ledgers, ledger1, [1], "j0"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(1, set: true));
            await ProposeMixedAsync(ledgers, ledger1, [1], "j1"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(2, set: true));
            await ProposeMixedAsync(ledgers, ledger1, [1], "j2"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(2, set: false));

            if (drainOpenSegmentFirst)
            {
                // End segment 0 while open, drain segment 0 (commits key 1!), then clear key 1 in segment 1 and close.
                _ = await ledgers.EndSegmentAsync(ledger1, CancellationToken.None);
                var openTail = await ledgers.ConfirmOpenAsync(ledger1, CancellationToken.None);
                var openDrain = await worker.DrainAsync(ledgers.Ledger(ledger1), openTail, CommitManifestAsync, CancellationToken.None);
                Assert.IsType<DrainOutcome.Drained>(openDrain.Value);
                Assert.Equal([1], processor.CommittedKeys);
                Assert.Equal(1, processor.CreatedShards);

                // Segment 1 clears key 1 -> 0 new shards published when segment 1 drains after close!
                await ProposeMixedAsync(ledgers, ledger1, [1], "j3"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(1, set: false));
                await ledgers.ReplaceAsync(ledger1, CancellationToken.None);
                var closedMeta = ledgers.Ledger(ledger1);
                var closedTail = Assert.IsType<LedgerStatus.Closed>(closedMeta.Status.Value).T;
                var seg1Outcome = await worker.DrainAsync(closedMeta, closedTail, CommitManifestAsync, CancellationToken.None);
                var drained = Assert.IsType<DrainOutcome.Drained>(seg1Outcome.Value);
                Assert.True(drained.Change.Complete);
                Assert.Equal(1, processor.CreatedShards);
            }
            else
            {
                await ledgers.ReplaceAsync(ledger1, CancellationToken.None);
                var closedMeta = ledgers.Ledger(ledger1);
                var closedTail = Assert.IsType<LedgerStatus.Closed>(closedMeta.Status.Value).T;
                var outcome = await worker.DrainAsync(closedMeta, closedTail, CommitManifestAsync, CancellationToken.None);
                var drained = Assert.IsType<DrainOutcome.Drained>(outcome.Value);
                Assert.True(drained.Change.Complete);
                Assert.Equal(1, manifestCommits);
                Assert.Equal([1], processor.CommittedKeys);
                Assert.Equal(1, processor.CreatedShards);
            }

            // Cleared-in-segment ledger performs 0 sealed shard writes.
            var clearedLedger = ledgers.OpenContaining([1]).Id;
            await ProposeMixedAsync(ledgers, clearedLedger, [3], "c0"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(9, set: true));
            await ProposeMixedAsync(ledgers, clearedLedger, [3], "c1"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(9, set: false));
            await ledgers.ReplaceAsync(clearedLedger, CancellationToken.None);
            var clearedMeta = ledgers.Ledger(clearedLedger);
            var clearedTail = Assert.IsType<LedgerStatus.Closed>(clearedMeta.Status.Value).T;
            var shardsBeforeCleared = processor.CreatedShards;
            var clearedOutcome = await worker.DrainAsync(clearedMeta, clearedTail, CommitManifestAsync, CancellationToken.None);
            Assert.IsType<DrainOutcome.Drained>(clearedOutcome.Value);
            Assert.Equal(shardsBeforeCleared, processor.CreatedShards);
            Assert.DoesNotContain((byte)9, processor.CommittedKeys);

            // Message-only segment (0 archive rows, 1 wakeup message) still commits messages and advances manifest!
            var msgOnlyLedger = ledgers.OpenContaining([1]).Id;
            await ProposeMessageOnlyAsync(ledgers, msgOnlyLedger, Channel, SimulatedWakeupMessageProcessor.Encode(77, set: true));
            await ledgers.ReplaceAsync(msgOnlyLedger, CancellationToken.None);
            var msgOnlyMeta = ledgers.Ledger(msgOnlyLedger);
            var msgOnlyTail = Assert.IsType<LedgerStatus.Closed>(msgOnlyMeta.Status.Value).T;
            var msgOnlyOutcome = await worker.DrainAsync(msgOnlyMeta, msgOnlyTail, CommitManifestAsync, CancellationToken.None);
            var msgOnlyDrained = Assert.IsType<DrainOutcome.Drained>(msgOnlyOutcome.Value);
            Assert.True(msgOnlyDrained.Change.Complete);
            Assert.Contains((byte)77, processor.CommittedKeys);
        });
    }

    /// <summary>
    /// Archive Logs single-pass <c>DrainScan</c> commits surviving wakeups before creating the drain marker, writes 0
    /// sealed shards on same-segment clear, and handles message-only segments (<c>writes.Length == 0 &amp;&amp; HasMessages</c>)
    /// cleanly so the ledger can drop.
    /// </summary>
    [Fact]
    public void ArchiveLogsSinglePassDrainCommitsSurvivingSkipsClearedAndHandlesMessageOnlySegments()
    {
        _ = SeededSimulation.Run(2, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var logs = new MemoryArchiveLogStore(new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 8, 49_900, 40_000, MemoryArchiveLogStore.NameRule));
            var archiveStore = new LogArchiveStore(logs, objects, ArchiveLogNames.Hexadecimal);
            var record = await archiveStore.StoreRecordAsync(new Dictionary<string, string>(), CancellationToken.None);
            var gate = await ArchiveLogGate.PassAsync(archiveStore, new ArchiveStoreIdentity(ArchiveStoreKind.Logs, ArchiveStorePrefix.Of("sim"), record.Id), new Dictionary<string, string>(), CancellationToken.None);

            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None, chains: new ArchiveChains(archiveStore));
            await ledgers.BootstrapAsync(CancellationToken.None);

            var processor = new SimulatedWakeupMessageProcessor();
            var worker = new ArchiveLogDrainWorker(gate, ledgers.Map, ledgers.Reader, new ArchiveLogOptions(), SimulationEnvironment.Clock, messageProcessors: [processor]);

            // Ledger 1: surviving wakeup key 11 + cleared key 12 -> only key 11 published.
            var l1 = ledgers.OpenContaining([1]).Id;
            await ProposeMixedAsync(ledgers, l1, [1], "aaaaaaaa"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(11, set: true));
            await ProposeMixedAsync(ledgers, l1, [1], "bbbbbbbb"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(12, set: true));
            await ProposeMixedAsync(ledgers, l1, [1], "cccccccc"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(12, set: false));
            await ledgers.ReplaceAsync(l1, CancellationToken.None);
            var m1 = ledgers.Ledger(l1);
            Assert.IsType<ArchiveLogDrainOutcome.Complete>((await worker.DrainAsync(m1, Assert.IsType<LedgerStatus.Closed>(m1.Status.Value).T, CancellationToken.None)).Value);
            Assert.IsType<LedgerMapCommitOutcome.Applied>((await worker.SubmitDropAsync(l1, ledgers.Committer.CommitAsync, CancellationToken.None)).Value);
            Assert.Equal([11], processor.CommittedKeys);
            Assert.Equal(1, processor.CreatedShards);

            // Ledger 2: message-only segment (0 archive rows) -> still publishes key 22 and writes drain marker!
            var l2 = ledgers.OpenContaining([1]).Id;
            await ProposeMessageOnlyAsync(ledgers, l2, Channel, SimulatedWakeupMessageProcessor.Encode(22, set: true));
            await ledgers.ReplaceAsync(l2, CancellationToken.None);
            var m2 = ledgers.Ledger(l2);
            Assert.IsType<ArchiveLogDrainOutcome.Complete>((await worker.DrainAsync(m2, Assert.IsType<LedgerStatus.Closed>(m2.Status.Value).T, CancellationToken.None)).Value);
            Assert.IsType<LedgerMapCommitOutcome.Applied>((await worker.SubmitDropAsync(l2, ledgers.Committer.CommitAsync, CancellationToken.None)).Value);
            Assert.Contains((byte)22, processor.CommittedKeys);
            Assert.Equal(2, processor.CreatedShards);
        });
    }

    /// <summary>
    /// Both Archive Logs and LSM fail closed on unregistered message channels and malformed wakeup payloads:
    /// LSM throws <see cref="ArchiveCorruptException"/> and Archive Logs wraps it in
    /// <see cref="ArchiveLogDrainBlockedException"/> with <see cref="ArchiveLogDrainBlockedCause.Corrupt"/>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArchiveLogAndLsmDrainWorkersFailClosedOnUnknownChannelAndCorruptPayload(bool unknownChannel)
    {
        _ = SeededSimulation.Run(4, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var manifest = new ManifestView(objects);
            await using var manifestCommitter = new ManifestCommitter(objects, manifest, SimulationEnvironment.Clock, TimeSpan.Zero);
            var logs = new MemoryArchiveLogStore(new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 8, 49_900, 40_000, MemoryArchiveLogStore.NameRule));
            var archiveStore = new LogArchiveStore(logs, objects, ArchiveLogNames.Hexadecimal);
            var record = await archiveStore.StoreRecordAsync(new Dictionary<string, string>(), CancellationToken.None);
            var gate = await ArchiveLogGate.PassAsync(archiveStore, new ArchiveStoreIdentity(ArchiveStoreKind.Logs, ArchiveStorePrefix.Of("sim"), record.Id), new Dictionary<string, string>(), CancellationToken.None);

            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None, chains: new ArchiveChains(archiveStore));
            await ledgers.BootstrapAsync(CancellationToken.None);

            var ledger = ledgers.OpenContaining([1]).Id;
            var ch = unknownChannel ? MessageChannel.Parse("sim.unknown") : Channel;
            var payload = unknownChannel ? SimulatedWakeupMessageProcessor.Encode(1, true) : [0xFF, 0xFF, 0xFF];
            await ProposeMixedAsync(ledgers, ledger, [1], "aaaaaaaa"u8.ToArray(), ch, payload);
            await ledgers.ReplaceAsync(ledger, CancellationToken.None);

            var processor = new SimulatedWakeupMessageProcessor();
            var lsmWorker = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, messageProcessors: [processor]);
            var logsWorker = new ArchiveLogDrainWorker(gate, ledgers.Map, ledgers.Reader, new ArchiveLogOptions(), SimulationEnvironment.Clock, messageProcessors: [processor]);

            var meta = ledgers.Ledger(ledger);
            var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

            _ = await Assert.ThrowsAsync<ArchiveCorruptException>(() => lsmWorker.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None));

            var blocked = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() => logsWorker.DrainAsync(meta, tail, CancellationToken.None));
            Assert.Equal(ArchiveLogDrainBlockedCause.Corrupt, blocked.Cause);
            Assert.IsType<ArchiveCorruptException>(blocked.InnerException);
        });
    }

    /// <summary>
    /// Single-pass message drain handles <see cref="LedgerLocation.ObjectStore"/> closed ledgers identically to
    /// node-backed ledgers.
    /// </summary>
    [Fact]
    public void SinglePassDrainHandlesObjectStoreClosedLedger()
    {
        _ = SeededSimulation.Run(5, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var manifest = new ManifestView(objects);
            await using var manifestCommitter = new ManifestCommitter(objects, manifest, SimulationEnvironment.Clock, TimeSpan.Zero);
            var objectStore = ObjectLedgerStore.Over(objects, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.PerSlot, CacheBytes = 64 * 1024 }, SimulationEnvironment.Clock);
            _ = await objectStore.StoreRecordAsync(CancellationToken.None);

            var nodeId = NodeId.New();
            var map = await LedgerMapView.OpenAsync(register, CancellationToken.None);
            await using var committer = new LedgerMapCommitter(register, int.MaxValue, map);
            using var transport = new Orleans.Durable.Storage.Engine.Ledgers.Memory.InMemoryLedgerReplicaTransport();
            await using var reader = new LedgerReader(map, transport, SimulationEnvironment.Clock, nodeId)
            {
                ObjectStoreProvider = () => objectStore,
            };
            await using var registry = new LedgerWriterRegistry(reader, 1 << 20, LedgerWriterOptions.Default, SimulationEnvironment.Clock, NullLoggerFactory.Instance);
            var executor = new TransitionExecutor(registry, NullLogger<TransitionExecutor>.Instance);

            var bootChoice = new TransitionChoice(nodeId, LedgerLocation.ObjectStore.Instance, k: 2, chosenAt: 0, epoch: map.Current.Epoch);
            Assert.IsType<LedgerMapCommitOutcome.Applied>((await executor.ExecuteAsync(new Transition.Bootstrap(bootChoice), committer.CommitAsync, CancellationToken.None)).Value);
            await registry.ReconciledAsync(map.Current.Epoch, CancellationToken.None);

            var open = map.Current.Ledgers.Single(static l => l.IsOpen);
            Assert.IsType<LedgerLocation.ObjectStore>(open.Location.Value);
            var writer = registry.TryGet(open.Id)!;
            using (var batch = await writer.CreateBatchAsync(2, 4096, CancellationToken.None))
            {
                _ = batch.BeginRow([1], RowKind.Append, default, default, 4, "obj0"u8);
                batch.BeginMessage(Channel, 2, SimulatedWakeupMessageProcessor.Encode(19, set: true));
                Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync(batch, CancellationToken.None)).Value);
            }

            var replaceChoice = new TransitionChoice(nodeId, LedgerLocation.ObjectStore.Instance, k: 2, chosenAt: 0, epoch: map.Current.Epoch);
            Assert.IsType<LedgerMapCommitOutcome.Applied>((await executor.ExecuteAsync(new Transition.Replace(open.Id, replaceChoice), committer.CommitAsync, CancellationToken.None)).Value);
            _ = await map.ReadAsync(CancellationToken.None);

            var processor = new SimulatedWakeupMessageProcessor();
            var worker = new DrainWorker(objects, manifest, reader, new ArchiveOptions(), SimulationEnvironment.Clock, messageProcessors: [processor]);
            var meta = map.Current.Get(open.Id)!.Value;
            var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

            var outcome = await worker.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None);
            var drained = Assert.IsType<DrainOutcome.Drained>(outcome.Value);
            Assert.True(drained.Change.Complete);
            Assert.Equal([19], processor.CommittedKeys);
            Assert.Equal(1, processor.CreatedShards);
        });
    }

    /// <summary>
    /// Under tight <c>DrainMemoryBytes</c>, <c>DrainScan</c> triggers <c>OverBudget</c> → <c>Alone</c> rescan, clears
    /// in-memory message folds cleanly on reset, and commits every surviving wakeup exactly once without deadlock.
    /// </summary>
    [Fact]
    public void TightDrainMemoryBudgetResetsMessageFoldsOnOverBudgetAndCommitsAccurately()
    {
        _ = SeededSimulation.Run(6, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var manifest = new ManifestView(objects);
            await using var manifestCommitter = new ManifestCommitter(objects, manifest, SimulationEnvironment.Clock, TimeSpan.Zero);
            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None);
            await ledgers.BootstrapAsync(CancellationToken.None);

            var ledger1 = ledgers.OpenContaining([1]).Id;
            for (byte k = 1; k <= 4; k++)
            {
                await ProposeMixedAsync(ledgers, ledger1, [k], "a"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(k, set: true));
            }

            await ledgers.ReplaceAsync(ledger1, CancellationToken.None);

            var ledger2 = ledgers.OpenContaining([1]).Id;
            for (byte k = 101; k <= 104; k++)
            {
                await ProposeMixedAsync(ledgers, ledger2, [k], "b"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(k, set: true));
            }

            await ledgers.ReplaceAsync(ledger2, CancellationToken.None);

            // Tight DrainMemoryBytes forces OverBudget -> Alone rescan when 2 drains run concurrently (each needs ~96 MiB < 128 MiB, 2 together need ~192 MiB > 128 MiB)!
            var processor = new SimulatedWakeupMessageProcessor(bytesPerEntry: 24L << 20);
            var options = new ArchiveOptions { DrainMemoryBytes = 128L << 20 };
            var worker = new DrainWorker(objects, manifest, ledgers.Reader, options, SimulationEnvironment.Clock, messageProcessors: [processor]);

            var m1 = ledgers.Ledger(ledger1);
            var m2 = ledgers.Ledger(ledger2);
            var t1 = worker.DrainAsync(m1, Assert.IsType<LedgerStatus.Closed>(m1.Status.Value).T, manifestCommitter.CommitAsync, CancellationToken.None);
            var t2 = worker.DrainAsync(m2, Assert.IsType<LedgerStatus.Closed>(m2.Status.Value).T, manifestCommitter.CommitAsync, CancellationToken.None);
            var outcomes = await Task.WhenAll(t1, t2);

            Assert.All(outcomes, static o => Assert.IsType<DrainOutcome.Drained>(o.Value));
            Assert.Equal(2, processor.CreatedShards);
            for (byte k = 1; k <= 4; k++)
            {
                Assert.Contains(k, processor.CommittedKeys);
            }

            for (byte k = 101; k <= 104; k++)
            {
                Assert.Contains(k, processor.CommittedKeys);
            }
        });
    }

    /// <summary>
    /// Transient commit failure leaves manifest/marker uncommitted so retry publishes and commits cleanly, and
    /// concurrent racing drainers colliding on deterministic <c>(ledger, segmentStart)</c> create at most 1 shard.
    /// </summary>
    [Fact]
    public void TransientCommitFailureRetriesSafelyAndRacingDrainersAreIdempotent()
    {
        _ = SeededSimulation.Run(7, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var manifest = new ManifestView(objects);
            await using var manifestCommitter = new ManifestCommitter(objects, manifest, SimulationEnvironment.Clock, TimeSpan.Zero);
            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None);
            await ledgers.BootstrapAsync(CancellationToken.None);

            var ledger = ledgers.OpenContaining([1]).Id;
            await ProposeMixedAsync(ledgers, ledger, [1], "retry"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(33, set: true));
            await ledgers.ReplaceAsync(ledger, CancellationToken.None);
            var meta = ledgers.Ledger(ledger);
            var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

            var failOnce = 1;
            var processor = new SimulatedWakeupMessageProcessor(onCommitAsync: (_, _, _, _) =>
                Interlocked.Exchange(ref failOnce, 0) == 1
                    ? Task.FromException(new IOException("transient publish failure"))
                    : Task.CompletedTask);

            var worker1 = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, messageProcessors: [processor]);
            var worker2 = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, messageProcessors: [processor]);

            // First attempt fails during CommitMessagesAsync -> manifest stays unadvanced!
            _ = await Assert.ThrowsAsync<IOException>(() => worker1.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None));
            Assert.Equal(Slot.Zero, manifest.Current!.Value.RecordedWatermark(ledger));
            Assert.Equal(0, processor.CreatedShards);

            // Two racing drainers both scan & publish the exact same segment -> 1 CreatedShard, 1 DuplicateShard!
            var outcomes = await Task.WhenAll(
                worker1.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None),
                worker2.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None));
            Assert.Contains(outcomes, static o => o.Value is DrainOutcome.Drained { Change.Complete: true });
            Assert.Equal(1, processor.CreatedShards);
            Assert.Equal(1, processor.DuplicateShards);
            Assert.Equal([33], processor.CommittedKeys);
        });
    }

    /// <summary>
    /// Catches race/safety mutants across both LSM and Archive Logs:
    /// <see cref="DrainWorker.DropBeforeMessagesCommittedMutant"/>,
    /// <see cref="ArchiveLogDrainWorker.DropBeforeMessagesCommittedMutant"/>,
    /// <see cref="DrainWorker.SkipUnknownChannelMutant"/>, and
    /// <see cref="ArchiveLogDrainWorker.SkipUnknownChannelMutant"/>.
    /// </summary>
    [Theory]
    [InlineData("DropBeforeMessagesCommittedLsm")]
    [InlineData("DropBeforeMessagesCommittedLogs")]
    [InlineData("SkipUnknownChannelLsm")]
    [InlineData("SkipUnknownChannelLogs")]
    public void RaceAndSafetyMutantsAreCaughtByOracles(string mutant)
    {
        _ = SeededSimulation.Run(8, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var manifest = new ManifestView(objects);
            await using var manifestCommitter = new ManifestCommitter(objects, manifest, SimulationEnvironment.Clock, TimeSpan.Zero);
            var logs = new MemoryArchiveLogStore(new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 8, 49_900, 40_000, MemoryArchiveLogStore.NameRule));
            var archiveStore = new LogArchiveStore(logs, objects, ArchiveLogNames.Hexadecimal);
            var record = await archiveStore.StoreRecordAsync(new Dictionary<string, string>(), CancellationToken.None);
            var gate = await ArchiveLogGate.PassAsync(archiveStore, new ArchiveStoreIdentity(ArchiveStoreKind.Logs, ArchiveStorePrefix.Of("sim"), record.Id), new Dictionary<string, string>(), CancellationToken.None);
            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None, chains: new ArchiveChains(archiveStore));
            await ledgers.BootstrapAsync(CancellationToken.None);

            var ledger = ledgers.OpenContaining([1]).Id;
            var ch = mutant.StartsWith("SkipUnknownChannel", StringComparison.Ordinal) ? MessageChannel.Parse("sim.unregistered") : Channel;
            await ProposeMixedAsync(ledgers, ledger, [1], "x"u8.ToArray(), ch, SimulatedWakeupMessageProcessor.Encode(5, set: true));
            await ledgers.ReplaceAsync(ledger, CancellationToken.None);
            var meta = ledgers.Ledger(ledger);
            var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

            var processor = new SimulatedWakeupMessageProcessor();
            if (mutant == "DropBeforeMessagesCommittedLsm")
            {
                var mutantWorker = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, messageProcessors: [processor])
                {
                    DropBeforeMessagesCommittedMutant = true,
                };
                var drained = Assert.IsType<DrainOutcome.Drained>((await mutantWorker.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None)).Value);
                Assert.True(drained.Change.Complete);
                Assert.Empty(processor.CommittedKeys);
            }
            else if (mutant == "DropBeforeMessagesCommittedLogs")
            {
                var mutantWorker = new ArchiveLogDrainWorker(gate, ledgers.Map, ledgers.Reader, new ArchiveLogOptions(), SimulationEnvironment.Clock, messageProcessors: [processor])
                {
                    DropBeforeMessagesCommittedMutant = true,
                };
                Assert.IsType<ArchiveLogDrainOutcome.Complete>((await mutantWorker.DrainAsync(meta, tail, CancellationToken.None)).Value);
                Assert.Empty(processor.CommittedKeys);
            }
            else if (mutant == "SkipUnknownChannelLsm")
            {
                var conforming = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, messageProcessors: [processor]);
                _ = await Assert.ThrowsAsync<ArchiveCorruptException>(() => conforming.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None));

                var mutantWorker = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, messageProcessors: [processor])
                {
                    SkipUnknownChannelMutant = true,
                };
                Assert.IsType<DrainOutcome.Drained>((await mutantWorker.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None)).Value);
            }
            else
            {
                var conforming = new ArchiveLogDrainWorker(gate, ledgers.Map, ledgers.Reader, new ArchiveLogOptions(), SimulationEnvironment.Clock, messageProcessors: [processor]);
                _ = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() => conforming.DrainAsync(meta, tail, CancellationToken.None));

                var mutantWorker = new ArchiveLogDrainWorker(gate, ledgers.Map, ledgers.Reader, new ArchiveLogOptions(), SimulationEnvironment.Clock, messageProcessors: [processor])
                {
                    SkipUnknownChannelMutant = true,
                };
                Assert.IsType<ArchiveLogDrainOutcome.Complete>((await mutantWorker.DrainAsync(meta, tail, CancellationToken.None)).Value);
            }
        });
    }

    /// <summary>
    /// Deterministic Clockwork replay pin (<c>RunAndReplay</c> + Verify snapshot) for concurrent racing message-drain
    /// workers over LSM, ensuring schedule determinism and idempotent shard creation across runs and platforms.
    /// </summary>
    [Fact]
    public Task ConcurrentLocalDrainReplaysDeterministicallyAndIsPinned()
    {
        var events = new List<string>();
        var run = SeededSimulation.RunAndReplay(11, async environment =>
        {
            events.Clear();
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var manifest = new ManifestView(objects);
            await using var manifestCommitter = new ManifestCommitter(objects, manifest, SimulationEnvironment.Clock, TimeSpan.Zero);
            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None);
            await ledgers.BootstrapAsync(CancellationToken.None);

            var ledger = ledgers.OpenContaining([1]).Id;
            await ProposeMixedAsync(ledgers, ledger, [1], "p0"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(1, set: true));
            await ProposeMixedAsync(ledgers, ledger, [2], "p1"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(2, set: true));
            await ProposeMixedAsync(ledgers, ledger, [1], "p2"u8.ToArray(), Channel, SimulatedWakeupMessageProcessor.Encode(1, set: false));
            await ledgers.ReplaceAsync(ledger, CancellationToken.None);

            var processor = new SimulatedWakeupMessageProcessor(
                bytesPerEntry: 64,
                onCommitAsync: (id, seg, keys, _) =>
                {
                    events.Add(string.Create(CultureInfo.InvariantCulture, $"publish:{id.Value}:{seg.Start.Value}:{string.Join(',', keys)}"));
                    return Task.CompletedTask;
                });
            var worker1 = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, messageProcessors: [processor]);
            var worker2 = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, messageProcessors: [processor]);
            var meta = ledgers.Ledger(ledger);
            var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

            var d1 = worker1.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None);
            var d2 = worker2.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None);
            await Task.WhenAll(d1, d2);
            events.Add(string.Create(CultureInfo.InvariantCulture, $"created={processor.CreatedShards}:dups={processor.DuplicateShards}:committed={string.Join(',', processor.CommittedKeys.Order())}"));
        });

        Assert.True(run.Steps > 0);
        return Verify(string.Join('\n', events));
    }

    private static async Task ProposeMixedAsync(LiveLedgers ledgers, LedgerId ledger, byte[] journalKey, byte[] journalBody, MessageChannel channel, byte[] messagePayload)
    {
        var writer = ledgers.Registry.TryGet(ledger)!;
        using var batch = await writer.CreateBatchAsync(2, 4096, CancellationToken.None);
        _ = batch.BeginRow(journalKey, RowKind.Append, batch.HeldVersion(journalKey), default, journalBody.Length, journalBody);
        batch.BeginMessage(channel, messagePayload.Length, messagePayload);
        Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync(batch, CancellationToken.None)).Value);
    }

    private static async Task ProposeMessageOnlyAsync(LiveLedgers ledgers, LedgerId ledger, MessageChannel channel, byte[] messagePayload)
    {
        var writer = ledgers.Registry.TryGet(ledger)!;
        using var batch = await writer.CreateBatchAsync(1, 4096, CancellationToken.None);
        batch.BeginMessage(channel, messagePayload.Length, messagePayload);
        Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync(batch, CancellationToken.None)).Value);
    }
}
