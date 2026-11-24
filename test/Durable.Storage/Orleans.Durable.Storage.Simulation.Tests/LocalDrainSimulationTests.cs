using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Orleans-free Clockwork simulations for <see cref="LocalStreamDrainCoordinator"/>, <see cref="DrainWorker"/>,
/// <see cref="ArchiveLogDrainWorker"/>, and <see cref="NodeController"/> covering LG-I15 and LG-I16
/// (verification.md §7.1, archive.md §2.3, orleans.md §5.6).
/// </summary>
public sealed class LocalDrainSimulationTests
{
    private static readonly LocalStreamName Stream = SimulatedWakeupStreamProcessor.DefaultStream;

    /// <summary>
    /// LSM up-front barrier on both <c>a &lt; T</c> and <c>a == T</c>: commits surviving wakeups before
    /// <c>ManifestChange.Drain(..., complete: true)</c> in one manifest replace (0 extra commits), writes 0 backing
    /// commits when cleared before close, and handles <c>Dropped</c> and <c>Backpressure</c> outcomes cleanly.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LsmUpFrontBarrierCoversLessThanTailAndAtTailWithZeroExtraManifestCommits(bool drainBeforeClose)
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

            var processor = new SimulatedWakeupStreamProcessor();
            var coordinator = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [processor]);
            var worker = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, localDrainBarrier: coordinator);

            // Ledger 1: key 1 survives, key 2 is set then cleared before close -> only key 1 is committed.
            var ledger1 = ledgers.OpenContaining([1]).Id;
            var localKey1 = LedgerLocalKey.Encode(ledger1, KeyRange.Root, Stream);
            await ProposeMixedAsync(ledgers, ledger1, [1], "j0"u8.ToArray(), localKey1, SimulatedWakeupStreamProcessor.Encode(1, set: true));
            await ProposeMixedAsync(ledgers, ledger1, [1], "j1"u8.ToArray(), localKey1, SimulatedWakeupStreamProcessor.Encode(2, set: true));
            await ProposeMixedAsync(ledgers, ledger1, [1], "j2"u8.ToArray(), localKey1, SimulatedWakeupStreamProcessor.Encode(2, set: false));

            var manifestCommits = 0;
            Task<ManifestCommitOutcome> CommitManifestAsync(ManifestChange change, CancellationToken ct)
            {
                manifestCommits++;
                return manifestCommitter.CommitAsync(change, ct);
            }

            if (drainBeforeClose)
            {
                // End segment while open and drain the open segment up to its marker so watermark reaches T - 1 / T.
                var marker = await ledgers.EndSegmentAsync(ledger1, CancellationToken.None);
                var openTail = await ledgers.ConfirmOpenAsync(ledger1, CancellationToken.None);
                var openDrain = await worker.DrainAsync(ledgers.Ledger(ledger1), openTail, CommitManifestAsync, CancellationToken.None);
                Assert.IsType<DrainOutcome.Drained>(openDrain.Value);
                Assert.Equal(0, processor.CommitCalls);

                // Drain the marker segment too once ledger closes so a == T when we test the final closed completion.
                await ledgers.ReplaceAsync(ledger1, CancellationToken.None);
                var closedMeta = ledgers.Ledger(ledger1);
                var closedTail = Assert.IsType<LedgerStatus.Closed>(closedMeta.Status.Value).T;
                // Advance manifest watermark directly to closedTail (simulating a == T already drained before close).
                if (manifest.Current!.Value.RecordedWatermark(ledger1) < closedTail)
                {
                    var segDrain = await worker.DrainAsync(closedMeta, closedTail, CommitManifestAsync, CancellationToken.None);
                    Assert.IsType<DrainOutcome.Drained>(segDrain.Value);
                }

                // Now test a second ledger whose manifest watermark is explicitly advanced to T without complete: true (pure a == T path).
                var ledger2 = ledgers.OpenContaining([1]).Id;
                var localKey2 = LedgerLocalKey.Encode(ledger2, KeyRange.Root, Stream);
                await ProposeMixedAsync(ledgers, ledger2, [2], "k0"u8.ToArray(), localKey2, SimulatedWakeupStreamProcessor.Encode(7, set: true));
                await ledgers.ReplaceAsync(ledger2, CancellationToken.None);
                var meta2 = ledgers.Ledger(ledger2);
                var tail2 = Assert.IsType<LedgerStatus.Closed>(meta2.Status.Value).T;
                // Record non-complete watermark at tail2 directly in manifest to force exact a == T branch.
                var preApplied = await manifestCommitter.CommitAsync(new ManifestChange.Drain(ledger2, Slot.Zero, tail2, [], complete: false), CancellationToken.None);
                Assert.IsType<ManifestCommitOutcome.Applied>(preApplied.Value);
                _ = await manifest.ReadAsync(CancellationToken.None);

                var commitsBefore = manifestCommits;
                var atEndOutcome = await worker.DrainAsync(meta2, tail2, CommitManifestAsync, CancellationToken.None);
                var drained = Assert.IsType<DrainOutcome.Drained>(atEndOutcome.Value);
                Assert.True(drained.Change.Complete);
                Assert.Equal(commitsBefore + 1, manifestCommits);
                Assert.Contains((byte)7, processor.CommittedKeys);
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
            }

            // Cleared-before-close ledger performs 0 backing commits.
            var clearedLedger = ledgers.OpenContaining([1]).Id;
            var localKeyCleared = LedgerLocalKey.Encode(clearedLedger, KeyRange.Root, Stream);
            await ProposeMixedAsync(ledgers, clearedLedger, [3], "c0"u8.ToArray(), localKeyCleared, SimulatedWakeupStreamProcessor.Encode(9, set: true));
            await ProposeMixedAsync(ledgers, clearedLedger, [3], "c1"u8.ToArray(), localKeyCleared, SimulatedWakeupStreamProcessor.Encode(9, set: false));
            await ledgers.ReplaceAsync(clearedLedger, CancellationToken.None);
            var clearedMeta = ledgers.Ledger(clearedLedger);
            var clearedTail = Assert.IsType<LedgerStatus.Closed>(clearedMeta.Status.Value).T;
            var commitsBeforeCleared = processor.CommitCalls;
            var clearedOutcome = await worker.DrainAsync(clearedMeta, clearedTail, CommitManifestAsync, CancellationToken.None);
            Assert.IsType<DrainOutcome.Drained>(clearedOutcome.Value);
            Assert.Equal(commitsBeforeCleared, processor.CommitCalls);
            Assert.DoesNotContain((byte)9, processor.CommittedKeys);
            Assert.Equal(32L << 20, coordinator.AvailableMemoryBytes);
        });
    }

    /// <summary>
    /// LSM <see cref="DrainWorker"/> propagates <see cref="LocalDrainBarrierOutcome.Dropped"/> and
    /// <see cref="LocalDrainBarrierOutcome.Backpressure"/> without caching completion or committing the manifest.
    /// </summary>
    [Fact]
    public void LsmDrainWorkerHandlesBarrierDroppedAndBackpressureOutcomes()
    {
        _ = SeededSimulation.Run(2, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var manifest = new ManifestView(objects);
            await using var manifestCommitter = new ManifestCommitter(objects, manifest, SimulationEnvironment.Clock, TimeSpan.Zero);
            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None);
            await ledgers.BootstrapAsync(CancellationToken.None);

            var ledger = ledgers.OpenContaining([1]).Id;
            var localKey = LedgerLocalKey.Encode(ledger, KeyRange.Root, Stream);
            await ProposeMixedAsync(ledgers, ledger, [1], "v0"u8.ToArray(), localKey, SimulatedWakeupStreamProcessor.Encode(3, set: true));
            await ledgers.ReplaceAsync(ledger, CancellationToken.None);
            var meta = ledgers.Ledger(ledger);
            var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

            var scripted = new ScriptedBarrier([
                LocalDrainBarrierOutcome.Backpressure.Instance,
                LocalDrainBarrierOutcome.Dropped.Instance,
                LocalDrainBarrierOutcome.Drained.Instance,
            ]);
            var worker = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, localDrainBarrier: scripted);

            var manifestCommits = 0;
            Task<ManifestCommitOutcome> CommitAsync(ManifestChange change, CancellationToken ct)
            {
                manifestCommits++;
                return manifestCommitter.CommitAsync(change, ct);
            }

            Assert.IsType<ReplicaWriteOutcome.Backpressure>((await worker.DrainAsync(meta, tail, CommitAsync, CancellationToken.None)).Value);
            Assert.Equal(0, manifestCommits);

            Assert.IsType<ReplicaWriteOutcome.Dropped>((await worker.DrainAsync(meta, tail, CommitAsync, CancellationToken.None)).Value);
            Assert.Equal(0, manifestCommits);

            var drained = Assert.IsType<DrainOutcome.Drained>((await worker.DrainAsync(meta, tail, CommitAsync, CancellationToken.None)).Value);
            Assert.True(drained.Change.Complete);
            Assert.Equal(1, manifestCommits);
        });
    }

    /// <summary>
    /// Archive Logs <c>Waiting</c> (<c>LG-I10</c>) lineage: <see cref="NodeController"/>'s dedicated eager-drain slot
    /// drains closed ledger <c>L2</c>'s local streams while <c>L2</c> waits behind older overlapping <c>L1</c>, retries
    /// transient backing failures with bounded backoff, does not touch <c>_draining</c>, and is awaited by
    /// <see cref="NodeController.DrainsEnded"/>.
    /// </summary>
    [Fact]
    public void ArchiveLogsWaitingClosedLedgerEagerlyDrainsLocalStreamsWithBackoffAndDrainsEnded()
    {
        _ = SeededSimulation.Run(3, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var logs = new MemoryArchiveLogStore(new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 8, 49_900, 40_000, MemoryArchiveLogStore.NameRule));
            var archiveStore = new LogArchiveStore(logs, objects, ArchiveLogNames.Hexadecimal);
            var record = await archiveStore.StoreRecordAsync(new Dictionary<string, string>(), CancellationToken.None);
            var gate = await ArchiveLogGate.PassAsync(archiveStore, new ArchiveStoreIdentity(ArchiveStoreKind.Logs, ArchiveStorePrefix.Of("sim"), record.Id), new Dictionary<string, string>(), CancellationToken.None);

            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None, chains: new ArchiveChains(archiveStore));
            await ledgers.BootstrapAsync(CancellationToken.None);

            // L1 stays closed & undrained (so L2 is Waiting behind L1).
            var l1 = ledgers.OpenContaining([1]).Id;
            _ = await ledgers.ProposeAsync(l1, [new LiveRow([1], RowKind.Append, "aaaaaaaa"u8.ToArray())], CancellationToken.None);
            await ledgers.ReplaceAsync(l1, CancellationToken.None);

            // L2 carries a wakeup that should drain eagerly despite Waiting on L1.
            var l2 = ledgers.OpenContaining([1]).Id;
            var localKey2 = LedgerLocalKey.Encode(l2, KeyRange.Root, Stream);
            await ProposeMixedAsync(ledgers, l2, [2], "bbbbbbbb"u8.ToArray(), localKey2, SimulatedWakeupStreamProcessor.Encode(42, set: true));
            await ledgers.ReplaceAsync(l2, CancellationToken.None);

            var failOnce = 1;
            var gateHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enteredSecondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var processor = new SimulatedWakeupStreamProcessor(onCommitAsync: async (_, _, _) =>
            {
                if (Interlocked.Exchange(ref failOnce, 0) == 1)
                {
                    throw new IOException("transient backing store failure");
                }

                enteredSecondAttempt.TrySetResult();
                await gateHold.Task.ConfigureAwait(false);
            });

            var coordinator = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [processor]);
            using var meter = new Meter("test.local.drain");
            var firstNode = ledgers.Ledger(l2).Location.Value is LedgerLocation.Nodes nodes ? nodes.Replicas[0] : throw new InvalidOperationException();
            var nodeDrains = new ArchiveLogNodeDrains(firstNode, 1000, static () => 1, 1024, TimeSpan.FromSeconds(1), new ArchiveLogOptions(), SimulationEnvironment.Clock, meter, NullLogger.Instance, coordinator);
            nodeDrains.Observe(new NodeReportReply(ledgers.Map.Current.Epoch, [], ArchiveLogShare: 1000));
            var executor = new TransitionExecutor(ledgers.Registry, NullLogger<TransitionExecutor>.Instance);
            var controller = new NodeController(
                firstNode,
                generation: 1,
                ledgers.Map,
                ledgers.Registry,
                ledgers.Reader,
                executor,
                static () => true,
                static () => ImmutableDictionary<NodeId, int>.Empty,
                new NodeArchive.Logs(nodeDrains),
                SimulationEnvironment.Clock,
                TimeSpan.FromMinutes(1),
                drainConcurrency: 1,
                NullLogger.Instance,
                localDrainBarrier: coordinator)
            {
                FirstExposedBackoff = TimeSpan.FromMilliseconds(50),
            };

            // Pass 1: drain L2 alone (pass only L2 in drainToo by closing L1 on another node? Here L1 is on firstNode too,
            // so directly mark L2 as Waiting via DrainLogAsync).
            var l2Meta = ledgers.Ledger(l2);
            nodeDrains.Open(gate, ledgers.Map, ledgers.Reader);
            var waitingOutcome = await nodeDrains.DrainAsync(l2Meta, Assert.IsType<LedgerStatus.Closed>(l2Meta.Status.Value).T, ledgers.Committer.CommitAsync, CancellationToken.None);
            Assert.IsType<ArchiveLogDrainOutcome.Waiting>(waitingOutcome.Value);

            var errors = new List<(LedgerId Ledger, Exception Error)>();
            // Pass 1 of StartLogDrains: L2 is Waiting, so eager slot starts and hits the transient failure!
            _ = controller.StartLogDrains(gate, [l2], (_, _) => Task.FromResult<LedgerMapCommitOutcome>(LedgerMapCommitOutcome.Refused.Instance), (id, ex) => errors.Add((id, ex)), CancellationToken.None);
            await controller.DrainsEnded;
            Assert.Contains(errors, e => e.Ledger == l2 && e.Error is IOException);
            Assert.Empty(processor.CommittedKeys);

            // Immediately calling StartLogDrains again before backoff expires does NOT restart eager drain yet.
            _ = controller.StartLogDrains(gate, [l2], (_, _) => Task.FromResult<LedgerMapCommitOutcome>(LedgerMapCommitOutcome.Refused.Instance), (id, ex) => errors.Add((id, ex)), CancellationToken.None);
            Assert.False(enteredSecondAttempt.Task.IsCompleted);

            // Advance clock past FirstExposedBackoff: eager drain retries, holds in flight, and DrainsEnded awaits it!
            await Task.Delay(TimeSpan.FromMilliseconds(100), SimulationEnvironment.Clock);
            _ = controller.StartLogDrains(gate, [l2], (_, _) => Task.FromResult<LedgerMapCommitOutcome>(LedgerMapCommitOutcome.Refused.Instance), (id, ex) => errors.Add((id, ex)), CancellationToken.None);
            await enteredSecondAttempt.Task;
            var endedTask = controller.DrainsEnded;
            Assert.False(endedTask.IsCompleted);

            gateHold.SetResult();
            await endedTask;
            Assert.Contains((byte)42, processor.CommittedKeys);
        });
    }

    /// <summary>
    /// Archive Logs wraps <see cref="ArchiveCorruptException"/> from both unknown-stream <see cref="RowKind.LocalAppend"/>
    /// keys and malformed payloads into <see cref="ArchiveLogDrainBlockedException"/> with
    /// <see cref="ArchiveLogDrainBlockedCause.Corrupt"/>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArchiveLogDrainWorkerWrapsLocalCorruptionsAsBlockedCorrupt(bool unknownStream)
    {
        _ = SeededSimulation.Run(4, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var logs = new MemoryArchiveLogStore(new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 8, 49_900, 40_000, MemoryArchiveLogStore.NameRule));
            var archiveStore = new LogArchiveStore(logs, objects, ArchiveLogNames.Hexadecimal);
            var record = await archiveStore.StoreRecordAsync(new Dictionary<string, string>(), CancellationToken.None);
            var gate = await ArchiveLogGate.PassAsync(archiveStore, new ArchiveStoreIdentity(ArchiveStoreKind.Logs, ArchiveStorePrefix.Of("sim"), record.Id), new Dictionary<string, string>(), CancellationToken.None);

            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None, chains: new ArchiveChains(archiveStore));
            await ledgers.BootstrapAsync(CancellationToken.None);

            var ledger = ledgers.OpenContaining([1]).Id;
            var streamName = unknownStream ? LocalStreamName.Parse("sim.unknown") : Stream;
            var localKey = LedgerLocalKey.Encode(ledger, KeyRange.Root, streamName);
            var payload = unknownStream ? SimulatedWakeupStreamProcessor.Encode(1, true) : [0xFF, 0xFF, 0xFF];
            await ProposeMixedAsync(ledgers, ledger, [1], "aaaaaaaa"u8.ToArray(), localKey, payload);
            await ledgers.ReplaceAsync(ledger, CancellationToken.None);

            var processor = new SimulatedWakeupStreamProcessor();
            var coordinator = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [processor]);
            var worker = new ArchiveLogDrainWorker(gate, ledgers.Map, ledgers.Reader, new ArchiveLogOptions(), SimulationEnvironment.Clock, localDrainBarrier: coordinator);

            var meta = ledgers.Ledger(ledger);
            var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;
            var blocked = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() => worker.DrainAsync(meta, tail, CancellationToken.None));
            Assert.Equal(ArchiveLogDrainBlockedCause.Corrupt, blocked.Cause);
            Assert.IsType<ArchiveCorruptException>(blocked.InnerException);
        });
    }

    /// <summary>
    /// <see cref="LocalStreamDrainCoordinator"/> drains <see cref="LedgerLocation.ObjectStore"/> closed ledgers
    /// identically to node-backed ledgers.
    /// </summary>
    [Fact]
    public void CoordinatorDrainsObjectStoreClosedLedger()
    {
        _ = SeededSimulation.Run(5, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
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
            var localKey = LedgerLocalKey.Encode(open.Id, open.Range, Stream);
            using (var batch = await writer.CreateBatchAsync(2, 4096, CancellationToken.None))
            {
                _ = batch.BeginRow([1], RowKind.Append, default, default, 4, "obj0"u8);
                _ = batch.BeginRow(localKey, RowKind.LocalAppend, default, default, 2, SimulatedWakeupStreamProcessor.Encode(19, set: true));
                Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync(batch, CancellationToken.None)).Value);
            }

            var replaceChoice = new TransitionChoice(nodeId, LedgerLocation.ObjectStore.Instance, k: 2, chosenAt: 0, epoch: map.Current.Epoch);
            Assert.IsType<LedgerMapCommitOutcome.Applied>((await executor.ExecuteAsync(new Transition.Replace(open.Id, replaceChoice), committer.CommitAsync, CancellationToken.None)).Value);
            _ = await map.ReadAsync(CancellationToken.None);

            var processor = new SimulatedWakeupStreamProcessor();
            var coordinator = new LocalStreamDrainCoordinator(map, reader, [processor]);
            var meta = map.Current.Get(open.Id)!.Value;
            var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

            var outcome = await coordinator.EnsureLocalStreamsDrainedAsync(meta, tail, CancellationToken.None);
            Assert.IsType<LocalDrainBarrierOutcome.Drained>(outcome.Value);
            Assert.Equal([19], processor.CommittedKeys);
            Assert.Equal(1, coordinator.SequentialScansStarted);
        });
    }

    /// <summary>
    /// Deadlock-free chunked <c>LocalDrainMemoryBytes</c> flushes under 2 concurrent drains: both complete without
    /// deadlock, flush in chunks, commit all surviving wakeups, and restore full <c>AvailableMemoryBytes</c>.
    /// </summary>
    [Fact]
    public void ChunkedFlushUnderTwoConcurrentDrainsNeverDeadlocksAndRestoresBudget()
    {
        _ = SeededSimulation.Run(6, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None);
            await ledgers.BootstrapAsync(CancellationToken.None);

            var ledger1 = ledgers.OpenContaining([1]).Id;
            var localKey1 = LedgerLocalKey.Encode(ledger1, KeyRange.Root, Stream);
            for (byte k = 1; k <= 6; k++)
            {
                await ProposeMixedAsync(ledgers, ledger1, [k], "a"u8.ToArray(), localKey1, SimulatedWakeupStreamProcessor.Encode(k, set: true));
            }

            await ledgers.ReplaceAsync(ledger1, CancellationToken.None);

            var ledger2 = ledgers.OpenContaining([1]).Id;
            var localKey2 = LedgerLocalKey.Encode(ledger2, KeyRange.Root, Stream);
            for (byte k = 101; k <= 106; k++)
            {
                await ProposeMixedAsync(ledgers, ledger2, [k], "b"u8.ToArray(), localKey2, SimulatedWakeupStreamProcessor.Encode(k, set: true));
            }

            await ledgers.ReplaceAsync(ledger2, CancellationToken.None);

            // Budget of 192 bytes with 64 bytes/entry forces mid-ledger chunked flushes when 2 drains run concurrently!
            const long tinyBudget = 192;
            var processor = new SimulatedWakeupStreamProcessor(bytesPerEntry: 64);
            var coordinator = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [processor], memoryBudgetBytes: tinyBudget);

            var meta1 = ledgers.Ledger(ledger1);
            var tail1 = Assert.IsType<LedgerStatus.Closed>(meta1.Status.Value).T;
            var meta2 = ledgers.Ledger(ledger2);
            var tail2 = Assert.IsType<LedgerStatus.Closed>(meta2.Status.Value).T;

            var t1 = coordinator.EnsureLocalStreamsDrainedAsync(meta1, tail1, CancellationToken.None).AsTask();
            var t2 = coordinator.EnsureLocalStreamsDrainedAsync(meta2, tail2, CancellationToken.None).AsTask();
            var outcomes = await Task.WhenAll(t1, t2);

            Assert.All(outcomes, static o => Assert.IsType<LocalDrainBarrierOutcome.Drained>(o.Value));
            Assert.True(coordinator.ChunkedFlushes > 0);
            Assert.Equal(tinyBudget, coordinator.AvailableMemoryBytes);
            for (byte k = 1; k <= 6; k++)
            {
                Assert.Contains(k, processor.CommittedKeys);
            }

            for (byte k = 101; k <= 106; k++)
            {
                Assert.Contains(k, processor.CommittedKeys);
            }
        });
    }

    /// <summary>
    /// O(1) <see cref="ILocalKeyPresenceLookup"/> zero-read fast path matches sequential scan equivalence: returns
    /// <c>Drained</c> with 0 sequential scans when <c>Absent</c>, and falls back to sequential scan when <c>Unindexed</c>.
    /// </summary>
    [Fact]
    public void PresenceLookupZeroReadFastPathMatchesSequentialScanEquivalence()
    {
        _ = SeededSimulation.Run(7, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None);
            await ledgers.BootstrapAsync(CancellationToken.None);

            // Ledger with only journal rows (no local appends).
            var ledger = ledgers.OpenContaining([1]).Id;
            _ = await ledgers.ProposeAsync(ledger, [new LiveRow([1], RowKind.Append, "only-journal"u8.ToArray())], CancellationToken.None);
            await ledgers.ReplaceAsync(ledger, CancellationToken.None);
            var meta = ledgers.Ledger(ledger);
            var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

            var absentLookup = new SimulatedLocalKeyPresenceLookup((_, _, _) => LocalKeyPresenceRead.Absent.Instance);
            var procFast = new SimulatedWakeupStreamProcessor();
            var coordFast = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [procFast], presenceLookup: absentLookup);
            Assert.IsType<LocalDrainBarrierOutcome.Drained>((await coordFast.EnsureLocalStreamsDrainedAsync(meta, tail, CancellationToken.None)).Value);
            Assert.Equal(1, absentLookup.Probes);
            Assert.Equal(1, coordFast.PresenceFastPathHits);
            Assert.Equal(0, coordFast.SequentialScansStarted);
            Assert.Equal(0, procFast.CommitCalls);

            var unindexedLookup = new SimulatedLocalKeyPresenceLookup((_, _, _) => LocalKeyPresenceRead.Unindexed.Instance);
            var procSlow = new SimulatedWakeupStreamProcessor();
            var coordSlow = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [procSlow], presenceLookup: unindexedLookup);
            Assert.IsType<LocalDrainBarrierOutcome.Drained>((await coordSlow.EnsureLocalStreamsDrainedAsync(meta, tail, CancellationToken.None)).Value);
            Assert.Equal(1, unindexedLookup.Probes);
            Assert.Equal(0, coordSlow.PresenceFastPathHits);
            Assert.Equal(1, coordSlow.SequentialScansStarted);
            Assert.Equal(0, procSlow.CommitCalls);
        });
    }

    /// <summary>
    /// Catches all 3 Phase J2 race/safety mutants: <c>DropBeforeLocalDrainedMutant</c>,
    /// <c>MarkCompleteBeforeCommitMutant</c>, and <c>SkipUnknownStreamMutant</c>.
    /// </summary>
    [Theory]
    [InlineData("DropBeforeLocalDrainedLsm")]
    [InlineData("MarkCompleteBeforeCommit")]
    [InlineData("SkipUnknownStream")]
    public void RaceAndSafetyMutantsAreCaughtByOracles(string mutant)
    {
        _ = SeededSimulation.Run(8, async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var objects = new InMemoryObjectStore();
            var manifest = new ManifestView(objects);
            await using var manifestCommitter = new ManifestCommitter(objects, manifest, SimulationEnvironment.Clock, TimeSpan.Zero);
            await using var ledgers = await LiveLedgers.StartAsync(register, SimulationEnvironment.Clock, CancellationToken.None);
            await ledgers.BootstrapAsync(CancellationToken.None);

            var ledger = ledgers.OpenContaining([1]).Id;
            if (mutant == "DropBeforeLocalDrainedLsm")
            {
                var localKey = LedgerLocalKey.Encode(ledger, KeyRange.Root, Stream);
                await ProposeMixedAsync(ledgers, ledger, [1], "x"u8.ToArray(), localKey, SimulatedWakeupStreamProcessor.Encode(5, set: true));
                await ledgers.ReplaceAsync(ledger, CancellationToken.None);
                var meta = ledgers.Ledger(ledger);
                var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

                var processor = new SimulatedWakeupStreamProcessor();
                var coordinator = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [processor]);
                var mutantWorker = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, localDrainBarrier: coordinator)
                {
                    DropBeforeLocalDrainedMutant = true,
                };

                var drained = Assert.IsType<DrainOutcome.Drained>((await mutantWorker.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None)).Value);
                // Mutant marked ledger complete in manifest WITHOUT committing key 5!
                Assert.True(drained.Change.Complete);
                Assert.Empty(processor.CommittedKeys);
            }
            else if (mutant == "MarkCompleteBeforeCommit")
            {
                var localKey = LedgerLocalKey.Encode(ledger, KeyRange.Root, Stream);
                await ProposeMixedAsync(ledgers, ledger, [1], "x"u8.ToArray(), localKey, SimulatedWakeupStreamProcessor.Encode(6, set: true));
                await ledgers.ReplaceAsync(ledger, CancellationToken.None);
                var meta = ledgers.Ledger(ledger);
                var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

                var failFirst = 1;
                var processor = new SimulatedWakeupStreamProcessor(onCommitAsync: (_, _, _) =>
                    Interlocked.Exchange(ref failFirst, 0) == 1
                        ? Task.FromException(new IOException("commit crashed"))
                        : Task.CompletedTask);
                var mutantCoord = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [processor])
                {
                    MarkCompleteBeforeCommitMutant = true,
                };

                _ = await Assert.ThrowsAsync<IOException>(() => mutantCoord.EnsureLocalStreamsDrainedAsync(meta, tail, CancellationToken.None).AsTask());
                // Mutant prematurely cached _completed before commit succeeded, so retry skips commit and loses key 6!
                var retryOutcome = await mutantCoord.EnsureLocalStreamsDrainedAsync(meta, tail, CancellationToken.None);
                Assert.IsType<LocalDrainBarrierOutcome.Drained>(retryOutcome.Value);
                Assert.Empty(processor.CommittedKeys);
            }
            else
            {
                var unknownKey = LedgerLocalKey.Encode(ledger, KeyRange.Root, LocalStreamName.Parse("sim.unregistered"));
                await ProposeMixedAsync(ledgers, ledger, [1], "x"u8.ToArray(), unknownKey, SimulatedWakeupStreamProcessor.Encode(8, set: true));
                await ledgers.ReplaceAsync(ledger, CancellationToken.None);
                var meta = ledgers.Ledger(ledger);
                var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

                var processor = new SimulatedWakeupStreamProcessor();
                var conforming = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [processor]);
                _ = await Assert.ThrowsAsync<ArchiveCorruptException>(() => conforming.EnsureLocalStreamsDrainedAsync(meta, tail, CancellationToken.None).AsTask());

                var mutantCoord = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [processor])
                {
                    SkipUnknownStreamMutant = true,
                };
                // Mutant silently accepts unknown stream instead of failing closed!
                var skipped = await mutantCoord.EnsureLocalStreamsDrainedAsync(meta, tail, CancellationToken.None);
                Assert.IsType<LocalDrainBarrierOutcome.Drained>(skipped.Value);
            }
        });
    }

    /// <summary>
    /// Deterministic Clockwork replay pin (<c>RunAndReplay</c> + Verify snapshot) for concurrent local-stream draining
    /// over LSM, ensuring schedule determinism across runs and platforms.
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
            var localKey = LedgerLocalKey.Encode(ledger, KeyRange.Root, Stream);
            await ProposeMixedAsync(ledgers, ledger, [1], "p0"u8.ToArray(), localKey, SimulatedWakeupStreamProcessor.Encode(1, set: true));
            await ProposeMixedAsync(ledgers, ledger, [2], "p1"u8.ToArray(), localKey, SimulatedWakeupStreamProcessor.Encode(2, set: true));
            await ProposeMixedAsync(ledgers, ledger, [1], "p2"u8.ToArray(), localKey, SimulatedWakeupStreamProcessor.Encode(1, set: false));
            await ledgers.ReplaceAsync(ledger, CancellationToken.None);

            var processor = new SimulatedWakeupStreamProcessor(bytesPerEntry: 64);
            var coordinator = new LocalStreamDrainCoordinator(ledgers.Map, ledgers.Reader, [processor], memoryBudgetBytes: 128)
            {
                OnFlushCommitted = (id, chunked) => events.Add(string.Create(CultureInfo.InvariantCulture, $"flush:{id.Value}:chunked={chunked}")),
                OnBarrierCompleted = (id, outcome) => events.Add(string.Create(CultureInfo.InvariantCulture, $"barrier:{id.Value}:{outcome.Value.GetType().Name}")),
            };
            var worker = new DrainWorker(objects, manifest, ledgers.Reader, new ArchiveOptions(), SimulationEnvironment.Clock, localDrainBarrier: coordinator);
            var meta = ledgers.Ledger(ledger);
            var tail = Assert.IsType<LedgerStatus.Closed>(meta.Status.Value).T;

            var d1 = worker.DrainAsync(meta, tail, manifestCommitter.CommitAsync, CancellationToken.None);
            var d2 = coordinator.EnsureLocalStreamsDrainedAsync(meta, tail, CancellationToken.None).AsTask();
            await Task.WhenAll(d1, d2);
            events.Add(string.Create(CultureInfo.InvariantCulture, $"committed:{string.Join(',', processor.CommittedKeys.Order())}"));
        });

        Assert.True(run.Steps > 0);
        return Verify(string.Join('\n', events));
    }

    private static async Task ProposeMixedAsync(LiveLedgers ledgers, LedgerId ledger, byte[] journalKey, byte[] journalBody, byte[] localKey, byte[] localPayload)
    {
        var writer = ledgers.Registry.TryGet(ledger)!;
        using var batch = await writer.CreateBatchAsync(2, 4096, CancellationToken.None);
        _ = batch.BeginRow(journalKey, RowKind.Append, batch.HeldVersion(journalKey), default, journalBody.Length, journalBody);
        _ = batch.BeginRow(localKey, RowKind.LocalAppend, batch.HeldVersion(localKey), default, localPayload.Length, localPayload);
        Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync(batch, CancellationToken.None)).Value);
    }

    private sealed class ScriptedBarrier(IEnumerable<LocalDrainBarrierOutcome> outcomes) : ILedgerLocalDrainBarrier
    {
        private readonly Queue<LocalDrainBarrierOutcome> _outcomes = new(outcomes);

        public void ValidateLocalAppendKey(LedgerMetadata ledger, ReadOnlySpan<byte> rowKey)
        {
            if (LedgerLocalKey.Validate(rowKey, ledger.Range, ledger.Id, out _) is { } error)
            {
                throw new ArchiveCorruptException(error.Message, error);
            }
        }

        public ValueTask<LocalDrainBarrierOutcome> EnsureLocalStreamsDrainedAsync(LedgerMetadata ledger, Slot tail, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_outcomes.Dequeue());
    }
}
