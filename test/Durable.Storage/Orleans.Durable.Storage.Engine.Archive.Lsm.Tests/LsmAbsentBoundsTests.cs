using System.Text;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// <see cref="LsmArchiveStore"/>'s per-ledger <see cref="ArchiveStoreRead.Absent.Bounds"/> (archive.md §8.1): an
/// <c>Absent</c> answer carries the drained watermark for every incomplete listed ledger (open or closed) when the key
/// has zero rows in the store, omits bounds when the key has rows in a listed ledger, and omits complete listed ledgers
/// because compaction may drop an empty expiry there; <see cref="LsmArchiveStore.BoundCompleteLedgerMutant"/>
/// deterministically fails both the store contract and an end-to-end <see cref="ArchiveReader"/> check.
/// </summary>
public sealed class LsmAbsentBoundsTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// When a listed ledger (open or closed) is partly drained (incomplete) through watermark <c>w</c>,
    /// <see cref="LsmArchiveStore.ReadAsync"/> for an absent key returns <see cref="ArchiveStoreRead.Absent"/> with
    /// <c>Bound(ledger) == w</c>, while querying a key that has drained rows in that listed ledger returns
    /// <see cref="ArchiveStoreRead.Absent"/> with <c>Bound(ledger) == Slot.Zero</c>.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AbsentReturnsPerLedgerBoundsForIncompleteOpenAndClosedListedLedgers()
    {
        var token = TestContext.Current.CancellationToken;
        await using var world = await ArchiveWorld.StartAsync(token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a1")], token);
        var worker = world.Worker();
        var drainedEnd = (await world.DrainOpenAsync(worker, ledger, token)).Below;
        Assert.True(drainedEnd > Slot.Zero);

        var store = world.ArchiveStore();

        // Key "b" has zero rows in the store -> Absent carries Bound(ledger) == drainedEnd.
        var readB = await store.ReadAsync(Encoding.UTF8.GetBytes("b"), world.Ledgers.Map.Current, token);
        var absentB = Assert.IsType<ArchiveStoreRead.Absent>(readB.Value);
        Assert.Equal(drainedEnd, absentB.Bound(ledger));

        // Key "a" has rows in the partly-drained listed ledger -> Absent omits bounds (Bound == Slot.Zero).
        var readA = await store.ReadAsync(Encoding.UTF8.GetBytes("a"), world.Ledgers.Map.Current, token);
        var absentA = Assert.IsType<ArchiveStoreRead.Absent>(readA.Value);
        Assert.Equal(Slot.Zero, absentA.Bound(ledger));

        // Append another segment and close the ledger without draining segment 1: it is now a CLOSED incomplete ledger.
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a2")], token);
        await world.Ledgers.ReplaceAsync(ledger, token);
        var closedMetadata = world.Ledgers.Ledger(ledger);
        var closedT = Assert.IsType<LedgerStatus.Closed>(closedMetadata.Status.Value).T;
        Assert.True(drainedEnd < closedT);

        var closedReadB = await store.ReadAsync(Encoding.UTF8.GetBytes("b"), world.Ledgers.Map.Current, token);
        var closedAbsentB = Assert.IsType<ArchiveStoreRead.Absent>(closedReadB.Value);
        Assert.Equal(drainedEnd, closedAbsentB.Bound(ledger));

        var closedReadA = await store.ReadAsync(Encoding.UTF8.GetBytes("a"), world.Ledgers.Map.Current, token);
        var closedAbsentA = Assert.IsType<ArchiveStoreRead.Absent>(closedReadA.Value);
        Assert.Equal(Slot.Zero, closedAbsentA.Bound(ledger));
    }

    /// <summary>
    /// A complete listed ledger whose rows for a key may be compacted away (such as an empty expiry) is omitted from
    /// <see cref="ArchiveStoreRead.Absent.Bounds"/> so an unindexed <see cref="ArchiveReader"/> never skips live rows
    /// above or in that complete ledger; <see cref="LsmArchiveStore.BoundCompleteLedgerMutant"/> deterministically
    /// loses the key's rows end-to-end.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AbsentOmitsCompleteLedgersAndBoundCompleteLedgerMutantLosesCompactedExpiryRowsEndToEnd()
    {
        var token = TestContext.Current.CancellationToken;
        await using var world = await ArchiveWorld.StartAsync(token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        var key = Encoding.UTF8.GetBytes("k");

        // Segment 0 & 1: Append "k"; Segment 2: empty Delete (expiry) of "k" -> three drained sections in `ledger`.
        var worker = world.Worker();
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("k", RowKind.Append, "v1")], token);
        _ = await world.DrainOpenAsync(worker, ledger, token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("k", RowKind.Append, "v2")], token);
        _ = await world.DrainOpenAsync(worker, ledger, token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("k", RowKind.Delete, string.Empty)], token);
        await world.Ledgers.ReplaceAsync(ledger, token);
        _ = await world.CompleteAsync(worker, ledger, token);

        // Compact the three sections while `ledger` is still listed and complete: compaction drops the expired key's rows.
        var options = new ArchiveOptions { GridBits = 1, MaxSectionsPerPartition = 2 };
        var compactor = new CompactionWorker(world.Store, new TailCache(world.Store, options), world.Manifest, options);
        var manifest = (await world.Manifest.ReadAsync(token)).Value;
        var compacted = Assert.IsType<CompactionOutcome.Compacted>(
            (await compactor.CompactAsync(KeyRange.Root, manifest, world.Ledgers.Map.Current, token)).Value);
        _ = Assert.IsType<ManifestCommitOutcome.Applied>((await world.CommitAsync(compacted.Change, token)).Value);

        // Store holds zero rows of "k", yet `ledger` is complete and still listed: Absent MUST omit `ledger`.
        var store = world.ArchiveStore(options: options);
        var read = await store.ReadAsync(key, world.Ledgers.Map.Current, token);
        var absent = Assert.IsType<ArchiveStoreRead.Absent>(read.Value);
        Assert.Equal(Slot.Zero, absent.Bound(ledger));
        Assert.Same(ArchiveStoreRead.Absent.Instance, absent);

        // End-to-end with unindexed ArchiveReader: reads all live rows of `ledger` (two Appends + empty Delete).
        var reader = new ArchiveReader(world.Ledgers.Reader, store, TimeProvider.System);
        using (var history = await reader.ReadAsync(key, token))
        {
            Assert.Equal(3, history.Rows.Length);
        }

        // Mutant check: bounding a complete ledger causes ArchiveReader (keyIndex: null) to skip `ledger` and lose its rows!
        store.BoundCompleteLedgerMutant = true;
        var mutantRead = await store.ReadAsync(key, world.Ledgers.Map.Current, token);
        var mutantAbsent = Assert.IsType<ArchiveStoreRead.Absent>(mutantRead.Value);
        Assert.NotEqual(Slot.Zero, mutantAbsent.Bound(ledger));

        using var mutantHistory = await reader.ReadAsync(key, token);
        Assert.Empty(mutantHistory.Rows.ToArray());
    }
}
