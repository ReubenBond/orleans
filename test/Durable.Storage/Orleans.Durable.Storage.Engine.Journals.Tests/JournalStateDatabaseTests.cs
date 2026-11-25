using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals.Memory;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// <see cref="JournalStateDatabase"/>'s per-node key indexes per live ledger (journals.md §8.3, JS-I6, JS-I9):
/// wired as <see cref="LedgerApplyLoop"/>'s <see cref="LedgerApplier"/> and read by both <see cref="JournalStore"/> and
/// <see cref="ArchiveReader"/>. Covers oracle parity, point-read slot counts, restarts with lagging durable watermarks,
/// takeovers and handoffs whose predecessor indexes lag <c>T</c>, ranges handed away and back, one shared scan for a
/// non-replicated closed predecessor, <c>unknown-ordinal</c> stale entries, SHA-256 hash-collision filtering, and code
/// mutants (<see cref="JournalStateDatabase.SkipCatchUpMutant"/> and <see cref="JournalStateDatabase.StaleOrdinalMutant"/>).
/// </summary>
public sealed class JournalStateDatabaseTests : IDisposable
{
    private static readonly TimeSpan Signal = TimeSpan.FromSeconds(10);
    private static readonly NodeId NodeA = new(new Guid("00000000-0000-0000-0000-00000000000a"));
    private static readonly NodeId NodeB = new(new Guid("00000000-0000-0000-0000-00000000000b"));
    private static readonly NodeId NodeC = new(new Guid("00000000-0000-0000-0000-00000000000c"));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Indexed reads through both <see cref="JournalStore"/> and <see cref="ArchiveReader"/> match the oracle across
    /// multi-segment histories of <c>Append</c>, <c>Replace</c>, <c>Delete</c>, and <c>Expire</c>, point-read only the
    /// queried key's distinct defining and row slots, and read zero slots for an absent key.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task IndexedReadsMatchTheOracleAndReadOnlyTheKeysSlots()
    {
        var token = TestContext.Current.CancellationToken;
        await using var env = new MultiNodeEnvironment();
        var storage = new InMemoryJournalIndex();
        var options = ManualFlushOptions();
        await using var nodeA = await env.OpenNodeAsync(NodeA, storage, options);
        await env.BootstrapAsync(nodeA, [NodeA]);
        await using var store = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions { TombstoneRetention = TimeSpan.Zero }, TimeProvider.System, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, token);

        // Interleave writes to "alpha", "beta", and "gamma" across two segments so each key occupies only a small subset of slots.
        var alphaV = JournalVersion.Origin;
        var betaV = JournalVersion.Origin;
        var gammaV = JournalVersion.Origin;

        alphaV = await AppendAsync(store, "alpha", alphaV, "a1-");
        betaV = await AppendAsync(store, "beta", betaV, "b1");
        gammaV = await AppendAsync(store, "gamma", gammaV, "g1");
        alphaV = await AppendAsync(store, "alpha", alphaV, "a2");
        betaV = await ReplaceAsync(store, "beta", betaV, "B2");
        gammaV = await DeleteAsync(store, "gamma", gammaV);

        var openLedger = nodeA.Map.Current.Find(JournalRowKey.Encode("alpha").Span)!.Value.Open.Id;
        Assert.True((await nodeA.Registry.TryGet(openLedger)!.EndSegmentAsync(token)).TryGetValue(out ProposalOutcome.Durable _));

        betaV = await AppendAsync(store, "beta", betaV, "+b3");
        alphaV = await AppendAsync(store, "alpha", alphaV, "-a3");
        gammaV = await ExpireAsync(store, "gamma", gammaV);

        // Carry tail so every slot is visible to open-ledger readers, and let the apply loop index through the tail.
        var tail = Assert.IsType<LedgerTail>((await nodeA.Reader.ReadTailAsync(nodeA.Map.Current.Get(openLedger)!.Value, token)).Value);
        Assert.True(tail.ContiguousTail > Slot.Zero);
        nodeA.ApplyLoop.Wake(openLedger);
        await WaitUntilAsync(() => nodeA.StateDb.Watermark(openLedger) >= tail.ContiguousTail, Signal);

        // Read "alpha" via JournalStore (backed by JournalStateDatabase) and verify oracle + point-read count.
        nodeA.Transport.ResetCounts();
        var (vA, existsA, contentA) = await ReadJournalAsync(store, "alpha");
        Assert.True(existsA);
        Assert.Equal(alphaV, vA);
        Assert.Equal("a1-a2-a3", contentA);
        var alphaSlots = nodeA.Transport.ReadSlotsOf(openLedger);
        Assert.True(alphaSlots.Count <= 3, $"Expected at most 3 distinct slots for 'alpha', read {alphaSlots.Count}: [{string.Join(", ", alphaSlots)}].");
        Assert.True(alphaSlots.Count < tail.ContiguousTail.Value, "Indexed read must not scan the whole ledger.");

        // Read "beta" via ArchiveReader directly with the same KeyIndex (returns all 3 raw history rows in order),
        // and via JournalStore (which applies Replace semantics to yield "B2+b3").
        var archiveReader = new ArchiveReader(nodeA.Reader, env.Store, TimeProvider.System, nodeA.StateDb.KeyIndex);
        nodeA.Transport.ResetCounts();
        using (var betaRead = await archiveReader.ReadAsync(JournalRowKey.Encode("beta").Memory, token))
        {
            Assert.NotNull(betaRead);
            Assert.Equal("b1B2+b3", ReadArchiveContent(betaRead));
            Assert.Equal(3, betaRead.Rows.Length);
            Assert.True(archiveReader.Counts.IndexedLedgers.Value > 0);
            Assert.True(archiveReader.Counts.PointReads.Value <= 3);
        }

        var (vB, existsB, contentB) = await ReadJournalAsync(store, "beta");
        Assert.True(existsB);
        Assert.Equal(betaV, vB);
        Assert.Equal("B2+b3", contentB);

        // Absent key: 0 slot reads!
        nodeA.Transport.ResetCounts();
        var (vMissing, existsMissing, contentMissing) = await ReadJournalAsync(store, "never-written");
        Assert.False(existsMissing);
        Assert.Equal(JournalVersion.Origin, vMissing);
        Assert.Equal(string.Empty, contentMissing);
        Assert.Empty(nodeA.Transport.ReadSlotsOf(openLedger));

