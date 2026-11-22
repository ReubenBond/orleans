using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals.Memory;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// Row 5.2 Part E verification at the Journals layer: claim-scoped warm state (<see cref="JournalWarmState"/>,
/// journals.md §8.3, JS-I6), <see cref="JournalStore.KeepWarmStateAcrossClaimsMutant"/>, indexed tombstone
/// expiry across warm-state eviction, restarts, max-length keys, and non-replicated closed predecessors
/// (journals.md §8.6, JS-I7) with mutants, owner reads (journals.md §8.2)
/// with <see cref="JournalStore.AnswersWarmStateNewerVersionMutant"/>, warm-state snapshot requests (journals.md §8.7)
/// across eviction/reload and archive rebasing, and preloaded-LSM absence answers making zero object-store requests and
/// zero replica slot reads.
/// </summary>
public sealed class JournalWarmStateTests : IDisposable
{
    private static readonly TimeSpan Signal = TimeSpan.FromSeconds(10);
    private static readonly NodeId NodeA = new(new Guid("00000000-0000-0000-0000-00000000001a"));
    private static readonly NodeId NodeB = new(new Guid("00000000-0000-0000-0000-00000000001b"));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Warm state is scoped to one primary claim: owned by <see cref="LedgerAdmission"/> while a primary serves an open
    /// ledger and discarded when the claim ends, so stale warm state from an earlier claim is never reused (JS-I6).
    /// </summary>
    [Fact]
    public async Task WarmStateIsClaimScopedAndDiscardedWhenClaimEnds()
    {
        await using var env = new WarmTestEnvironment();
        var storageA = new InMemoryJournalIndex();
        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA);
        await env.BootstrapAsync(nodeA, [NodeA]);

        await using var store = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), env.Time, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);
        var ledger0 = nodeA.Map.Current.Find(JournalRowKey.Encode("k").Span)!.Value.Open.Id;

        var v1 = await AppendAsync(store, "k", JournalVersion.Origin, "hello");

        // Second write at wrong version is rejected directly from warm state with zero replica slot reads.
        nodeA.Transport.ResetCounts();
        using var badPayload = ArcBuffer.Wrap("bad"u8.ToArray());
        using var badRecord = store.EncodeAppend(JournalRowKey.Encode("k"), JournalVersion.Origin, badPayload);
        var rejected = Assert.IsType<JournalWriteOutcome.Rejected>((await store.WriteAsync(badRecord, Token)).Value);
        Assert.Equal(v1, rejected.CurrentVersion);
        Assert.Empty(nodeA.Transport.ReadSlotsOf(ledger0));

        // Replace ledger0 -> ledger1: ledger0's claim ends and its warm state is discarded; first touch on ledger1
        // must reload from history (reading ledger0's indexed slot) before admitting v2.
        await env.ReplaceAsync(nodeA, ledger0, [NodeA]);
        await WaitUntilAsync(() => !store.ServedLedgers.Contains(ledger0), Signal);
        nodeA.Transport.ResetCounts();
        var v2 = await AppendAsync(store, "k", v1, "+world");
        Assert.NotEqual(v1, v2);
        Assert.NotEmpty(nodeA.Transport.ReadSlotsOf(ledger0));
    }

    /// <summary>
    /// Handing a range from NodeA to NodeB (which writes to it) and back to NodeA discards NodeA's old claim warm state
    /// (JS-I6), so NodeA's new claim loads NodeB's write; <see cref="JournalStore.KeepWarmStateAcrossClaimsMutant"/>
    /// keeps NodeA's stale warm state across claims and fails the check.
    /// </summary>
    [Fact]
    public Task RangeHandedAwayAndBackDiscardsStaleWarmState() => KeepWarmAcrossClaimsAsync(keepMutant: false);

    /// <summary>The JS-I6 handoff test catches <see cref="JournalStore.KeepWarmStateAcrossClaimsMutant"/>.</summary>
    [Fact]
    public async Task KeepWarmStateAcrossClaimsMutantIsCaught() =>
        Assert.Contains("stale version", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => KeepWarmAcrossClaimsAsync(keepMutant: true))).Message, StringComparison.Ordinal);

    /// <summary>
    /// Tombstone expiry discovers cold tombstones from <see cref="JournalStateDatabase"/>'s <c>tombstones</c> index after
    /// warm-state eviction and across a full process restart, skips resurrected keys, respects <c>TombstoneRetention</c>,
    /// and cleans up the index entry once expired (journals.md §8.6, JS-I7).
    /// </summary>
    [Fact]
    public async Task IndexedTombstoneExpiryExpiresEvictedAndRestartedColdTombstonesAndSkipsResurrectedKeys()
    {
        await using var env = new WarmTestEnvironment();
        var storageA = new InMemoryJournalIndex();
        var retention = TimeSpan.FromHours(1);
        var options = new JournalStoreOptions
        {
            TombstoneRetention = retention,
            WarmStateBytes = 0, // Evict idle warm state immediately so all deleted keys are cold.
        };

        LedgerId ledger0;
        await using (var node1 = await env.OpenNodeAsync(NodeA, storageA))
        {
            await env.BootstrapAsync(node1, [NodeA]);
            await using var store1 = new JournalStore(node1.Registry, env.Store, options, env.Time, node1.StateDb);
            await node1.Registry.ReconciledAsync(node1.Map.Current.Epoch, Token);
            ledger0 = node1.Map.Current.Find(JournalRowKey.Encode("evicted-tomb").Span)!.Value.Open.Id;

            // Create and delete "evicted-tomb" (stays deleted) and "resurrected" (re-appended after delete).
            var vEvicted = await AppendAsync(store1, "evicted-tomb", JournalVersion.Origin, "bye");
            _ = await DeleteAsync(store1, "evicted-tomb", vEvicted);

            var vRes = await AppendAsync(store1, "resurrected", JournalVersion.Origin, "v1");
            vRes = await DeleteAsync(store1, "resurrected", vRes);
            _ = await AppendAsync(store1, "resurrected", vRes, "alive-again");

            var tail = Assert.IsType<LedgerTail>((await node1.Reader.ReadTailAsync(node1.Map.Current.Get(ledger0)!.Value, Token)).Value);
            node1.ApplyLoop.Wake(ledger0);
            await WaitUntilAsync(() => node1.StateDb.Watermark(ledger0) >= tail.ContiguousTail, Signal);

            // Before retention elapses: nothing expires (even though warm state was evicted and tombstones table has the key).
            var beforeRetention = await store1.ExpireTombstonesAsync(Token);
            Assert.Equal(0, beforeRetention.Expired);

            // Flush state DB so "evicted-tomb" survives restart in the tombstones index.
            await node1.StateDb.FlushAsync(Token);
        }

        // Full process restart: all warm state is gone; advance clock past retention, replace the previous process's
        // open ledger to take a new claim with ledger0 as a closed predecessor, and expire from the persisted index.
        env.Time.Advance(retention + TimeSpan.FromMinutes(1));
        await using var node2 = await env.OpenNodeAsync(NodeA, storageA);
        await env.ReplaceAsync(node2, ledger0, [NodeA]);
        await using var store2 = new JournalStore(node2.Registry, env.Store, options, env.Time, node2.StateDb);
        await node2.Registry.ReconciledAsync(node2.Map.Current.Epoch, Token);
        var ledger1 = node2.Map.Current.Find(JournalRowKey.Encode("evicted-tomb").Span)!.Value.Open.Id;

        var scan = await store2.ExpireTombstonesAsync(Token);
        Assert.Equal(1, scan.Expired);

        // Wait for the Expired row on ledger1 to be indexed so the tombstones index records the expiration.
        var tailAfter = Assert.IsType<LedgerTail>((await node2.Reader.ReadTailAsync(node2.Map.Current.Get(ledger1)!.Value, Token)).Value);
        node2.ApplyLoop.Wake(ledger1);
        await WaitUntilAsync(() => node2.StateDb.Watermark(ledger1) >= tailAfter.ContiguousTail, Signal);

        // A second scan finds nothing left to expire; resurrected key still exists with "alive-again".
        var secondScan = await store2.ExpireTombstonesAsync(Token);
        Assert.Equal(0, secondScan.Expired);

        var (_, evictedExists, evictedContent) = await ReadJournalAsync(store2, "evicted-tomb");
        Assert.False(evictedExists);
        Assert.Equal(string.Empty, evictedContent);

        var (_, resExists, resContent) = await ReadJournalAsync(store2, "resurrected");
        Assert.True(resExists);
        Assert.Equal("alive-again", resContent);
    }

    /// <summary>
    /// Indexed cold tombstones respect <c>TombstoneRetention</c> (JS-I7): nothing expires before retention elapses, and
    /// once retention elapses the indexed tombstone expires cleanly.
    /// </summary>
    [Fact]
    public Task IndexedColdTombstoneRespectsRetention() => IndexedColdTombstoneRespectsRetentionAsync(ignoreRetentionMutant: false);

    /// <summary>
    /// The JS-I7 indexed cold tombstone retention test catches <see cref="JournalStore.IgnoreRetentionMutant"/>.
    /// </summary>
    [Fact]
    public async Task IgnoreRetentionMutantOverIndexedColdTombstoneIsCaught() =>
        Assert.Contains("before retention elapsed", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => IndexedColdTombstoneRespectsRetentionAsync(ignoreRetentionMutant: true))).Message, StringComparison.Ordinal);

    /// <summary>
    /// A journal at the maximum legal row key length (<see cref="JournalRowKey.MaxKeyBytes"/> UTF-8 bytes, 65,535-byte
    /// row key) writes, deletes, indexes into the fixed-width 40-byte <c>tombstones</c> column family,
    /// and expires cleanly once retention elapses (journals.md §8.3, §8.6).
    /// </summary>
    [Fact]
    public async Task MaxLengthRowKeyTombstoneIndexesAndExpiresCleanly()
    {
        await using var env = new WarmTestEnvironment();
        var storageA = new InMemoryJournalIndex();
        var retention = TimeSpan.FromHours(1);
        var options = new JournalStoreOptions
        {
            TombstoneRetention = retention,
            WarmStateBytes = 0,
        };

        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA);
        await env.BootstrapAsync(nodeA, [NodeA]);
        await using var store = new JournalStore(nodeA.Registry, env.Store, options, env.Time, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);

        var maxKey = new string('k', JournalRowKey.MaxKeyBytes);
        Assert.Equal(KeyRange.MaxKeyLength, JournalRowKey.Encode(maxKey).Length);

        var v1 = await AppendAsync(store, maxKey, JournalVersion.Origin, "max-payload");
        _ = await DeleteAsync(store, maxKey, v1);

        var ledger0 = nodeA.Map.Current.Find(JournalRowKey.Encode(maxKey).Span)!.Value.Open.Id;
        var tail = Assert.IsType<LedgerTail>((await nodeA.Reader.ReadTailAsync(nodeA.Map.Current.Get(ledger0)!.Value, Token)).Value);
        nodeA.ApplyLoop.Wake(ledger0);
        await WaitUntilAsync(() => nodeA.StateDb.Watermark(ledger0) >= tail.ContiguousTail, Signal);

        env.Time.Advance(retention + TimeSpan.FromMinutes(1));
        var scan = await store.ExpireTombstonesAsync(Token);
        Assert.Equal(1, scan.Expired);

        var (_, exists, content) = await ReadJournalAsync(store, maxKey);
        Assert.False(exists);
        Assert.Equal(string.Empty, content);
    }

    /// <summary>
    /// A new primary whose node was not a replica of a closed predecessor catches up that predecessor's key and tombstone
    /// index via <see cref="JournalStateDatabase"/>'s predecessor scan and expires its cold tombstones (journals.md §8.6,
    /// JS-I7, JS-I9).
    /// </summary>
    [Fact]
    public Task NonReplicatedClosedPredecessorColdTombstoneExpiresAfterCatchUp() =>
        NonReplicatedPredecessorColdExpiryAsync(skipCatchUpMutant: false);

    /// <summary>
    /// The non-replicated closed predecessor cold-expiry check catches <see cref="JournalStateDatabase.SkipCatchUpMutant"/>.
    /// </summary>
    [Fact]
    public async Task NonReplicatedClosedPredecessorColdExpiryCatchesSkipCatchUpMutant() =>
        Assert.Contains("expected 1 expired cold tombstone", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => NonReplicatedPredecessorColdExpiryAsync(skipCatchUpMutant: true))).Message, StringComparison.Ordinal);

    /// <summary>
    /// <see cref="JournalStore.ApplyOnAdmitMutant"/> exposes uncommitted writes to warm state before durability, whereas
    /// normal admission applies a batch only on <c>Durable</c> (journals.md §8.1, JS-I2).
    /// </summary>
    [Fact]
    public Task ApplyOnAdmitMutantExposesUncommittedStateBeforeDurability() => ApplyOnAdmitAsync(applyOnAdmitMutant: false);

    /// <summary>Proves the apply-on-admit check catches <see cref="JournalStore.ApplyOnAdmitMutant"/>.</summary>
    [Fact]
    public async Task ApplyOnAdmitMutantIsCaught() =>
        Assert.Contains("before durability", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => ApplyOnAdmitAsync(applyOnAdmitMutant: true))).Message, StringComparison.Ordinal);

    /// <summary>
    /// Owner reads return the version of the content folded from their cut, never warm state's newer version when a
    /// concurrent write lands after the read's cut (journals.md §8.2); <see cref="JournalStore.AnswersWarmStateNewerVersionMutant"/>
    /// fails this invariant.
    /// </summary>
    [Fact]
    public Task OwnerReadReturnsCutVersionAndAnswersWarmStateNewerVersionMutantIsCaught() => OwnerReadVersionMatchesContentAsync(mutant: false);

    /// <summary>Proves the owner-read cut invariant catches <see cref="JournalStore.AnswersWarmStateNewerVersionMutant"/>.</summary>
    [Fact]
    public async Task AnswersWarmStateNewerVersionMutantIsCaught() =>
        Assert.Contains("matched warm state's newer version", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => OwnerReadVersionMatchesContentAsync(mutant: true))).Message, StringComparison.Ordinal);

    /// <summary>
    /// Warm state tracks <c>BaseLength</c> and <c>BytesSinceBase</c> per journals.md §8.7: <c>ReplaceRequested</c> flips
    /// strictly above <c>BaseLength</c> (<c>alpha = 1</c>), agrees between <c>Accepted</c> and <c>ReadAsync</c>, and
    /// resets on <c>Replace</c>, <c>Delete</c>, and <c>Expired</c>.
    /// </summary>
    [Fact]
    public async Task SnapshotRequestFollowsSection87ThresholdsAndResets()
    {
        await using var env = new WarmTestEnvironment();
        var storageA = new InMemoryJournalIndex();
        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA);
        await env.BootstrapAsync(nodeA, [NodeA]);
        await using var store = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions { TombstoneRetention = TimeSpan.Zero }, env.Time, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);

        // Append onto empty base (BaseLength == 0, BytesSinceBase > 0): immediately ReplaceRequested == true.
        var (v1, rr1) = await WriteAppendWithOutcomeAsync(store, "snap", JournalVersion.Origin, "12345");
        Assert.True(rr1);
        Assert.True(await ReadReplaceRequestedAsync(store, "snap"));

        // Replace with 10-byte snapshot resets BytesSinceBase to 0 -> ReplaceRequested == false.
        var (v2, rr2) = await WriteReplaceWithOutcomeAsync(store, "snap", v1, "0123456789");
        Assert.False(rr2);
        Assert.False(await ReadReplaceRequestedAsync(store, "snap"));

        // Append 10 bytes: BytesSinceBase == 10 <= 1 * 10 -> still false at boundary.
        var (v3, rr3) = await WriteAppendWithOutcomeAsync(store, "snap", v2, "abcdefghij");
        Assert.False(rr3);
        Assert.False(await ReadReplaceRequestedAsync(store, "snap"));

        // Append 1 more byte: BytesSinceBase == 11 > 1 * 10 -> flips to true on both Accepted and ReadAsync.
        var (v4, rr4) = await WriteAppendWithOutcomeAsync(store, "snap", v3, "!");
        Assert.True(rr4);
        Assert.True(await ReadReplaceRequestedAsync(store, "snap"));

        // Delete resets BytesSinceBase to 0 -> ReplaceRequested == false.
        var (v5, rr5) = await WriteDeleteWithOutcomeAsync(store, "snap", v4);
        Assert.False(rr5);
        Assert.False(await ReadReplaceRequestedAsync(store, "snap"));

        // Expire keeps ReplaceRequested == false.
        Assert.Equal(1, (await store.ExpireTombstonesAsync(Token)).Expired);
        Assert.False(await ReadReplaceRequestedAsync(store, "snap"));
        _ = v5;
    }

    /// <summary>
    /// <c>BytesSinceBase == BaseLength</c> vs <c>BaseLength + 1</c> preserves the exact §8.7 boundary after warm-state
    /// eviction and reload, and after draining and dropping the ledger into an LSM archive prefix (which rebases
    /// <c>BaseLength</c> to the prefix's content length and resets <c>BytesSinceBase</c> to 0).
    /// </summary>
    [Fact]
    public async Task SnapshotRequestBoundaryHoldsAcrossWarmEvictionReloadAndArchivePrefixRebase()
    {
        var objects = new InMemoryObjectStore();
        var manifest = new ManifestView(objects);
        await using var committer = new ManifestCommitter(objects, manifest, TimeProvider.System, TimeSpan.Zero);
        var lsmOptions = new ArchiveOptions { GridBits = 1 };
        var lsmStore = new LsmArchiveStore(objects, manifest, new TailCache(objects, lsmOptions), lsmOptions, TimeProvider.System);

        await using var env = new WarmTestEnvironment(lsmStore);
        var storageA = new InMemoryJournalIndex();
        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA);
        await env.BootstrapAsync(nodeA, [NodeA]);

        // WarmStateBytes = 0 evicts idle entries immediately so every write/read reloads from history!
        await using var store = new JournalStore(nodeA.Registry, lsmStore, new JournalStoreOptions { WarmStateBytes = 0 }, env.Time, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);

        // Base of 6 bytes, append 6 bytes -> BytesSinceBase == BaseLength (6 == 6) -> false after eviction & reload.
        var (v1, rr1) = await WriteReplaceWithOutcomeAsync(store, "rebased", JournalVersion.Origin, "123456");
        Assert.False(rr1);
        var (v2, rr2) = await WriteAppendWithOutcomeAsync(store, "rebased", v1, "abcdef");
        Assert.False(rr2);
        Assert.False(await ReadReplaceRequestedAsync(store, "rebased"));

        // Append 1 more byte after eviction & reload -> BytesSinceBase == BaseLength + 1 (7 > 6) -> true!
        var (v3, rr3) = await WriteAppendWithOutcomeAsync(store, "rebased", v2, "!");
        Assert.True(rr3);
        Assert.True(await ReadReplaceRequestedAsync(store, "rebased"));

        // Replace open ledger, drain and drop ledger0 into the LSM so "rebased" has a 13-byte archive prefix and 0 live rows.
        var ledger0 = nodeA.Map.Current.Find(JournalRowKey.Encode("rebased").Span)!.Value.Open.Id;
        await env.ReplaceAsync(nodeA, ledger0, [NodeA]);
        await nodeA.Map.ReadAsync(Token);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);

        var closed0 = nodeA.Map.Current.Get(ledger0)!.Value;
        var t0 = Assert.IsType<LedgerStatus.Closed>(closed0.Status.Value).T;
        var drainWorker = new DrainWorker(objects, manifest, nodeA.Reader, lsmOptions, TimeProvider.System);
        while (true)
        {
            var outcome = await drainWorker.DrainAsync(closed0, t0, committer.CommitAsync, Token);
            if (outcome.Value is DrainOutcome.Covered)
            {
                break;
            }

            var drained = Assert.IsType<DrainOutcome.Drained>(outcome.Value);
            _ = Assert.IsType<ManifestCommitOutcome.Applied>(drained.Outcome.Value);
            await manifest.ReadAsync(Token);
            if (drained.Change.Complete)
            {
                break;
            }
        }

        var dropper = new LedgerDropper(nodeA.Map, manifest, NullLogger<LedgerDropper>.Instance);
        Assert.NotEmpty(await dropper.DropCompleteAsync(env.CommitAsync, Token));
        _ = await nodeA.Map.ReadAsync(Token);
        await manifest.ReadAsync(Token);

        // After rebase onto the 13-byte archive prefix: BaseLength == 13, BytesSinceBase == 0 -> ReplaceRequested == false!
        var (rebasedVersion, exists, content) = await ReadJournalAsync(store, "rebased");
        Assert.True(exists);
        Assert.Equal("123456abcdef!", content);
        Assert.False(await ReadReplaceRequestedAsync(store, "rebased"));

        // Append 13 bytes onto the 13-byte archive base -> BytesSinceBase == 13 == BaseLength -> still false!
        var (v4, rr4) = await WriteAppendWithOutcomeAsync(store, "rebased", rebasedVersion, "0123456789abc");
        Assert.False(rr4);
        Assert.False(await ReadReplaceRequestedAsync(store, "rebased"));

        // Append 1 more byte -> BytesSinceBase == 14 == BaseLength + 1 -> flips to true!
        var (_, rr5) = await WriteAppendWithOutcomeAsync(store, "rebased", v4, "!");
        Assert.True(rr5);
        Assert.True(await ReadReplaceRequestedAsync(store, "rebased"));
        _ = v3;
    }

    /// <summary>
    /// An owner absence read through <see cref="JournalStore.ReadAsync"/> over a preloaded <see cref="LsmArchiveStore"/>
    /// and caught-up <see cref="JournalStateDatabase"/> makes zero object-store requests and zero replica slot reads
    /// (journals.md §8.2, §8.3, archive-lsm.md §8.3).
    /// </summary>
    [Fact]
    public async Task OwnerAbsenceReadOverPreloadedLsmAndKeyIndexesMakesZeroObjectStoreAndReplicaSlotCalls()
    {
        var objects = new CountingObjectStore(new InMemoryObjectStore());
        var manifest = new ManifestView(objects);
        await using var committer = new ManifestCommitter(objects, manifest, TimeProvider.System, TimeSpan.Zero);
        var lsmOptions = new ArchiveOptions { GridBits = 1 };
        var lsmStore = new LsmArchiveStore(objects, manifest, new TailCache(objects, lsmOptions), lsmOptions, TimeProvider.System);

        await using var env = new WarmTestEnvironment(lsmStore);
        var storageA = new InMemoryJournalIndex();
        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA);
        await env.BootstrapAsync(nodeA, [NodeA]);

        // Split root into Lower / Upper and drain one key ("a") into Lower's LSM section.
        var rootId = nodeA.Map.Current.Find(JournalRowKey.Encode("a").Span)!.Value.Open.Id;
        await env.SplitAsync(nodeA, rootId, [NodeA]);

        await using var store = new JournalStore(nodeA.Registry, lsmStore, new JournalStoreOptions(), env.Time, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);

        _ = await AppendAsync(store, "a", JournalVersion.Origin, "present-in-lower");
        var lowerMeta = nodeA.Map.Current.Find(JournalRowKey.Encode("a").Span)!.Value.Open;
        var lowerLedger = lowerMeta.Id;
        Assert.True((await nodeA.Registry.TryGet(lowerLedger)!.EndSegmentAsync(Token)).TryGetValue(out ProposalOutcome.Durable marker));

        var drainWorker = new DrainWorker(objects, manifest, nodeA.Reader, lsmOptions, TimeProvider.System);
        while (true)
        {
            var drained = Assert.IsType<DrainOutcome.Drained>((await drainWorker.DrainAsync(nodeA.Map.Current.Get(lowerLedger)!.Value, marker.Slot + 1, committer.CommitAsync, Token)).Value);
            _ = Assert.IsType<ManifestCommitOutcome.Applied>(drained.Outcome.Value);
            if (drained.Change.Below == marker.Slot)
            {
                break;
            }
        }

        // Preload this child's section metadata and catch up the open ledger's key index.
        await lsmStore.PreloadAsync([lowerMeta.Range]);
        var tail = Assert.IsType<LedgerTail>((await nodeA.Reader.ReadTailAsync(nodeA.Map.Current.Get(lowerLedger)!.Value, Token)).Value);
        nodeA.ApplyLoop.Wake(lowerLedger);
        await WaitUntilAsync(() => nodeA.StateDb.Watermark(lowerLedger) >= tail.ContiguousTail, Signal);

        // Pick an absent key in the same range (excluded by Bloom filter, zero live rows in key index):
        // expect 0 object-store calls AND 0 replica slot reads!
        string absentKey = null!;
        for (var i = 0; i < 1000; i++)
        {
            var candidate = $"absent-{i}";
            if (lowerMeta.Range.Contains(JournalRowKey.Encode(candidate).Span))
            {
                absentKey = candidate;
                break;
            }
        }

        Assert.NotNull(absentKey);
        var objectCallsBefore = objects.CallCount;
        nodeA.Transport.ResetCounts();

        var (version, exists, content) = await ReadJournalAsync(store, absentKey);
        Assert.False(exists);
        Assert.Equal(JournalVersion.Origin, version);
        Assert.Equal(string.Empty, content);
        Assert.Equal(objectCallsBefore, objects.CallCount);
        Assert.Empty(nodeA.Transport.ReadSlotsOf(lowerLedger));
    }

    private static async Task IndexedColdTombstoneRespectsRetentionAsync(bool ignoreRetentionMutant)
    {
        await using var env = new WarmTestEnvironment();
        var storageA = new InMemoryJournalIndex();
        var retention = TimeSpan.FromHours(2);
        var options = new JournalStoreOptions
        {
            TombstoneRetention = retention,
            WarmStateBytes = 0,
        };

        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA);
        await env.BootstrapAsync(nodeA, [NodeA]);
        await using var store = new JournalStore(nodeA.Registry, env.Store, options, env.Time, nodeA.StateDb)
        {
            IgnoreRetentionMutant = ignoreRetentionMutant,
        };
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);

        var v = await AppendAsync(store, "cold-tomb", JournalVersion.Origin, "x");
        _ = await DeleteAsync(store, "cold-tomb", v);

        var ledger0 = nodeA.Map.Current.Find(JournalRowKey.Encode("cold-tomb").Span)!.Value.Open.Id;
        var tail = Assert.IsType<LedgerTail>((await nodeA.Reader.ReadTailAsync(nodeA.Map.Current.Get(ledger0)!.Value, Token)).Value);
        nodeA.ApplyLoop.Wake(ledger0);
        await WaitUntilAsync(() => nodeA.StateDb.Watermark(ledger0) >= tail.ContiguousTail, Signal);

        var before = await store.ExpireTombstonesAsync(Token);
        Assert.True(before.Expired == 0, $"Cold tombstone expired before retention elapsed ({before.Expired} expired).");

        env.Time.Advance(retention);
        var after = await store.ExpireTombstonesAsync(Token);
        Assert.Equal(1, after.Expired);
    }

    private static async Task NonReplicatedPredecessorColdExpiryAsync(bool skipCatchUpMutant)
    {
        await using var env = new WarmTestEnvironment();
        var storageA = new InMemoryJournalIndex();
        var storageB = new InMemoryJournalIndex();
        var retention = TimeSpan.FromHours(1);
        var options = new JournalStoreOptions
        {
            TombstoneRetention = retention,
            WarmStateBytes = 0,
        };

        // NodeB owns ledger0 alone (NodeA is NOT in ledger0's replica set).
        await using var nodeB = await env.OpenNodeAsync(NodeB, storageB);
        await env.BootstrapAsync(nodeB, [NodeB]);
        await using var storeB = new JournalStore(nodeB.Registry, env.Store, options, env.Time, nodeB.StateDb);
        await nodeB.Registry.ReconciledAsync(nodeB.Map.Current.Epoch, Token);

        var ledger0 = nodeB.Map.Current.Find(JournalRowKey.Encode("nonrep-tomb").Span)!.Value.Open.Id;
        var v = await AppendAsync(storeB, "nonrep-tomb", JournalVersion.Origin, "owned-by-B");
        _ = await DeleteAsync(storeB, "nonrep-tomb", v);

        // Open NodeA, transfer ownership of the range to NodeA (closing non-replicated ledger0 at T).
        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA);
        nodeA.StateDb.SkipCatchUpMutant = skipCatchUpMutant;
        await env.ReplaceAsync(nodeA, ledger0, [NodeA]);
        await using var storeA = new JournalStore(nodeA.Registry, env.Store, options, env.Time, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);

        env.Time.Advance(retention + TimeSpan.FromMinutes(1));
        var scan = await storeA.ExpireTombstonesAsync(Token);
        Assert.True(scan.Expired == 1, $"Non-replicated closed predecessor cold expiry expected 1 expired cold tombstone, got {scan.Expired}.");

        var (_, exists, _) = await ReadJournalAsync(storeA, "nonrep-tomb");
        Assert.False(exists);
    }

    private static async Task KeepWarmAcrossClaimsAsync(bool keepMutant)
    {
        await using var env = new WarmTestEnvironment();
        var storageA = new InMemoryJournalIndex();
        var storageB = new InMemoryJournalIndex();
        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA);
        await using var nodeB = await env.OpenNodeAsync(NodeB, storageB);

        await env.BootstrapAsync(nodeA, [NodeA]);
        await nodeB.Map.ReadAsync(Token);

        await using var storeA = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), env.Time, nodeA.StateDb)
        {
            KeepWarmStateAcrossClaimsMutant = keepMutant,
        };
        await using var storeB = new JournalStore(nodeB.Registry, env.Store, new JournalStoreOptions(), env.Time, nodeB.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);

        // NodeA serves ledger 0 and warms "k" at v1.
        var ledger0 = nodeA.Map.Current.Find(JournalRowKey.Encode("k").Span)!.Value.Open.Id;
        var v1 = await AppendAsync(storeA, "k", JournalVersion.Origin, "from-A");

        // Hand off to NodeB: ledger 0 closes, ledger 1 opens on NodeB; NodeB advances "k" to v2.
        await env.ReplaceAsync(nodeB, ledger0, [NodeB]);
        await nodeB.Map.ReadAsync(Token);
        await nodeB.Registry.ReconciledAsync(nodeB.Map.Current.Epoch, Token);
        var ledger1 = nodeB.Map.Current.Find(JournalRowKey.Encode("k").Span)!.Value.Open.Id;
        var v2 = await AppendAsync(storeB, "k", v1, "+from-B");

        // Hand back to NodeA: ledger 1 closes, ledger 2 opens on NodeA.
        await env.ReplaceAsync(nodeA, ledger1, [NodeA]);
        await nodeA.Map.ReadAsync(Token);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);
        await WaitUntilAsync(() => !storeA.ServedLedgers.Contains(ledger0), Signal);

        // Writing at stale v1 MUST be Rejected with v2 (NodeA must have discarded ledger 0's warm state).
        using var stalePayload = ArcBuffer.Wrap("+stale"u8.ToArray());
        using var staleRecord = storeA.EncodeAppend(JournalRowKey.Encode("k"), v1, stalePayload);
        var outcome = await storeA.WriteAsync(staleRecord, Token);
        Assert.True(
            outcome.Value is JournalWriteOutcome.Rejected rejected && rejected.CurrentVersion == v2,
            $"Writing at stale version {v1} after handoff back to NodeA answered {outcome.Value} instead of Rejected({v2}).");
    }

    private static async Task ApplyOnAdmitAsync(bool applyOnAdmitMutant)
    {
        await using var env = new WarmTestEnvironment();
        var storageA = new InMemoryJournalIndex();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockPropose = 0;

        await using var nodeA = await env.OpenNodeAsync(
            NodeA,
            storageA,
            onPropose: () =>
            {
                if (Volatile.Read(ref blockPropose) != 0)
                {
                    _ = proposeEntered.TrySetResult();
                    return gate.Task;
                }

                return Task.CompletedTask;
            });
        await env.BootstrapAsync(nodeA, [NodeA]);
        await using var store = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), env.Time, nodeA.StateDb)
        {
            ApplyOnAdmitMutant = applyOnAdmitMutant,
        };
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);

        // Seed v1 first so "k" is warm.
        var v1 = await AppendAsync(store, "k", JournalVersion.Origin, "base");

        // Gate the next replica proposal so v1->v2 is admitted & in flight, NOT yet durable.
        Volatile.Write(ref blockPropose, 1);
        using var p2 = ArcBuffer.Wrap("+v2"u8.ToArray());
        using var r2 = store.EncodeAppend(JournalRowKey.Encode("k"), v1, p2);
        var write2 = store.WriteAsync(r2, Token);
        await proposeEntered.Task.WaitAsync(Signal, Token);

        // While write2 is NOT durable, write a conflicting entry at JournalVersion.Origin (which applies to neither v1 nor v2)
        // so Rejected(CurrentVersion) reports warm state's applied version!
        using var conflictPayload = ArcBuffer.Wrap("conflict"u8.ToArray());
        using var conflictRecord = store.EncodeAppend(JournalRowKey.Encode("k"), JournalVersion.Origin, conflictPayload);
        var rejected = Assert.IsType<JournalWriteOutcome.Rejected>((await store.WriteAsync(conflictRecord, Token)).Value);
        try
        {
            Assert.True(
                rejected.CurrentVersion == v1,
                $"Warm state exposed version {rejected.CurrentVersion} before durability (applied version was {v1}).");
        }
        finally
        {
            Volatile.Write(ref blockPropose, 0);
            gate.SetResult();
            _ = await write2;
        }
    }

    private static async Task OwnerReadVersionMatchesContentAsync(bool mutant)
    {
        await using var env = new WarmTestEnvironment();
        var storageA = new InMemoryJournalIndex();
        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA);
        await env.BootstrapAsync(nodeA, [NodeA]);
        await using var store = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), env.Time, nodeA.StateDb)
        {
            AnswersWarmStateNewerVersionMutant = mutant,
        };
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);

        var v1 = await AppendAsync(store, "cut", JournalVersion.Origin, "first");
        var ledger0 = nodeA.Map.Current.Find(JournalRowKey.Encode("cut").Span)!.Value.Open.Id;
        var tail1 = Assert.IsType<LedgerTail>((await nodeA.Reader.ReadTailAsync(nodeA.Map.Current.Get(ledger0)!.Value, Token)).Value);
        nodeA.ApplyLoop.Wake(ledger0);
        await WaitUntilAsync(() => nodeA.StateDb.Watermark(ledger0) >= tail1.ContiguousTail, Signal);

        // Gate the read's replica slot read so a concurrent write v1 -> v2 completes and updates warm state
        // AFTER the read's cut was already established at tail1!
        var readPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseNextRead = 1;
        nodeA.Transport.OnRead = (_, _) =>
        {
            if (Interlocked.Exchange(ref pauseNextRead, 0) == 1)
            {
                _ = readPaused.TrySetResult();
                return releaseRead.Task;
            }

            return Task.CompletedTask;
        };

        var readTask = ReadJournalAsync(store, "cut");
        await readPaused.Task.WaitAsync(Signal, Token);

        // Advance warm state to v2 while the read is paused on its earlier cut.
        var v2 = await AppendAsync(store, "cut", v1, "+second");
        releaseRead.SetResult();

        var (readVersion, exists, content) = await readTask;
        Assert.True(exists);
        Assert.Equal("first", content);
        Assert.True(
            readVersion == v1,
            $"Owner read returned content 'first' at version {v1}, but its version {readVersion} matched warm state's newer version {v2}.");
    }

    /// <summary>
    /// When a concurrent foreground write changes a cold tombstone's warm state while <c>onProposed</c> runs outside
    /// <see cref="LedgerAdmission"/>'s lock, <see cref="JournalStore.ExpireColdAsync"/> re-checks warm state under the
    /// lock, skips submitting a stale <c>Expired</c>, and invokes its <c>observed</c> callback outside the lock.
    /// </summary>
    [Fact]
    public async Task ColdExpirySkipsSubmissionAndRunsObservedOutsideLockWhenForegroundWriteIntervenesDuringOnProposed()
    {
        await using var env = new WarmTestEnvironment();
        var storageA = new InMemoryJournalIndex();
        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA);
        await env.BootstrapAsync(nodeA, [NodeA]);

        var retention = TimeSpan.FromMinutes(10);
        var options = new JournalStoreOptions { TombstoneRetention = retention };
        await using (var store1 = new JournalStore(nodeA.Registry, env.Store, options, env.Time, nodeA.StateDb))
        {
            await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);
            var v1 = await AppendAsync(store1, "raced-tomb", JournalVersion.Origin, "initial");
            _ = await DeleteAsync(store1, "raced-tomb", v1);
        }

        // Advance past retention and reopen the store so "raced-tomb" is cold in the key index.
        env.Time.Advance(retention + TimeSpan.FromMinutes(1));
        await using var store2 = new JournalStore(nodeA.Registry, env.Store, options, env.Time, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);

        var key = JournalRowKey.Encode("raced-tomb");
        var observedCalled = false;
        var observedSawUnlockedStore = false;
        JournalWriteOutcome? observedOutcome = null;
        JournalVersion? resurrectedVersion = null;

        store2.OnExpiryProposed = (proposedKey, version) =>
        {
            Assert.Equal(key, proposedKey);
            // Resurrect the key via a foreground write while ExpireColdAsync has released _lock.
            resurrectedVersion = Task.Run(() => AppendAsync(store2, "raced-tomb", version, "resurrected"), Token)
                .GetAwaiter()
                .GetResult();
            return outcome =>
            {
                observedCalled = true;
                observedOutcome = outcome;
                // Acquire _lock from another thread; if observed ran under _lock this would deadlock.
                var readDone = Task.Run(() => ReadJournalAsync(store2, "raced-tomb"), Token).Wait(TimeSpan.FromSeconds(5), Token);
                observedSawUnlockedStore = readDone;
            };
        };

        var result = await store2.ExpireColdAsync(key, Token);
        Assert.Null(result);
        Assert.NotNull(resurrectedVersion);
        Assert.True(observedCalled);
        Assert.Null(observedOutcome);
        Assert.True(observedSawUnlockedStore, "observed callback must run outside LedgerAdmission._lock.");

        var (currentVersion, exists, content) = await ReadJournalAsync(store2, "raced-tomb");
        Assert.True(exists);
        Assert.Equal(resurrectedVersion, currentVersion);
        Assert.Equal("resurrected", content);
    }

    /// <summary>
    /// Cancelling a non-replicated predecessor scan while <c>FlushPendingAsync</c> runs concurrently with an in-flight
    /// <c>MoveNextAsync</c> cancels and drains the in-flight read cleanly without overlapping <c>DisposeAsync</c> or
    /// leaking any <see cref="ArcBuffer"/>.
    /// </summary>
    [Fact]
    public async Task PredecessorScanFlushCancellationDrainsInFlightMoveNextWithoutLeakingBuffers()
    {
        await using var env = new WarmTestEnvironment();
        var storageA = new InMemoryJournalIndex();
        var storageB = new InMemoryJournalIndex();
        await using var nodeA = await env.OpenNodeAsync(NodeA, storageA);

        var slot1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slot1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LedgerId closedLedgerId = default;
        Task? disposeStateDbTask = null;
        WarmNode? nodeBRef = null;

        await using var nodeB = await env.OpenNodeAsync(
            NodeB,
            storageB,
            onRead: (ledger, slot) =>
            {
                if (ledger == closedLedgerId && slot == new Slot(1))
                {
                    // Cancel _stop synchronously before MoveNextAsync returns an incomplete task so FlushPendingAsync
                    // immediately sees cancellation while MoveNextAsync is still in flight.
                    disposeStateDbTask ??= nodeBRef!.StateDb.DisposeAsync().AsTask();
                    _ = slot1Started.TrySetResult();
                    return slot1Release.Task;
                }

                return Task.CompletedTask;
            });
        nodeBRef = nodeB;
        nodeB.Transport.IgnoreReadCancellation = true;

        await env.BootstrapAsync(nodeA, [NodeA]);
        await nodeB.Map.ReadAsync(Token);

        await using var storeA = new JournalStore(nodeA.Registry, env.Store, new JournalStoreOptions(), env.Time, nodeA.StateDb);
        await nodeA.Registry.ReconciledAsync(nodeA.Map.Current.Epoch, Token);

        var ledger0 = nodeA.Map.Current.Find(JournalRowKey.Encode("k0").Span)!.Value.Open.Id;
        _ = await AppendAsync(storeA, "k0", JournalVersion.Origin, "slot-0");
        _ = await AppendAsync(storeA, "k1", JournalVersion.Origin, "slot-1");

        // Close ledger 0 and hand off to NodeB (non-replica of ledger 0).
        await env.ReplaceAsync(nodeB, ledger0, [NodeB]);
        await nodeB.Map.ReadAsync(Token);
        var closedLedger = nodeB.Map.Current.Get(ledger0)!.Value;
        var closedT = Assert.IsType<LedgerStatus.Closed>(closedLedger.Status.Value).T;
        Assert.True(closedT >= new Slot(2));

        // Arm the hook only for PredecessorScan's reads, not transition recovery.
        closedLedgerId = ledger0;
        var indexTask = nodeB.StateDb.KeyIndex.ReadAsync(closedLedger, closedT, JournalRowKey.Encode("k0").Memory, Token).AsTask();
        await slot1Started.Task.WaitAsync(Signal, Token);
        _ = slot1Release.TrySetResult();

        Assert.NotNull(disposeStateDbTask);
        await disposeStateDbTask.WaitAsync(Signal, Token);
        try
        {
            _ = await indexTask.WaitAsync(Signal, Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task<JournalVersion> AppendAsync(JournalStore store, string key, JournalVersion expected, string payload) =>
        (await WriteAppendWithOutcomeAsync(store, key, expected, payload)).Version;

    private static async Task<(JournalVersion Version, bool ReplaceRequested)> WriteAppendWithOutcomeAsync(JournalStore store, string key, JournalVersion expected, string payload)
    {
        using var buffer = ArcBuffer.Wrap(Encoding.UTF8.GetBytes(payload));
        using var record = store.EncodeAppend(JournalRowKey.Encode(key), expected, buffer);
        var accepted = Assert.IsType<JournalWriteOutcome.Accepted>((await store.WriteAsync(record, Token)).Value);
        return (accepted.Version, accepted.ReplaceRequested);
    }

    private static async Task<(JournalVersion Version, bool ReplaceRequested)> WriteReplaceWithOutcomeAsync(JournalStore store, string key, JournalVersion expected, string payload)
    {
        using var buffer = ArcBuffer.Wrap(Encoding.UTF8.GetBytes(payload));
        using var record = store.EncodeReplace(JournalRowKey.Encode(key), expected, buffer);
        var accepted = Assert.IsType<JournalWriteOutcome.Accepted>((await store.WriteAsync(record, Token)).Value);
        return (accepted.Version, accepted.ReplaceRequested);
    }

    private static async Task<JournalVersion> DeleteAsync(JournalStore store, string key, JournalVersion expected) =>
        (await WriteDeleteWithOutcomeAsync(store, key, expected)).Version;

    private static async Task<(JournalVersion Version, bool ReplaceRequested)> WriteDeleteWithOutcomeAsync(JournalStore store, string key, JournalVersion expected)
    {
        using var record = store.EncodeDelete(JournalRowKey.Encode(key), expected);
        var accepted = Assert.IsType<JournalWriteOutcome.Accepted>((await store.WriteAsync(record, Token)).Value);
        return (accepted.Version, accepted.ReplaceRequested);
    }

    private static async Task<bool> ReadReplaceRequestedAsync(JournalStore store, string key)
    {
        var read = Assert.IsType<JournalReadResult>((await store.ReadAsync(JournalRowKey.Encode(key), Token)).Value);
        using (read)
        {
            return read.ReplaceRequested;
        }
    }

    private static async Task<(JournalVersion Version, bool Exists, string Content)> ReadJournalAsync(JournalStore store, string key)
    {
        var read = Assert.IsType<JournalReadResult>((await store.ReadAsync(JournalRowKey.Encode(key), Token)).Value);
        using (read)
        {
            var sb = new StringBuilder();
            await foreach (var chunk in read.ReadPayloadsAsync(Token))
            {
                using (chunk)
                {
                    _ = sb.Append(Encoding.UTF8.GetString(chunk.ToArray()));
                }
            }

            return (read.CurrentVersion, read.Exists, sb.ToString());
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = TimeProvider.System.GetUtcNow() + timeout;
        while (!condition())
        {
            if (TimeProvider.System.GetUtcNow() >= deadline)
            {
                throw new TimeoutException($"Condition was not met within {timeout}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Token);
        }
    }

    /// <summary>Counts every call to an underlying <see cref="IObjectStore"/>.</summary>
    private sealed class CountingObjectStore(IObjectStore inner) : IObjectStore
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return inner.CreateAsync(name, content, crc32c, cancellationToken);
        }

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return inner.ReadAsync(name, offset, length, cancellationToken);
        }

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return inner.GetPropertiesAsync(name, cancellationToken);
        }

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return inner.ListAsync(prefix, startAfter, cancellationToken);
        }

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return inner.DeleteAsync(name, cancellationToken);
        }

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return inner.ReadVersionedAsync(name, cancellationToken);
        }

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
        }
    }

    private sealed class WarmTestEnvironment : IAsyncDisposable
    {
        private readonly InMemoryLedgerReplicaTransport _transport = new();
        private readonly InMemoryConditionalRegister _register = new();
        private readonly LedgerMapCommitter _committer;
        private readonly List<WarmNode> _nodes = [];
        private readonly IArchiveStore? _externalStore;
        private readonly InMemoryArchiveStore? _memoryStore;

        public WarmTestEnvironment(IArchiveStore? store = null)
        {
            _externalStore = store;
            _memoryStore = store is null ? new InMemoryArchiveStore() : null;
            _committer = new LedgerMapCommitter(_register, 1 << 20);
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));

        public IArchiveStore Store => _externalStore ?? _memoryStore!;

        public async Task<WarmNode> OpenNodeAsync(
            NodeId nodeId,
            InMemoryJournalIndex storage,
            Func<LedgerId, Slot, Task>? onRead = null,
            Func<Task>? onPropose = null)
        {
            var node = await WarmNode.OpenAsync(nodeId, _register, _transport, storage, Time, onRead, onPropose);
            _nodes.Add(node);
            return node;
        }

        public async Task BootstrapAsync(WarmNode node, NodeId[] ensemble)
        {
            var committer = GetCommitter();
            var executor = new TransitionExecutor(node.Registry, NullLogger<TransitionExecutor>.Instance);
            var choice = new TransitionChoice(node.NodeId, ImmutableArray.Create(ensemble), 4, 0, 0);
            Assert.IsType<LedgerMapCommitOutcome.Applied>((await executor.ExecuteAsync(new Transition.Bootstrap(choice), committer.CommitAsync, Token)).Value);
            _ = await node.Map.ReadAsync(Token);
        }

        public async Task ReplaceAsync(WarmNode node, LedgerId ledgerId, NodeId[] nextEnsemble)
        {
            var committer = GetCommitter();
            _ = await node.Map.ReadAsync(Token);
            var executor = new TransitionExecutor(node.Registry, NullLogger<TransitionExecutor>.Instance);
            var choice = new TransitionChoice(nextEnsemble[0], ImmutableArray.Create(nextEnsemble), 4, 0, node.Map.Current.Epoch);
            Assert.IsType<LedgerMapCommitOutcome.Applied>((await executor.ExecuteAsync(new Transition.Replace(ledgerId, choice), committer.CommitAsync, Token)).Value);
            _ = await node.Map.ReadAsync(Token);
        }

        public Task<LedgerMapCommitOutcome> CommitAsync(LedgerMapChange change, CancellationToken cancellationToken) =>
            GetCommitter().CommitAsync(change, cancellationToken);

        public async Task SplitAsync(WarmNode node, LedgerId ledgerId, NodeId[] ensemble)
        {
            var committer = GetCommitter();
            _ = await node.Map.ReadAsync(Token);
            var executor = new TransitionExecutor(node.Registry, NullLogger<TransitionExecutor>.Instance);
            var choice = new TransitionChoice(ensemble[0], ImmutableArray.Create(ensemble), 4, 0, node.Map.Current.Epoch);
            Assert.IsType<LedgerMapCommitOutcome.Applied>((await executor.ExecuteAsync(new Transition.Split(ledgerId, choice), committer.CommitAsync, Token)).Value);
            _ = await node.Map.ReadAsync(Token);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var node in _nodes)
            {
                await node.DisposeAsync();
            }

            if (_committer is not null)
            {
                await _committer.DisposeAsync();
            }

            _memoryStore?.Dispose();
            _transport.Dispose();
        }

        private LedgerMapCommitter GetCommitter() => _committer ?? throw new InvalidOperationException("Committer not initialized.");
    }

    private sealed class WarmNode : IAsyncDisposable
    {
        private JournalStateDatabase? _stateDb;
        private LedgerApplyLoop? _applyLoop;
        private bool _disposed;

        private WarmNode(NodeId nodeId, HookedReplicaTransport transport, LedgerMapView map)
        {
            NodeId = nodeId;
            Transport = transport;
            Map = map;
            Reader = new LedgerReader(map, transport, TimeProvider.System, nodeId);
            Registry = new LedgerWriterRegistry(Reader, 1 << 25, LedgerWriterOptions.Default, TimeProvider.System, NullLoggerFactory.Instance);
        }

        public NodeId NodeId { get; }

        public HookedReplicaTransport Transport { get; }

        public LedgerMapView Map { get; }

        public LedgerReader Reader { get; }

        public LedgerWriterRegistry Registry { get; }

        public JournalStateDatabase StateDb => _stateDb ?? throw new InvalidOperationException("Not opened.");

        public LedgerApplyLoop ApplyLoop => _applyLoop ?? throw new InvalidOperationException("Not opened.");

        public static async Task<WarmNode> OpenAsync(
            NodeId nodeId,
            InMemoryConditionalRegister register,
            ILedgerReplicaTransport transport,
            InMemoryJournalIndex storage,
            TimeProvider timeProvider,
            Func<LedgerId, Slot, Task>? onRead,
            Func<Task>? onPropose)
        {
            var map = await LedgerMapView.OpenAsync(register, Token);
            var hooked = new HookedReplicaTransport(transport, onRead, onPropose);
            var node = new WarmNode(nodeId, hooked, map);
            try
            {
                await node.InitializeAsync(storage, timeProvider);
                return node;
            }
            catch
            {
                await node.DisposeAsync();
                throw;
            }
        }

        private async Task InitializeAsync(InMemoryJournalIndex storage, TimeProvider timeProvider)
        {
            var stateOptions = new JournalIndexOptions
            {
                FlushInterval = Timeout.InfiniteTimeSpan,
                QuickCheckInterval = Timeout.InfiniteTimeSpan,
            };
            _stateDb = await JournalStateDatabase.OpenAsync(storage, Reader, stateOptions, timeProvider, Token);
            _applyLoop = new LedgerApplyLoop(Reader, _stateDb.Applier, TimeProvider.System);
            _stateDb.Attach(_applyLoop);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
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

    private sealed class HookedReplicaTransport(
        ILedgerReplicaTransport inner,
        Func<LedgerId, Slot, Task>? onRead,
        Func<Task>? onPropose) : ILedgerReplicaTransport
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<LedgerId, HashSet<long>> _readSlots = [];

        public Func<LedgerId, Slot, Task>? OnRead { get; set; } = onRead;

        public Func<Task>? OnPropose { get; set; } = onPropose;

        public bool IgnoreReadCancellation { get; set; }

        public void ResetCounts()
        {
            lock (_lock)
            {
                _readSlots.Clear();
            }
        }

        public HashSet<long> ReadSlotsOf(LedgerId ledger)
        {
            lock (_lock)
            {
                return _readSlots.TryGetValue(ledger, out var set) ? [.. set] : [];
            }
        }

        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            new HookedReplica(this, ledger, inner.Open(node, ledger, options));

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(node, ledgers, cancellationToken);

        private void RecordRead(LedgerId ledger, Slot slot)
        {
            lock (_lock)
            {
                if (!_readSlots.TryGetValue(ledger, out var set))
                {
                    set = [];
                    _readSlots[ledger] = set;
                }

                _ = set.Add(slot.Value);
            }
        }

        private sealed class HookedReplica(HookedReplicaTransport owner, LedgerId ledger, ILedgerReplica inner) : ILedgerReplica
        {
            public async ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
            {
                if (owner.OnPropose is { } hook)
                {
                    await hook().WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken).ConfigureAwait(false);
            }

            public async ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
            {
                if (owner.OnRead is { } hook)
                {
                    var task = hook(ledger, slot);
                    await (owner.IgnoreReadCancellation ? task : task.WaitAsync(cancellationToken)).ConfigureAwait(false);
                }

                owner.RecordRead(ledger, slot);
                var readToken = owner.IgnoreReadCancellation ? CancellationToken.None : cancellationToken;
                return await inner.ReadAsync(slot, contiguousTail, readToken).ConfigureAwait(false);
            }

            public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

            public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
                inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