        // Expired key: matches oracle (not exists, empty content).
        var (vG, existsG, contentG) = await ReadJournalAsync(store, "gamma");
        Assert.False(existsG);
        Assert.Equal(gammaV, vG);
        Assert.Equal(string.Empty, contentG);
    }

    /// <summary>
    /// JS-I9 across a restart with a lagging durable watermark: the database flushes mid-segment, crashes without
    /// flushing later batches, reopens at its durable watermark, rewinds to the segment start, waits for catch-up before
    /// answering, and returns the oracle's state; <see cref="JournalStateDatabase.SkipCatchUpMutant"/> deterministically
    /// fails the oracle.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task RestartWithLaggingDurableWatermarkCatchesUpBeforeAnsweringAndMutantFails()
    {
        var token = TestContext.Current.CancellationToken;
        await using var env = new MultiNodeEnvironment();
        var storage = new InMemoryJournalIndex();
        var options = ManualFlushOptions();
        JournalVersion expectedVersion;
        LedgerId ledgerId;
        Slot durableAfterFlush;

        // First boot: write two rows, flush durably mid-segment, write two more rows in the same segment + a new segment, then "crash" without flushing!
        await using (var node1 = await env.OpenNodeAsync(NodeA, storage, options))
        {
            await env.BootstrapAsync(node1, [NodeA]);
            await using var store1 = new JournalStore(node1.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, node1.StateDb);
            await node1.Registry.ReconciledAsync(node1.Map.Current.Epoch, token);
            ledgerId = node1.Map.Current.Find(JournalRowKey.Encode("k").Span)!.Value.Open.Id;

            var v = await AppendAsync(store1, "k", JournalVersion.Origin, "part1-");
            v = await AppendAsync(store1, "k", v, "part2-");

            // Wait for apply loop to index slots 0..1, then flush durably at slot 2 (mid-segment!).
            var tail1 = Assert.IsType<LedgerTail>((await node1.Reader.ReadTailAsync(node1.Map.Current.Get(ledgerId)!.Value, token)).Value);
            node1.ApplyLoop.Wake(ledgerId);
            await WaitUntilAsync(() => node1.StateDb.Watermark(ledgerId) >= tail1.ContiguousTail, Signal);
            await node1.StateDb.FlushAsync(token);
            durableAfterFlush = node1.StateDb.DurableWatermark(ledgerId);
            Assert.True(durableAfterFlush > Slot.Zero);

            // Append more rows in the SAME segment and across a segment boundary, without flushing storage.
            v = await AppendAsync(store1, "k", v, "part3-");
            Assert.True((await node1.Registry.TryGet(ledgerId)!.EndSegmentAsync(token)).TryGetValue(out ProposalOutcome.Durable _));
            v = await AppendAsync(store1, "k", v, "part4");
            expectedVersion = v;

            // Carry tail on replicas before crash.
            _ = await node1.Reader.ReadTailAsync(node1.Map.Current.Get(ledgerId)!.Value, token);
        }

        // Second boot: replace L1 with L2 (as a restarted primary does), and verify SkipCatchUpMutant fails BEFORE catch-up!
        LedgerId ledger2;
        await using (var mutantNode = await env.OpenNodeAsync(NodeA, storage, options, startApplyLoop: false))
        {
            Assert.Equal(durableAfterFlush, mutantNode.StateDb.Watermark(ledgerId));
            Assert.Equal(durableAfterFlush, mutantNode.StateDb.DurableWatermark(ledgerId));
            await env.ReplaceAsync(mutantNode, ledgerId, [NodeA]);
            ledger2 = mutantNode.Map.Current.Find(JournalRowKey.Encode("k").Span)!.Value.Open.Id;

            mutantNode.StateDb.SkipCatchUpMutant = true;
            await using var mutantStore = new JournalStore(mutantNode.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, mutantNode.StateDb);
            await mutantNode.Registry.ReconciledAsync(mutantNode.Map.Current.Epoch, token);
            var (_, _, mutantContent) = await ReadJournalAsync(mutantStore, "k");
            Assert.Equal("part1-part2-", mutantContent);
        }

        // Third boot (real recovery): reopen over the same lagging storage with LedgerApplyLoop attached, take over L2->L3, read immediately.
        await using var recovered = await env.OpenNodeAsync(NodeA, storage, options);
        await env.ReplaceAsync(recovered, ledger2, [NodeA]);
        await using var recoveredStore = new JournalStore(recovered.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, recovered.StateDb);
        await recovered.Registry.ReconciledAsync(recovered.Map.Current.Epoch, token);
        var (version, exists, content) = await ReadJournalAsync(recoveredStore, "k");
        Assert.True(exists);
        Assert.Equal(expectedVersion, version);
        Assert.Equal("part1-part2-part3-part4", content);
    }

    /// <summary>
    /// Takeover/handoff whose closed predecessor ledger's index on the new primary lags <c>T</c>: the read waits until
    /// the predecessor index catches up to <c>T</c> (JS-I9) and returns the oracle's state;
    /// <see cref="JournalStateDatabase.SkipCatchUpMutant"/> deterministically fails the oracle.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TakeoverAndHandoffWaitForPredecessorIndexToReachTAndMutantFails()
    {
        var token = TestContext.Current.CancellationToken;
        // Bootstrap L1 replicated on NodeA (primary), NodeB, and NodeC (3 replicas).
        await using var env = new MultiNodeEnvironment();
        var storageB = new InMemoryJournalIndex();
        var options = ManualFlushOptions();

        JournalVersion versionA;
        LedgerId ledger1;
        await using (var nodeA = await env.OpenNodeAsync(NodeA, new InMemoryJournalIndex(), options))
        {
            await env.BootstrapAsync(nodeA, [NodeA, NodeB, NodeC]);
            await using var storeA = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeA.StateDb);
            await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, token);
            ledger1 = nodeA.Map.Current.Find(JournalRowKey.Encode("order").Span)!.Value.Open.Id;
            versionA = await AppendAsync(storeA, "order", JournalVersion.Origin, "first-");
            versionA = await AppendAsync(storeA, "order", versionA, "second-");
            Assert.True((await nodeA.Registry.TryGet(ledger1)!.EndSegmentAsync(token)).TryGetValue(out ProposalOutcome.Durable _));
            versionA = await AppendAsync(storeA, "order", versionA, "third");
        }

        // First verify SkipCatchUpMutant on NodeB (L1 is replicated on NodeB, watermark is 0 < T) fails the oracle!
        LedgerId ledger2;
        await using (var mutantB = await env.OpenNodeAsync(NodeB, storageB, options, startApplyLoop: false))
        {
            Assert.Equal(Slot.Zero, mutantB.StateDb.Watermark(ledger1));
            await env.ReplaceAsync(mutantB, ledger1, [NodeB]);
            ledger2 = mutantB.Map.Current.Find(JournalRowKey.Encode("order").Span)!.Value.Open.Id;
            mutantB.StateDb.SkipCatchUpMutant = true;
            await using var mutantStoreB = new JournalStore(mutantB.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, mutantB.StateDb);
            await mutantB.Registry.ReconciledAsync(mutantB.Map.Current.Epoch, token);
            var (_, existsMutant, contentMutant) = await ReadJournalAsync(mutantStoreB, "order");
            Assert.False(existsMutant);
            Assert.Equal(string.Empty, contentMutant);
        }

        // Real NodeB starts with L1's index at 0 < T; gate L1 replica reads briefly so the read arrives while L1 lags T!
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var nodeB = await env.OpenNodeAsync(
            NodeB,
            storageB,
            options,
            onRead: (ledger, slot) =>
            {
                if (ledger == ledger1)
                {
                    _ = arrived.TrySetResult();
                    return gate.Task;
                }

                return Task.CompletedTask;
            });
        await env.ReplaceAsync(nodeB, ledger2, [NodeB]);
        await using var storeB = new JournalStore(nodeB.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeB.StateDb);
        await nodeB.Registry.ReconciledAsync(nodeB.Map.Current.Epoch, token);

        var reading = ReadJournalAsync(storeB, "order");
        await arrived.Task.WaitAsync(Signal, TimeProvider.System, token);
        Assert.False(reading.IsCompleted, "Read must wait for lagging closed predecessor index to catch up to T (JS-I9).");

        // Release the gate: apply loop finishes L1 to T and the read returns the exact oracle state!
        gate.SetResult();
        var (vB, existsB, contentB) = await reading.WaitAsync(Signal, TimeProvider.System, token);
        Assert.True(existsB);
        Assert.Equal(versionA, vB);
        Assert.Equal("first-second-third", contentB);

        // NodeB can also continue writing in L3 on top of the caught-up state.
        var vB2 = await AppendAsync(storeB, "order", vB, "-fourth");
        var (vBFinal, _, contentBFinal) = await ReadJournalAsync(storeB, "order");
        Assert.Equal(vB2, vBFinal);
        Assert.Equal("first-second-third-fourth", contentBFinal);
    }

    /// <summary>
    /// A range handed away from NodeA to NodeB and later handed back to NodeA: NodeA's old ledger drops and leaves its
    /// state database, NodeB's closed ledger (not replicated on NodeA) is indexed by one shared predecessor scan, and
    /// NodeA's new open ledger is indexed by <see cref="LedgerApplyLoop"/>; reads match the oracle throughout.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task RangeHandedAwayAndBackDropsOldIndexScansNonReplicatedPredecessorOnceAndMatchesOracle()
    {
        var token = TestContext.Current.CancellationToken;
        await using var env = new MultiNodeEnvironment();
        var storageA = new InMemoryJournalIndex();
        var storageB = new InMemoryJournalIndex();
        var options = ManualFlushOptions();

        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA, options);
        await env.BootstrapAsync(nodeA, [NodeA]);
        await using var storeA = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, token);

        // Phase 1: NodeA owns L1, writes "k1" and "k2".
        var ledger1 = nodeA.Map.Current.Find(JournalRowKey.Encode("k1").Span)!.Value.Open.Id;
        var v1 = await AppendAsync(storeA, "k1", JournalVersion.Origin, "fromA1-");
        var v2 = await AppendAsync(storeA, "k2", JournalVersion.Origin, "k2A1-");

        // Phase 2: NodeB takes over L1 -> L2 (replicated only on NodeB), drains & drops L1, writes more to "k1" and "k2".
        LedgerId ledger2;
        await using (var nodeB = await env.OpenNodeAsync(NodeB, storageB, options))
        {
            await env.ReplaceAsync(nodeB, ledger1, [NodeB]);
            await env.DrainClosedLedgerAsync(nodeB.Reader, ledger1);
            await env.DropLedgerAsync(nodeB, ledger1);

            // Wait for NodeA's LedgerApplyLoop to remove dropped L1 from NodeA's state database!
            _ = await nodeA.Map.ReadAsync(token);
            await WaitUntilAsync(() => !nodeA.StateDb.HasLedger(ledger1), Signal);
            Assert.Equal(Slot.Zero, nodeA.StateDb.Watermark(ledger1));

            await using var storeB = new JournalStore(nodeB.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeB.StateDb);
            await nodeB.Registry.ReconciledAsync(nodeB.Map.Current.Epoch, token);
            ledger2 = nodeB.Map.Current.Find(JournalRowKey.Encode("k1").Span)!.Value.Open.Id;

            // L1 was drained & dropped, so its version rebased on NodeB; read current versions then append.
            var (cur1, _, c1) = await ReadJournalAsync(storeB, "k1");
            Assert.Equal("fromA1-", c1);
            var (cur2, _, c2) = await ReadJournalAsync(storeB, "k2");
            Assert.Equal("k2A1-", c2);

            v1 = await AppendAsync(storeB, "k1", cur1, "fromB2-");
            v2 = await AppendAsync(storeB, "k2", cur2, "k2B2-");
        }

        // Phase 3: NodeA takes range BACK (L2 closes at T on NodeB; L3 opens on NodeA) and reads across L1 (store), L2 (non-replicated predecessor!), and L3 (open on NodeA).
        _ = await nodeA.Map.ReadAsync(token);
        await env.ReplaceAsync(nodeA, ledger2, [NodeA]);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, token);

        var (after1, exists1, content1) = await ReadJournalAsync(storeA, "k1");
        Assert.True(exists1);
        Assert.Equal(v1, after1);
        Assert.Equal("fromA1-fromB2-", content1);

        // Append in L3 on NodeA and read both keys; L2 was scanned exactly once!
        var final1 = await AppendAsync(storeA, "k1", after1, "fromA3");
        var (v1Final, _, content1Final) = await ReadJournalAsync(storeA, "k1");
        var (v2Final, _, content2Final) = await ReadJournalAsync(storeA, "k2");
        Assert.Equal(final1, v1Final);
        Assert.Equal("fromA1-fromB2-fromA3", content1Final);
        Assert.Equal(v2, v2Final);
        Assert.Equal("k2A1-k2B2-", content2Final);
        Assert.Equal(1, nodeA.StateDb.PredecessorScansStarted);
    }

    /// <summary>
    /// A closed predecessor ledger this node does not replicate uses exactly one shared scan across concurrent readers;
    /// cancelling one caller cancels only its wait; later reads reuse the cached index without rescanning; dropping the
    /// ledger removes its entries and source; and <see cref="JournalStateDatabase.SkipCatchUpMutant"/> deterministically
    /// fails the oracle.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task NonReplicatedClosedPredecessorUsesOneSharedScanAcrossConcurrentReadersSurvivesOneCancellationAndRemovesOnDrop()
    {
        var token = TestContext.Current.CancellationToken;
        await using var env = new MultiNodeEnvironment();
        var options = ManualFlushOptions();
        LedgerId ledger1;
        await using (var nodeA = await env.OpenNodeAsync(NodeA, new InMemoryJournalIndex(), options))
        {
            await env.BootstrapAsync(nodeA, [NodeA]);
            await using var storeA = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeA.StateDb);
            await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, token);
            ledger1 = nodeA.Map.Current.Find(JournalRowKey.Encode("x").Span)!.Value.Open.Id;
            _ = await AppendAsync(storeA, "x", JournalVersion.Origin, "val-x");
            _ = await AppendAsync(storeA, "y", JournalVersion.Origin, "val-y");
            _ = await AppendAsync(storeA, "z", JournalVersion.Origin, "val-z");
        }

        // First verify SkipCatchUpMutant on NodeB (taking over L1 -> L2; L1 is only on NodeA) fails the oracle!
        LedgerId ledger2;
        await using (var mutantB = await env.OpenNodeAsync(NodeB, new InMemoryJournalIndex(), options))
        {
            await env.ReplaceAsync(mutantB, ledger1, [NodeB]);
            ledger2 = mutantB.Map.Current.Find(JournalRowKey.Encode("x").Span)!.Value.Open.Id;
            mutantB.StateDb.SkipCatchUpMutant = true;
            await using var mutantStoreB = new JournalStore(mutantB.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, mutantB.StateDb);
            await mutantB.Registry.ReconciledAsync(mutantB.Map.Current.Epoch, token);
            var (_, existsMutant, _) = await ReadJournalAsync(mutantStoreB, "x");
            Assert.False(existsMutant);
        }

        // Gate L1 reads on NodeB so three concurrent readers all join the ONE shared scan!
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var nodeB = await env.OpenNodeAsync(
            NodeB,
            new InMemoryJournalIndex(),
            options,
            onRead: (ledger, slot) =>
            {
                if (ledger == ledger1)
                {
                    _ = started.TrySetResult();
                    return gate.Task;
                }

                return Task.CompletedTask;
            });
        await env.ReplaceAsync(nodeB, ledger2, [NodeB]);
        await using var storeB = new JournalStore(nodeB.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeB.StateDb);
        await nodeB.Registry.ReconciledAsync(nodeB.Map.Current.Epoch, token);

        using var cancelOne = CancellationTokenSource.CreateLinkedTokenSource(token);
        var readX = storeB.ReadAsync(JournalRowKey.Encode("x"), cancelOne.Token);
        var readY = ReadJournalAsync(storeB, "y");
        var readZ = ReadJournalAsync(storeB, "z");

        await started.Task.WaitAsync(Signal, TimeProvider.System, token);
        Assert.Equal(1, nodeB.StateDb.PredecessorScansStarted);

        // Cancel reader X while the shared scan is in flight: X cancels, the shared scan continues for Y and Z!
        await cancelOne.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await readX);

        gate.SetResult();
        var (_, existsY, contentY) = await readY.WaitAsync(Signal, TimeProvider.System, token);
        var (_, existsZ, contentZ) = await readZ.WaitAsync(Signal, TimeProvider.System, token);
        Assert.True(existsY);
        Assert.Equal("val-y", contentY);
        Assert.True(existsZ);
        Assert.Equal("val-z", contentZ);
        Assert.Equal(1, nodeB.StateDb.PredecessorScansStarted);

        // A later read of "x" reuses the completed index without starting a second scan and reads only x's slot!
        nodeB.Transport.ResetCounts();
        var (_, existsX, contentX) = await ReadJournalAsync(storeB, "x");
        Assert.True(existsX);
        Assert.Equal("val-x", contentX);
        Assert.Equal(1, nodeB.StateDb.PredecessorScansStarted);
        Assert.Single(nodeB.Transport.ReadSlotsOf(ledger1));

        // Drain and drop L1: NodeB's map watcher removes L1's non-replicated index entries and watermark!
        await env.DrainClosedLedgerAsync(nodeB.Reader, ledger1);
        await env.DropLedgerAsync(nodeB, ledger1);
        _ = await nodeB.Map.ReadAsync(token);
        await WaitUntilAsync(() => !nodeB.StateDb.HasLedger(ledger1), Signal);
        Assert.Equal(Slot.Zero, nodeB.StateDb.Watermark(ledger1));
    }

    /// <summary>
    /// A stale or corrupt ordinal in a key-index entry is rejected as <c>unknown-ordinal</c>
    /// (<see cref="BatchView.ReasonUnknownOrdinal"/>) by <see cref="ArchiveReader"/> and surfaced as
    /// <see cref="JournalStoreCorruptException"/> by <see cref="JournalStore"/>, with zero leaked buffers.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task StaleOrdinalIndexEntryIsRejectedAsUnknownOrdinal()
    {
        var token = TestContext.Current.CancellationToken;
        await using var env = new MultiNodeEnvironment();
        var storage = new InMemoryJournalIndex();
        await using var nodeA = await env.OpenNodeAsync(NodeA, storage, ManualFlushOptions());
        await env.BootstrapAsync(nodeA, [NodeA]);
        nodeA.StateDb.StaleOrdinalMutant = true;
        await using var store = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, token);

        var v = await AppendAsync(store, "stale", JournalVersion.Origin, "v1-");
        _ = await AppendAsync(store, "stale", v, "v2");

        var archiveReader = new ArchiveReader(nodeA.Reader, env.Store, TimeProvider.System, nodeA.StateDb.KeyIndex);
        var invalid = await Assert.ThrowsAsync<InvalidDataException>(async () => _ = await archiveReader.ReadAsync(JournalRowKey.Encode("stale").Memory, token));
        Assert.Equal(BatchView.ReasonUnknownOrdinal, DecodeRejection.ReasonOf(invalid));

        var corrupt = await Assert.ThrowsAsync<JournalStoreCorruptException>(async () => _ = await store.ReadAsync(JournalRowKey.Encode("stale"), token));
        var inner = Assert.IsType<InvalidDataException>(corrupt.InnerException);
        Assert.Equal(BatchView.ReasonUnknownOrdinal, DecodeRejection.ReasonOf(inner));
    }

    /// <summary>
    /// If two keys ever collide on their 32-byte SHA-256 index prefix, <see cref="ArchiveReader"/> checks the defining
    /// row's exact key bytes and ignores the colliding key's rows without corrupting the target key's history.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task HashCollisionInKeyIndexIsFilteredByDefiningRowKey()
    {
        var token = TestContext.Current.CancellationToken;
        await using var env = new MultiNodeEnvironment();
        var storage = new InMemoryJournalIndex();
        await using var nodeA = await env.OpenNodeAsync(NodeA, storage, ManualFlushOptions());
        await env.BootstrapAsync(nodeA, [NodeA]);
        await using var store = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, token);

        var ledgerId = nodeA.Map.Current.Find(JournalRowKey.Encode("real").Span)!.Value.Open.Id;
        var vReal = await AppendAsync(store, "real", JournalVersion.Origin, "real-value");
        var vOther = await AppendAsync(store, "other", JournalVersion.Origin, "other-v1");
        _ = await AppendAsync(store, "other", vOther, "other-v2");

        var tail = Assert.IsType<LedgerTail>((await nodeA.Reader.ReadTailAsync(nodeA.Map.Current.Get(ledgerId)!.Value, token)).Value);
        nodeA.ApplyLoop.Wake(ledgerId);
        await WaitUntilAsync(() => nodeA.StateDb.Watermark(ledgerId) >= tail.ContiguousTail, Signal);

        // Inject synthetic key_index entries under SHA256("real") pointing at BOTH "other"'s defining row (slot 1)
        // AND "other"'s referencing row (slot 2, DefiningSlot = 1 != Tag.Slot = 2, ordinal 1).
        var realHash = JournalKeyHash.Compute(JournalRowKey.Encode("real").Span);
        await nodeA.StateDb.Index.ApplyBatchAsync(
            new JournalIndexBatch(
                ledgerId,
                nodeA.StateDb.Watermark(ledgerId),
                new JournalIndexKeyWrite[]
                {
                    new(realHash, new Slot(1), 0, new Slot(1), 1),
                    new(realHash, new Slot(2), 0, new Slot(1), 1),
                },
                ReadOnlyMemory<JournalIndexTombstoneWrite>.Empty),
            token);

        var (version, exists, content) = await ReadJournalAsync(store, "real");
        Assert.True(exists);
        Assert.Equal(vReal, version);
        Assert.Equal("real-value", content);
    }

    /// <summary>
    /// Reading an open ledger through <see cref="JournalStore"/> without pre-waking or pre-waiting for
    /// <see cref="LedgerApplyLoop"/> wakes the loop via <see cref="LedgerApplyLoop.Wake"/> and waits until the index
    /// reaches the read's tail (JS-I9); <see cref="JournalStateDatabase.SkipCatchUpMutant"/> on a lagging open ledger
    /// deterministically fails the oracle.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task OpenLedgerReadWakesApplyLoopAndWaitsForCatchUpWithoutPreWake()
    {
        var token = TestContext.Current.CancellationToken;
        await using var env = new MultiNodeEnvironment();
        var storage = new InMemoryJournalIndex();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = 1;
        await using var nodeA = await env.OpenNodeAsync(
            NodeA,
            storage,
            ManualFlushOptions(),
            onRead: (_, _) =>
            {
                if (Volatile.Read(ref gated) != 0)
                {
                    _ = blocked.TrySetResult();
                    return gate.Task;
                }

                return Task.CompletedTask;
            });
        await env.BootstrapAsync(nodeA, [NodeA]);
        await using var store = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, token);

        var v1 = await AppendAsync(store, "open-k", JournalVersion.Origin, "first-");
        var v2 = await AppendAsync(store, "open-k", v1, "second");

        // Mutant check while apply loop is still gated at watermark 0 on the open ledger: skipping catch-up misses the writes!
        nodeA.StateDb.SkipCatchUpMutant = true;
        var (_, mutantExists, mutantContent) = await ReadJournalAsync(store, "open-k");
        Assert.False(mutantExists);
        Assert.Equal(string.Empty, mutantContent);
        nodeA.StateDb.SkipCatchUpMutant = false;

        // Real path: start a read without calling ApplyLoop.Wake or WaitUntilAsync; WaitForReplicatedCatchUpAsync wakes the loop!
        var readTask = ReadJournalAsync(store, "open-k");
        await blocked.Task.WaitAsync(Signal, token);
        Assert.False(readTask.IsCompleted);

        // Release replica reads so LedgerApplyLoop finishes catch-up and unblocks the reader.
        Volatile.Write(ref gated, 0);
        gate.SetResult();

        var (version, exists, content) = await readTask;
        Assert.True(exists);
        Assert.Equal(v2, version);
        Assert.Equal("first-second", content);
    }

    /// <summary>
    /// Opening <see cref="JournalStateDatabase"/> over a corrupt <see cref="IJournalIndex"/> resets it
    /// automatically and rebuilds key indexes cleanly from the live ledgers.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task CorruptDatabaseIsResetOnOpenAndRebuildsIndexes()
    {
        var token = TestContext.Current.CancellationToken;
        await using var env = new MultiNodeEnvironment();
        var storage = new InMemoryJournalIndex();
        var options = ManualFlushOptions();
        LedgerId ledger1;
        JournalVersion expectedVersion;

        await using (var node1 = await env.OpenNodeAsync(NodeA, storage, options))
        {
            await env.BootstrapAsync(node1, [NodeA]);
            await using var store1 = new JournalStore(node1.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, node1.StateDb);
            await node1.Registry.ReconciledAsync(node1.Map.Current.Epoch, token);
            ledger1 = node1.Map.Current.Find(JournalRowKey.Encode("corrupt-k").Span)!.Value.Open.Id;

            var v = await AppendAsync(store1, "corrupt-k", JournalVersion.Origin, "alpha-");
            expectedVersion = await AppendAsync(store1, "corrupt-k", v, "beta");

            var tail = Assert.IsType<LedgerTail>((await node1.Reader.ReadTailAsync(node1.Map.Current.Get(ledger1)!.Value, token)).Value);
            node1.ApplyLoop.Wake(ledger1);
            await WaitUntilAsync(() => node1.StateDb.Watermark(ledger1) >= tail.ContiguousTail, Signal);
            await node1.StateDb.FlushAsync(token);
        }

        // Corrupt the underlying journal index before reopening!
        storage.Corrupt();

        await using var node2 = await env.OpenNodeAsync(NodeA, storage, options);
        Assert.False(node2.StateDb.IsCorrupt);
        Assert.Equal(Slot.Zero, node2.StateDb.DurableWatermark(ledger1));
        Assert.Empty(storage.GetDurableWatermarks());

        await env.ReplaceAsync(node2, ledger1, [NodeA]);
        await using var store2 = new JournalStore(node2.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, node2.StateDb);
        await node2.Registry.ReconciledAsync(node2.Map.Current.Epoch, token);

        var (version, exists, content) = await ReadJournalAsync(store2, "corrupt-k");
        Assert.True(exists);
        Assert.Equal(expectedVersion, version);
        Assert.Equal("alpha-beta", content);
    }

    /// <summary>
    /// When a closed non-replicated predecessor ledger is dropped while its shared <c>PredecessorScan</c> is in flight,
    /// the scan terminates with <see cref="KeyIndexRead.Dropped"/>, <see cref="ArchiveReader"/> restarts cleanly with
    /// the updated map, and returns the exact oracle state from the archive store without throwing
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task NonReplicatedPredecessorDroppedMidScanRestartsCleanly()
    {
        var token = TestContext.Current.CancellationToken;
        await using var env = new MultiNodeEnvironment();
        var storageA = new InMemoryJournalIndex();
        var storageB = new InMemoryJournalIndex();
        var options = ManualFlushOptions();

        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA, options);
        await env.BootstrapAsync(nodeA, [NodeA]);
        await using var storeA = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, token);

        var ledger1 = nodeA.Map.Current.Find(JournalRowKey.Encode("drop-mid").Span)!.Value.Open.Id;
        var v = await AppendAsync(storeA, "drop-mid", JournalVersion.Origin, "d1-");
        v = await AppendAsync(storeA, "drop-mid", v, "d2");

        var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var nodeB = await env.OpenNodeAsync(
            NodeB,
            storageB,
            options,
            onRead: (ledger, slot) =>
            {
                if (ledger == ledger1 && slot == Slot.Zero)
                {
                    _ = scanEntered.TrySetResult();
                    return scanGate.Task;
                }

                return Task.CompletedTask;
            });
        await env.ReplaceAsync(nodeB, ledger1, [NodeB]);
        await using var storeB = new JournalStore(nodeB.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeB.StateDb);
        await nodeB.Registry.ReconciledAsync(nodeB.Map.Current.Epoch, token);

        // Start reading "drop-mid" on NodeB; it starts the shared PredecessorScan of L1 and blocks on slot 0.
        var readTask = ReadJournalAsync(storeB, "drop-mid");
        await scanEntered.Task.WaitAsync(Signal, token);

        // Drain & drop L1 while NodeB's PredecessorScan is blocked, then refresh NodeB's map so WatchMapAsync stops the scan as Dropped.
        _ = await nodeA.Map.ReadAsync(token);
        await env.DrainClosedLedgerAsync(nodeA.Reader, ledger1);
        await env.DropLedgerAsync(nodeA, ledger1);
        _ = await nodeB.Map.ReadAsync(token);

        var (version, exists, content) = await readTask;
        Assert.True(exists);
        Assert.Equal("d1-d2", content);

        // Appending on L2 after the restart uses the rebased post-drain version and reads back cleanly.
        var expectedVersion = await AppendAsync(storeB, "drop-mid", version, "-d3");
        var (vAfter, existsAfter, contentAfter) = await ReadJournalAsync(storeB, "drop-mid");
        Assert.True(existsAfter);
        Assert.Equal(expectedVersion, vAfter);
        Assert.Equal("d1-d2-d3", contentAfter);
    }

    /// <summary>
    /// Restarting a node after a partial durable flush of a non-replicated predecessor scan resumes from the persisted
    /// durable watermark rather than rescanning from slot 0, and cleans up the persisted non-replicated source once the
    /// predecessor is later dropped from the map.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task RestartMidNonReplicatedPredecessorScanResumesFromDurableWatermarkAndCleansUpOnDrop()
    {
        var token = TestContext.Current.CancellationToken;
        await using var env = new MultiNodeEnvironment();
        var storageA = new InMemoryJournalIndex();
        var storageB = new InMemoryJournalIndex();
        var options = ManualFlushOptions();

        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA, options);
        await env.BootstrapAsync(nodeA, [NodeA]);
        await using var storeA = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, token);

        var ledger1 = nodeA.Map.Current.Find(JournalRowKey.Encode("mid-restart").Span)!.Value.Open.Id;
        var v = await AppendAsync(storeA, "mid-restart", JournalVersion.Origin, "s0-");
        v = await AppendAsync(storeA, "mid-restart", v, "s1-");
        var expectedVersion = await AppendAsync(storeA, "mid-restart", v, "s2");

        // Boot 1 of NodeB: pause PredecessorScan at slot 1, flush slot 0 durably, then dispose (simulating a crash mid-scan).
        LedgerId ledger2;
        var slot1Reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slot1Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var nodeB1 = await env.OpenNodeAsync(
            NodeB,
            storageB,
            options,
            onRead: (ledger, slot) =>
            {
                if (ledger == ledger1 && slot == new Slot(1))
                {
                    _ = slot1Reached.TrySetResult();
                    return slot1Gate.Task;
                }

                return Task.CompletedTask;
            }))
        {
            await env.ReplaceAsync(nodeB1, ledger1, [NodeB]);
            ledger2 = nodeB1.Map.Current.Find(JournalRowKey.Encode("mid-restart").Span)!.Value.Open.Id;
            var lookupTask = nodeB1.StateDb.KeyIndex.ReadAsync(nodeB1.Map.Current.Get(ledger1)!.Value, new Slot(3), JournalRowKey.Encode("mid-restart").Memory, token).AsTask();
            await slot1Reached.Task.WaitAsync(Signal, token);
            await WaitUntilAsync(() => nodeB1.StateDb.Watermark(ledger1) >= new Slot(1), Signal);
            await nodeB1.StateDb.FlushAsync(token);
            Assert.Equal(new Slot(1), nodeB1.StateDb.DurableWatermark(ledger1));

            // Dispose nodeB1 while lookupTask is still blocked; lookupTask observes cancellation/disposal.
            await nodeB1.DisposeAsync();
            _ = await Record.ExceptionAsync(() => lookupTask);
        }

        // Boot 2 of NodeB on the same storage: starts at durable watermark 1, replaces L2 -> L3 as a restarted primary,
        // completes scan without rereading slot 0, and cleans up on drop!
        await using var nodeB2 = await env.OpenNodeAsync(NodeB, storageB, options);
        await env.ReplaceAsync(nodeB2, ledger2, [NodeB]);
        Assert.Equal(new Slot(1), nodeB2.StateDb.Watermark(ledger1));
        Assert.Equal(new Slot(1), nodeB2.StateDb.DurableWatermark(ledger1));

        await using var storeB = new JournalStore(nodeB2.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeB2.StateDb);
        await nodeB2.Registry.ReconciledAsync(nodeB2.Map.Current.Epoch, token);

        var (version, exists, content) = await ReadJournalAsync(storeB, "mid-restart");
        Assert.True(exists);
        Assert.Equal(expectedVersion, version);
        Assert.Equal("s0-s1-s2", content);
        Assert.Equal(new Slot(3), nodeB2.StateDb.Watermark(ledger1));

        // Now drain & drop L1 and verify WatchMapAsync removes the persisted non-replicated source cleanly!
        _ = await nodeA.Map.ReadAsync(token);
        await env.DrainClosedLedgerAsync(nodeA.Reader, ledger1);
        await env.DropLedgerAsync(nodeA, ledger1);
        _ = await nodeB2.Map.ReadAsync(token);
        await WaitUntilAsync(() => nodeB2.StateDb.Watermark(ledger1) == Slot.Zero, Signal);
    }

    /// <summary>
    /// Duplicate <see cref="JournalStateDatabase.Attach"/> throws <see cref="InvalidOperationException"/>, and both a
    /// clean stop and a background fault of <see cref="LedgerApplyLoop"/> wake any waiting catch-up lookup promptly
    /// instead of hanging.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ApplyLoopStopAndFaultWakeCatchUpWaitersAndDuplicateAttachThrows()
    {
        var token = TestContext.Current.CancellationToken;

        // Case 1: Duplicate Attach throws, and clean disposal of LedgerApplyLoop wakes waiting catch-up lookups.
        await using (var env = new MultiNodeEnvironment())
        {
            var storage = new InMemoryJournalIndex();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var nodeA = await env.OpenNodeAsync(NodeA, storage, ManualFlushOptions(), onRead: (_, _) => gate.Task);
            await env.BootstrapAsync(nodeA, [NodeA]);

            _ = Assert.Throws<InvalidOperationException>(() => nodeA.StateDb.Attach(nodeA.ApplyLoop));

            await using var store = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeA.StateDb);
            await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, token);
            _ = await AppendAsync(store, "stop-k", JournalVersion.Origin, "v1");

            var ledger = nodeA.Map.Current.Find(JournalRowKey.Encode("stop-k").Span)!.Value.Open;
            var waiting = nodeA.StateDb.KeyIndex.ReadAsync(ledger, new Slot(1), JournalRowKey.Encode("stop-k").Memory, token).AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, token);
            Assert.False(waiting.IsCompleted);

            await nodeA.ApplyLoop.DisposeAsync();
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(Signal, token));
        }

        // Case 2: Faulting LedgerApplyLoop wakes waiting catch-up lookups with the underlying exception.
        await using (var env = new MultiNodeEnvironment())
        {
            var storage = new InMemoryJournalIndex();
            var triggerFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var nodeA = await env.OpenNodeAsync(NodeA, storage, ManualFlushOptions(), startApplyLoop: false);
            var innerApplier = nodeA.StateDb.Applier;
            await using var faultingLoop = new LedgerApplyLoop(
                nodeA.Reader,
                new LedgerApplier
                {
                    NextSlotAsync = innerApplier.NextSlotAsync,
                    ApplyAsync = async (id, item, ct) =>
                    {
                        await triggerFault.Task.WaitAsync(ct).ConfigureAwait(false);
                        throw new InvalidOperationException("injected-apply-fault");
                    },
                    RemoveAsync = innerApplier.RemoveAsync,
                },
                TimeProvider.System);
            nodeA.StateDb.Attach(faultingLoop);

            await env.BootstrapAsync(nodeA, [NodeA]);
            await using var store = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), TimeProvider.System, nodeA.StateDb);
            await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, token);
            _ = await AppendAsync(store, "fault-k", JournalVersion.Origin, "v1");

            var ledger = nodeA.Map.Current.Find(JournalRowKey.Encode("fault-k").Span)!.Value.Open;
            var waiting = nodeA.StateDb.KeyIndex.ReadAsync(ledger, new Slot(1), JournalRowKey.Encode("fault-k").Memory, token).AsTask();
            triggerFault.SetResult();

            var fault = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting.WaitAsync(Signal, token));
            Assert.Equal("injected-apply-fault", fault.Message);
        }
    }

    /// <summary>
    /// Key-index keys sort big-endian by <c>(LedgerId, SHA256(key), Slot, Index)</c>.
    /// </summary>
    [Fact]
    public void IndexKeyCodecOrdersByLedgerHashSlotIndex()
    {
        var hashA = JournalKeyHash.Compute("a"u8);
        var hashB = JournalKeyHash.Compute("b"u8);
        if (hashA.CompareTo(hashB) > 0)
        {
            (hashA, hashB) = (hashB, hashA);
        }

        (LedgerId Ledger, JournalKeyHash Hash, Slot Slot, int Index)[] ordered =
        [
            (new LedgerId(1), hashA, new Slot(0), 0),
            (new LedgerId(1), hashA, new Slot(0), 1),
            (new LedgerId(1), hashA, new Slot(0), 255),
            (new LedgerId(1), hashA, new Slot(0), 256),
            (new LedgerId(1), hashA, new Slot(0), int.MaxValue),
            (new LedgerId(1), hashA, new Slot(1), 0),
            (new LedgerId(1), hashA, new Slot(256), 0),
            (new LedgerId(1), hashA, new Slot(long.MaxValue), 0),
            (new LedgerId(1), hashB, new Slot(0), 0),
            (new LedgerId(256), hashA, new Slot(0), 0),
            (new LedgerId(ulong.MaxValue), hashB, new Slot(long.MaxValue), int.MaxValue),
        ];

        var encoded = new byte[ordered.Length][];
        for (var i = 0; i < ordered.Length; i++)
        {
            encoded[i] = new byte[RocksDb.RocksDbJournalIndex.KeyIndexKeyLength];
            RocksDb.RocksDbJournalIndex.EncodeKeyIndexKey(encoded[i], ordered[i].Ledger, ordered[i].Hash, ordered[i].Slot, ordered[i].Index);
            if (i > 0)
            {
                Assert.True(
                    encoded[i - 1].AsSpan().SequenceCompareTo(encoded[i]) < 0,
                    $"Expected encoded key {i - 1} to sort strictly before key {i}.");
            }
        }
    }

    /// <summary>
    /// Verifies <see cref="JournalIndexOptions.FlushInterval"/> defaults to <see cref="Timeout.InfiniteTimeSpan"/>
    /// (size-based + explicit/clean-shutdown flushing without periodic timer flushes), while an explicit positive
    /// <see cref="JournalIndexOptions.FlushInterval"/> still triggers periodic flushes.
    /// </summary>
    [Fact]
    public async Task DefaultFlushIntervalDisablesPeriodicFlushWhileExplicitIntervalFlushesPeriodically()
    {
        var defaults = new JournalIndexOptions();
        Assert.Equal(Timeout.InfiniteTimeSpan, defaults.FlushInterval);
        Assert.Equal(64L * 1024 * 1024, defaults.MaxWriteBufferBytes);

        // With default FlushInterval (Timeout.InfiniteTimeSpan), advancing time does not advance DurableWatermark
        // until an explicit FlushAsync is called.
        await using (var env = new MultiNodeEnvironment())
        {
            var storage = new InMemoryJournalIndex();
            var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
            await using var node = await env.OpenNodeAsync(NodeA, storage, new JournalIndexOptions { QuickCheckInterval = Timeout.InfiniteTimeSpan }, timeProvider: time);
            await env.BootstrapAsync(node, [NodeA]);
            var ledgerId = node.Map.Current.Find(JournalRowKey.Encode("k").Span)!.Value.Open.Id;

            await using var store = new JournalStore(node.Registry, env.Store, new JournalStoreOptions(), time, node.StateDb);
            _ = await AppendAsync(store, "k", JournalVersion.Origin, "v1");
            node.ApplyLoop.Wake(ledgerId);
            await WaitUntilAsync(() => node.StateDb.Watermark(ledgerId) >= new Slot(1), Signal);

            time.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(TimeSpan.FromMilliseconds(30), TimeProvider.System, Token);
            Assert.Equal(Slot.Zero, node.StateDb.DurableWatermark(ledgerId));

            await node.StateDb.FlushAsync(Token);
            Assert.Equal(new Slot(1), node.StateDb.DurableWatermark(ledgerId));
        }

        // With an explicit positive FlushInterval, advancing time triggers a periodic atomic flush.
        await using (var env = new MultiNodeEnvironment())
        {
            var storage = new InMemoryJournalIndex();
            var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
            await using var node = await env.OpenNodeAsync(
                NodeA,
                storage,
                new JournalIndexOptions { FlushInterval = TimeSpan.FromSeconds(2), QuickCheckInterval = Timeout.InfiniteTimeSpan },
                timeProvider: time);
            await env.BootstrapAsync(node, [NodeA]);
            var ledgerId = node.Map.Current.Find(JournalRowKey.Encode("k").Span)!.Value.Open.Id;

            await using var store = new JournalStore(node.Registry, env.Store, new JournalStoreOptions(), time, node.StateDb);
            _ = await AppendAsync(store, "k", JournalVersion.Origin, "v1");
            node.ApplyLoop.Wake(ledgerId);
            await WaitUntilAsync(() => node.StateDb.Watermark(ledgerId) >= new Slot(1), Signal);
            Assert.Equal(Slot.Zero, node.StateDb.DurableWatermark(ledgerId));

            time.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => node.StateDb.DurableWatermark(ledgerId) >= new Slot(1), Signal);
        }
    }

    private static JournalIndexOptions ManualFlushOptions() => new()
    {
        FlushInterval = Timeout.InfiniteTimeSpan,
        QuickCheckInterval = Timeout.InfiniteTimeSpan,
    };

    private static async Task<JournalVersion> AppendAsync(JournalStore store, string key, JournalVersion expected, string text)
    {
        using var payload = text.Length == 0 ? default : ArcBuffer.Wrap(Encoding.UTF8.GetBytes(text));
        using var record = store.EncodeAppend(JournalRowKey.Encode(key), expected, payload);
        var outcome = await store.WriteAsync(record, Token);
        return Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;
    }

    private static async Task<JournalVersion> ReplaceAsync(JournalStore store, string key, JournalVersion expected, string text)
    {
        using var payload = text.Length == 0 ? default : ArcBuffer.Wrap(Encoding.UTF8.GetBytes(text));
        using var record = store.EncodeReplace(JournalRowKey.Encode(key), expected, payload);
        var outcome = await store.WriteAsync(record, Token);
        return Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;
    }

    private static async Task<JournalVersion> DeleteAsync(JournalStore store, string key, JournalVersion expected)
    {
        using var record = store.EncodeDelete(JournalRowKey.Encode(key), expected);
        var outcome = await store.WriteAsync(record, Token);
        return Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;
    }

    private static async Task<JournalVersion> ExpireAsync(JournalStore store, string key, JournalVersion expected)
    {
        var outcome = await store.ExpireAsync(JournalRowKey.Encode(key), expected, Token);
        return Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;
    }

    private static async Task<(JournalVersion Version, bool Exists, string Content)> ReadJournalAsync(JournalStore store, string key)
    {
        using var result = Assert.IsType<JournalReadResult>((await store.ReadAsync(JournalRowKey.Encode(key), Token)).Value);
        var content = await JournalCluster.ContentAsync(result);
        Assert.Equal(result.ContentLength, Encoding.UTF8.GetByteCount(content));
        return (result.CurrentVersion, result.Exists, content);
    }

    private static string ReadArchiveContent(ArchiveRead read)
    {
        var sb = new StringBuilder();
        if (read.Prefix is { } prefix && prefix.Content.Length > 0)
        {
            _ = sb.Append(Encoding.UTF8.GetString(prefix.Content.ToArray()));
        }

        foreach (var row in read.Rows)
        {
            if (!row.IsHeld && row.Body.Length > 0)
            {
                _ = sb.Append(Encoding.UTF8.GetString(row.Body.ToArray()));
            }
        }

        return sb.ToString();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = TimeProvider.System.GetUtcNow() + timeout;
        while (!predicate())
        {
            if (TimeProvider.System.GetUtcNow() >= deadline)
            {
                Assert.Fail("Condition was not met before the timeout.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Token);
        }
    }

    /// <summary>
    /// Multi-node test environment over a shared <see cref="InMemoryLedgerReplicaTransport"/>,
    /// <see cref="InMemoryConditionalRegister"/>, and <see cref="InMemoryArchiveStore"/>.
    /// </summary>
    private sealed class MultiNodeEnvironment : IAsyncDisposable
    {
        public MultiNodeEnvironment() => Committer = new LedgerMapCommitter(Register, 1 << 20);

        public InMemoryLedgerReplicaTransport Transport { get; } = new();

        public InMemoryConditionalRegister Register { get; } = new();

        public LedgerMapCommitter Committer { get; }

        public InMemoryArchiveStore Store { get; } = new();

        public async Task BootstrapAsync(NodeProcess primary, NodeId[] replicas)
        {
            var executor = new TransitionExecutor(primary.Registry, NullLogger<TransitionExecutor>.Instance);
            var choice = new TransitionChoice(primary.Node, ImmutableArray.Create(replicas), 4, 0, 0);
            Assert.IsType<LedgerMapCommitOutcome.Applied>((await executor.ExecuteAsync(new Transition.Bootstrap(choice), Committer.CommitAsync, Token)).Value);
            _ = await primary.Map.ReadAsync(Token);
        }

        public async Task<NodeProcess> OpenNodeAsync(
            NodeId node,
            InMemoryJournalIndex storage,
            JournalIndexOptions options,
            bool startApplyLoop = true,
            Func<LedgerId, Slot, Task>? onRead = null,
            TimeProvider? timeProvider = null) =>
            await NodeProcess.OpenAsync(node, Register, Transport, storage, options, startApplyLoop, onRead, timeProvider);

        public async Task ReplaceAsync(NodeProcess newPrimary, LedgerId ledger, NodeId[] newReplicas)
        {
            _ = await newPrimary.Map.ReadAsync(Token);
            var executor = new TransitionExecutor(newPrimary.Registry, NullLogger<TransitionExecutor>.Instance);
            var choice = new TransitionChoice(newPrimary.Node, ImmutableArray.Create(newReplicas), 4, 0, newPrimary.Map.Current.Epoch);
            Assert.IsType<LedgerMapCommitOutcome.Applied>((await executor.ExecuteAsync(new Transition.Replace(ledger, choice), Committer.CommitAsync, Token)).Value);
            _ = await newPrimary.Map.ReadAsync(Token);
        }

        public async Task DrainClosedLedgerAsync(LedgerReader reader, LedgerId ledger)
        {
            var listed = reader.Map.Current.Get(ledger)!.Value;
            var end = Assert.IsType<LedgerStatus.Closed>(listed.Status.Value).T;
            var segments = new List<List<KeyedRow>>();
            using var bodies = new ArcBufferWriter();
            using var view = new LedgerSegmentView(ledger);
            LedgerSegmentId? current = null;
            try
            {
                await foreach (var item in reader.ScanAsync(listed, Slot.Zero, end, view, cancellationToken: Token))
                {
                    Assert.True(item.Read.TryGetValue(out LedgerRead.Data data));
                    using (data)
                    {
                        foreach (var row in item.Rows)
                        {
                            if (current != view.Segment)
                            {
                                segments.Add([]);
                                current = view.Segment;
                            }

                            segments[^1].Add(new KeyedRow(row.Key.ToArray(), ArchiveRow.Of(row, view.Segment, bodies)));
                        }
                    }
                }

                foreach (var segment in segments)
                {
                    KeyedRow.DrainByKey([.. segment], (key, rows) => Store.Drain(key, rows));
                }
            }
            finally
            {
                foreach (var segment in segments)
                {
                    KeyedRow.Release(segment);
                }
            }
        }

        public async Task DropLedgerAsync(NodeProcess actor, LedgerId ledger)
        {
            Assert.IsType<LedgerMapCommitOutcome.Applied>((await Committer.CommitAsync(new LedgerMapChange.Drop(ledger), Token)).Value);
            _ = await actor.Map.ReadAsync(Token);
            foreach (var node in new[] { NodeA, NodeB })
            {
                _ = await new ReplicaReconciler(Transport.Node(node), actor.Map).ReconcileAsync(Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Committer.DisposeAsync();
            Store.Dispose();
            Transport.Dispose();
        }
    }

    private sealed class NodeProcess : IAsyncDisposable
    {
        private JournalStateDatabase? _stateDb;
        private LedgerApplyLoop? _applyLoop;

        private NodeProcess(
            NodeId node,
            LedgerMapView map,
            CountingTransport transport)
        {
            Node = node;
            Map = map;
            Transport = transport;
            Reader = new LedgerReader(map, transport, TimeProvider.System, node);
            Registry = new LedgerWriterRegistry(Reader, 1 << 25, LedgerWriterOptions.Default, TimeProvider.System, NullLoggerFactory.Instance);
        }

        public NodeId Node { get; }

        public LedgerMapView Map { get; }

        public CountingTransport Transport { get; }

        public LedgerReader Reader { get; }

        public LedgerWriterRegistry Registry { get; }

        public JournalStateDatabase StateDb => _stateDb ?? throw new InvalidOperationException("Not opened.");

        public LedgerApplyLoop ApplyLoop => _applyLoop ?? throw new InvalidOperationException("Apply loop was not started.");

        public static async Task<NodeProcess> OpenAsync(
            NodeId node,
            InMemoryConditionalRegister register,
            ILedgerReplicaTransport transport,
            InMemoryJournalIndex storage,
            JournalIndexOptions options,
            bool startApplyLoop,
            Func<LedgerId, Slot, Task>? onRead,
            TimeProvider? timeProvider = null)
        {
            var map = await LedgerMapView.OpenAsync(register, Token);
            var counting = new CountingTransport(transport, onRead);
            var process = new NodeProcess(node, map, counting);
            try
            {
                await process.InitializeAsync(storage, options, startApplyLoop, timeProvider ?? TimeProvider.System);
                return process;
            }
            catch
            {
                await process.DisposeAsync();
                throw;
            }
        }

        private async Task InitializeAsync(
            InMemoryJournalIndex storage,
            JournalIndexOptions options,
            bool startApplyLoop,
            TimeProvider timeProvider)
        {
            _stateDb = await JournalStateDatabase.OpenAsync(storage, Reader, options, timeProvider, Token);
            if (startApplyLoop)
            {
                _applyLoop = new LedgerApplyLoop(Reader, _stateDb.Applier, TimeProvider.System);
                _stateDb.Attach(_applyLoop);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            if (_applyLoop is not null)
            {
                await _applyLoop.DisposeAsync();
            }

            if (_stateDb is not null)
            {
                await _stateDb.DisposeAsync();
            }

            await Reader.DisposeAsync();
        }
    }

    private sealed class CountingTransport(ILedgerReplicaTransport inner, Func<LedgerId, Slot, Task>? onRead) : ILedgerReplicaTransport
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<LedgerId, HashSet<long>> _reads = [];

        public void ResetCounts()
        {
            lock (_lock)
            {
                _reads.Clear();
            }
        }

        public HashSet<long> ReadSlotsOf(LedgerId ledger)
        {
            lock (_lock)
            {
                return _reads.TryGetValue(ledger, out var slots) ? [.. slots] : [];
            }
        }

        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            new CountingReplica(this, ledger, inner.Open(node, ledger, options), onRead);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(node, ledgers, cancellationToken);

        private void RecordRead(LedgerId ledger, Slot slot)
        {
            lock (_lock)
            {
                if (!_reads.TryGetValue(ledger, out var slots))
                {
                    slots = [];
                    _reads[ledger] = slots;
                }

                _ = slots.Add(slot.Value);
            }
        }

        private sealed class CountingReplica(CountingTransport owner, LedgerId ledger, ILedgerReplica inner, Func<LedgerId, Slot, Task>? onRead) : ILedgerReplica
        {
            public ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
                inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

            public async ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
            {
                if (onRead is not null)
                {
                    await onRead(ledger, slot).WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                owner.RecordRead(ledger, slot);
                return await inner.ReadAsync(slot, contiguousTail, cancellationToken).ConfigureAwait(false);
            }

            public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

            public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
                inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
