using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Examples of <see cref="LsmArchiveStore"/> (archive-lsm.md §2.5, §2.8, §8.3, §8.6): absence from the sections' metadata,
/// a key's prefix assembled from its rows of the ledgers the caller's map dropped, with the tag of its last row as its
/// version base, a view which covers every ledger the caller's map dropped (LSM-I4), <c>Stale</c> once an object the
/// view names is gone, and the store's record.
/// </summary>
public sealed class LsmArchiveStoreTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static readonly Dictionary<string, string> Parameters = new(StringComparer.Ordinal) { ["format"] = "json" };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task AKeyIsAbsentWhileTheArchiveHoldsNoSection()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);

        Assert.IsType<ArchiveStoreRead.Absent>((await world.ArchiveStore().ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token)).Value);
    }

    /// <summary>
    /// A key's drained rows of a dropped ledger read back from every section containing it as its prefix, its version base
    /// the tag of its last row; a key which the sections' Bloom filters exclude is absent, which reads no block.
    /// </summary>
    [Fact]
    public async Task AKeysRowsReadBackAsItsPrefixAndAnExcludedKeyIsAbsent()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        var a0 = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0"), LiveRow.Of("b", RowKind.Append, "b0")], Token);
        var worker = world.Worker();
        _ = await world.DrainOpenAsync(worker, ledger, Token);
        var a1 = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a1")], Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("c", RowKind.Append, "c0")], Token);
        _ = await world.DrainOpenAsync(worker, ledger, Token);
        await world.CloseCompleteAndDropAsync(ledger, Token);
        var store = world.ArchiveStore();

        using (var read = await ReadAsync(store, world, "a"))
        {
            Assert.Equal("a0a1", Content(read));
            Assert.Equal(LedgerMapCoverage.VersionBaseOf(a1[0]), read.Prefix.VersionBase);
            Assert.True(read.Prefix.Token.IsEmpty);
            Assert.False(read.Prefix.HasBase);
            Assert.NotEqual(LedgerMapCoverage.VersionBaseOf(a0[0]), read.Prefix.VersionBase);
        }

        var blockReads = world.DrainOutputReads();
        Assert.IsType<ArchiveStoreRead.Absent>((await store.ReadAsync("absent-key"u8.ToArray(), world.Ledgers.Map.Current, Token)).Value);
        Assert.Equal(blockReads, world.DrainOutputReads());
    }

    /// <summary>
    /// A prefix folds a key's rows from the last <c>Replace</c> or <c>Delete</c> among them (archive-lsm.md §8.6): a
    /// <c>Replace</c> base sets <c>HasBase</c>, and a key whose base is an empty <c>Delete</c> reads back with no content,
    /// no base, and no tombstone, and one with a tombstone while no <c>Append</c> follows it.
    /// </summary>
    [Fact]
    public async Task APrefixFoldsTheRowsFromTheLastReplaceOrDelete()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var replaced = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Replace, "a1"), LiveRow.Of("a", RowKind.Append, "a2")], Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("b", RowKind.Append, "b0")], Token);
        var deleted = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("b", RowKind.Delete, string.Empty)], Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("c", RowKind.Delete, "tombstone")], Token);
        await world.CloseCompleteAndDropAsync(ledger, Token);
        var store = world.ArchiveStore();

        using (var read = await ReadAsync(store, world, "a"))
        {
            Assert.Equal(("a1a2", true, LedgerMapCoverage.VersionBaseOf(replaced[1])), (Content(read), read.Prefix.HasBase, read.Prefix.VersionBase));
        }

        using (var read = await ReadAsync(store, world, "b"))
        {
            Assert.Equal((string.Empty, false), (Content(read), read.Prefix.HasBase));
            Assert.Null(read.Prefix.Tombstone);
            Assert.Equal(LedgerMapCoverage.VersionBaseOf(deleted[0]), read.Prefix.VersionBase);
        }

        using (var read = await ReadAsync(store, world, "c"))
        {
            Assert.Equal("tombstone", Encoding.UTF8.GetString(read.Prefix.Tombstone!.Value.Span));
        }
    }

    /// <summary>
    /// LSM-I4: a store whose view predates a drop reads the manifest again for a map value which no longer lists the
    /// dropped ledger, so the dropped ledger's rows, which no live ledger holds for the reader any more, are among its
    /// answer; a map value which still lists it reads nothing more. The drop itself took a manifest value showing the
    /// ledger complete (LSM-I3).
    /// </summary>
    [Fact]
    public async Task AViewWhichPredatesADropReadsTheManifestAgainForAMapWhichDroppedTheLedger()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        var a = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var store = world.ArchiveStore();
        var before = world.Ledgers.Map.Current;
        Assert.IsType<ArchiveStoreRead.Absent>((await store.ReadAsync("a"u8.ToArray(), before, Token)).Value);

        await world.Ledgers.ReplaceAsync(ledger, Token);
        _ = await world.CompleteAsync(world.Worker(), ledger, Token);
        var dropper = new LedgerDropper(world.Ledgers.Map, world.Manifest, NullLogger<LedgerDropper>.Instance);
        Assert.Equal(new[] { ledger }, await dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token));

        var reads = world.ManifestReads;
        Assert.IsType<ArchiveStoreRead.Absent>((await store.ReadAsync("a"u8.ToArray(), before, Token)).Value);
        Assert.Equal(reads, world.ManifestReads);

        using var read = await ReadAsync(store, world, "a");
        Assert.Equal(("a0", LedgerMapCoverage.VersionBaseOf(a[0])), (Content(read), read.Prefix.VersionBase));
        Assert.Equal(reads + 1, world.ManifestReads);
    }

    /// <summary>
    /// An object the store's view names which a compaction removed answers <c>Stale</c>, once a manifest read after the
    /// miss no longer lists its section, and the next read uses that manifest; an absent object which the manifest still
    /// lists is corrupt.
    /// </summary>
    [Fact]
    public async Task AGoneObjectIsStaleUnlessTheManifestStillListsIt()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var worker = world.Worker();
        var first = await world.DrainOpenAsync(worker, ledger, Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a1")], Token);
        var second = await world.DrainOpenAsync(worker, ledger, Token);
        await world.CloseCompleteAndDropAsync(ledger, Token);
        var store = world.ArchiveStore();
        using (await ReadAsync(store, world, "a"))
        {
        }

        var cached = store.CachedTailBytes;
        _ = Assert.IsType<ManifestCommitOutcome.Applied>((await world.Committer.CommitAsync(new ManifestChange.Compaction(first.Sections, []), Token)).Value);
        await world.Objects.DeleteAsync(first.Sections[0].Object, Token);

        Assert.IsType<ArchiveStoreRead.Stale>((await store.ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token)).Value);

        // The view which no longer lists the removed object forgets its tail.
        Assert.InRange(store.CachedTailBytes, 1, cached - 1);
        using (var read = await ReadAsync(store, world, "a"))
        {
            Assert.Equal("a1", Content(read));
        }

        await world.Objects.DeleteAsync(second.Sections[0].Object, Token);
        var corrupt = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await world.ArchiveStore().ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token));
        Assert.Contains("is absent", corrupt.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The store ID is created once and read back the same by every store over the object store; content which is not an
    /// ID is corrupt.
    /// </summary>
    [Fact]
    public async Task TheStoreIdIsCreatedOnceAndReadBack()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);

        var created = await world.ArchiveStore().CheckedIdAsync(Parameters, Token);

        Assert.Equal(created, await world.ArchiveStore().CheckedIdAsync(Parameters, Token));
        using (var stored = (await world.Objects.ReadVersionedAsync(ObjectName.Store, Token))!.Value.Content)
        {
            Assert.Equal(Invariant($"id={created}\nlayout=lsm\ncaller.format=json"), Encoding.UTF8.GetString(stored.ToArray()));
        }

        await world.Objects.DeleteAsync(ObjectName.Store, Token);
        using var content = ArcBuffer.Wrap("not a store record"u8.ToArray());
        _ = await world.Objects.CreateAsync(ObjectName.Store, content, null, Token);
        _ = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await world.ArchiveStore().CheckedIdAsync(Parameters, Token));
    }

    /// <summary>
    /// A create of the store's record which lands and answers that the record exists, as a client which retried its own
    /// landed attempt does (<see cref="FaultInjectingObjectStore"/>), answers the call's own record, the stored bytes.
    /// </summary>
    [Fact]
    public async Task ARecordCreateWhichLandedButAnsweredExistsAnswersItsOwnRecord()
    {
        var objects = new ObjectStore.Memory.InMemoryObjectStore();
        var faulty = new FaultInjectingObjectStore(objects, seed: 1, faultRate: 0);
        faulty.RetryNextCreate();
        var store = new LsmArchiveStore(faulty, new ManifestView(objects), new TailCache(objects, new ArchiveOptions()), new ArchiveOptions(), TimeProvider.System);

        var record = await store.StoreRecordAsync(Parameters, Token);

        Assert.Equal(["Create(store): fault Retried"], faulty.TakeEvents());
        using var stored = (await objects.ReadVersionedAsync(ObjectName.Store, Token))!.Value.Content;
        Assert.Equal(stored.ToArray(), record.Encode());
        record.CheckParametersMatch(Parameters);
    }

    /// <summary>
    /// A map value which lists open a ledger the store's view shows complete, read before the ledger closed and drained, is
    /// stale, not corruption: one reader's newer map advances the shared view, and another's older map then reads
    /// <c>Stale</c>, whichever order their reads run in, and reads again with a newer map.
    /// </summary>
    [Fact]
    public async Task AMapWhichPredatesALedgersCompletionReadsStale()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var before = world.Ledgers.Map.Current;
        await world.Ledgers.ReplaceAsync(ledger, Token);
        _ = await world.CompleteAsync(world.Worker(), ledger, Token);
        var dropper = new LedgerDropper(world.Ledgers.Map, world.Manifest, NullLogger<LedgerDropper>.Instance);
        Assert.Equal([ledger], await dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token));
        var after = world.Ledgers.Map.Current;
        var store = world.ArchiveStore();

        using (var read = Assert.IsType<ArchiveStoreRead.Read>((await store.ReadAsync("a"u8.ToArray(), after, Token)).Value))
        {
            Assert.Equal("a0", Content(read));
        }

        Assert.IsType<ArchiveStoreRead.Stale>((await store.ReadAsync("a"u8.ToArray(), before, Token)).Value);

        var racing = world.ArchiveStore();
        var answers = await Task.WhenAll(
            racing.ReadAsync("a"u8.ToArray(), before, Token).AsTask(),
            racing.ReadAsync("a"u8.ToArray(), after, Token).AsTask());
        try
        {
            // Whichever read loads the view first, it shows the ledger complete: the map which lists it predates it.
            Assert.IsType<ArchiveStoreRead.Stale>(answers[0].Value);
            Assert.Equal("a0", Content(Assert.IsType<ArchiveStoreRead.Read>(answers[1].Value)));
        }
        finally
        {
            foreach (var answer in answers)
            {
                (answer.Value as ArchiveStoreRead.Read)?.Dispose();
            }
        }
    }

    /// <summary>
    /// A ledger the map still lists holds none of the prefix, though the view shows it complete: the reader scans its rows
    /// from slot 0, so a store which holds them answers <c>Absent</c>, and <c>Holds</c> holds none of its writes, but those of
    /// a ledger the map dropped (archive-lsm.md §2.8).
    /// </summary>
    [Fact]
    public async Task ARowOfAListedLedgerIsNotInThePrefixAndHoldsCoversOnlyDroppedLedgers()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        await world.Ledgers.ReplaceAsync(ledger, Token);
        _ = await world.CompleteAsync(world.Worker(), ledger, Token);
        var open = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(open, [LiveRow.Of("a", RowKind.Append, "a1")], Token);
        _ = await world.Ledgers.ConfirmOpenAsync(open, Token);
        var store = world.ArchiveStore();

        Assert.IsType<ArchiveStoreRead.Absent>((await store.ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token)).Value);

        var dropper = new LedgerDropper(world.Ledgers.Map, world.Manifest, NullLogger<LedgerDropper>.Instance);
        Assert.Equal([ledger], await dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token));
        using var read = await ReadAsync(store, world, "a");
        var dropped = new ArchiveWrite(new LedgerSegmentId(ledger, Slot.Zero), default, [ArchiveCommand.Append(2)]);
        var listed = new ArchiveWrite(new LedgerSegmentId(open, Slot.Zero), default, [ArchiveCommand.Append(2)]);
        Assert.Equal((true, true, false), (store.Holds(read.Prefix, dropped, 1), store.Holds(read.Prefix, listed, 0), store.Holds(read.Prefix, listed, 1)));
    }

    /// <summary>
    /// The tail cache: a tail is fetched once across reads; a tail found corrupt stays corrupt without another fetch; a
    /// cache with no room fetches again for each read.
    /// </summary>
    [Fact]
    public async Task ATailIsFetchedOnceAndACorruptOneStaysCorrupt()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var change = await world.DrainOpenAsync(world.Worker(), ledger, Token);
        var section = change.Sections[0];
        int tailReads() => world.RangedReads(section.Object);

        var cached = world.ArchiveStore();
        var before = tailReads();
        for (var read = 0; read < 3; read++)
        {
            await ReadAnyAsync(cached, world, "a");
        }

        // Each read reads the key's block; only the first reads the tail.
        Assert.Equal(before + 4, tailReads());

        var uncached = world.ArchiveStore(options: new ArchiveOptions { TailCacheBytes = 0 });
        before = tailReads();
        for (var read = 0; read < 2; read++)
        {
            await ReadAnyAsync(uncached, world, "a");
        }

        Assert.Equal(before + 4, tailReads());

        var length = (await world.Objects.ListAsync(section.Object.Value, cancellationToken: Token).ToListAsync(Token)).Single().Length;
        byte[] bytes;
        using (var content = (await world.Objects.ReadAsync(section.Object, 0, (int)length, Token))!.Value)
        {
            bytes = content.ToArray();
        }

        bytes[^5] ^= 0xff;
        await world.Objects.DeleteAsync(section.Object, Token);
        using (var corrupted = ArcBuffer.Wrap(bytes))
        {
            _ = await world.Objects.CreateAsync(section.Object, corrupted, null, Token);
        }

        var corrupt = world.ArchiveStore();
        _ = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await corrupt.ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token));
        before = tailReads();
        _ = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await corrupt.ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token));
        Assert.Equal(before, tailReads());
    }

    /// <summary>
    /// A store over the committer's manifest view keeps its view while it covers the map, so a compaction which removes an
    /// object it names reads <c>Stale</c>; once the adoption interval passes, the store adopts the view's newer value and
    /// reads without meeting the removed object.
    /// </summary>
    [Fact]
    public async Task TheViewAdoptsANewerCurrentValueOnlyOncePerInterval()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var worker = world.Worker();
        var first = await world.DrainOpenAsync(worker, ledger, Token);
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a1")], Token);
        _ = await world.DrainOpenAsync(worker, ledger, Token);
        var (early, late) = (new FakeTimeProvider(), new FakeTimeProvider());
        var (lagging, adopting) = (world.ArchiveStore(world.Manifest, timeProvider: early), world.ArchiveStore(world.Manifest, timeProvider: late));
        foreach (var store in new[] { lagging, adopting })
        {
            await ReadAnyAsync(store, world, "a");
        }

        _ = Assert.IsType<ManifestCommitOutcome.Applied>((await world.Committer.CommitAsync(new ManifestChange.Compaction(first.Sections, []), Token)).Value);
        await world.Objects.DeleteAsync(first.Sections[0].Object, Token);
        var reads = world.ManifestReads;
        late.Advance(new ArchiveOptions().ViewAdoptionInterval);

        // The open ledger's rows are not the prefix's, so the read answers Absent once it met no removed object.
        Assert.IsType<ArchiveStoreRead.Absent>((await adopting.ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token)).Value);
        Assert.Equal(reads, world.ManifestReads);
        Assert.IsType<ArchiveStoreRead.Stale>((await lagging.ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token)).Value);
    }

    /// <summary>
    /// A store over the committer's manifest view adopts its newer value for a map which dropped a ledger, with no manifest
    /// read; a map epoch it found covered is not checked again until the view changes.
    /// </summary>
    [Fact]
    public async Task AStoreAdoptsTheViewsNewerValueWithoutAReadAndChecksCoverageOncePerMapEpoch()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        _ = await world.DrainOpenAsync(world.Worker(), ledger, Token);
        var store = world.ArchiveStore(world.Manifest);
        await ReadAnyAsync(store, world, "a");
        var checks = store.CoverageChecks;
        await ReadAnyAsync(store, world, "a");

        Assert.Equal(checks, store.CoverageChecks);

        await world.Ledgers.ReplaceAsync(ledger, Token);
        _ = await world.CompleteAsync(world.Worker(), ledger, Token);
        var dropper = new LedgerDropper(world.Ledgers.Map, world.Manifest, NullLogger<LedgerDropper>.Instance);
        Assert.Equal([ledger], await dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token));
        var reads = world.ManifestReads;

        using (var read = await ReadAsync(store, world, "a"))
        {
            Assert.Equal("a0", Content(read));
        }

        Assert.Equal(reads, world.ManifestReads);
    }

    /// <summary>
    /// A ledger the map dropped which a manifest read after the map shows incomplete is corruption (LSM-I3): the map's drop
    /// lacked its evidence.
    /// </summary>
    [Fact]
    public async Task ADroppedLedgerWhichAManifestReadAfterTheMapShowsIncompleteIsCorrupt()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        await world.Ledgers.ReplaceAsync(ledger, Token);
        var dropped = Assert.IsType<LedgerMapCommitOutcome.Applied>((await world.Ledgers.CommitAsync(new LedgerMapChange.Drop(ledger), Token)).Value);
        _ = await world.Ledgers.Map.RefreshAsync(dropped.Epoch, Token);

        var corrupt = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await world.ArchiveStore().ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token));
        Assert.Contains("shows incomplete a ledger the map dropped", corrupt.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Concurrent first reads of an object share one fetch of its tail; a fetch which fails fails the reads which shared it
    /// and leaves the cache, so the next read fetches again.
    /// </summary>
    [Fact]
    public async Task ConcurrentReadsShareOneTailFetchAndAFailedFetchIsRetried()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var change = await world.DrainOpenAsync(world.Worker(), ledger, Token);
        var name = change.Sections[0].Object;
        var store = world.ArchiveStore(world.Manifest);
        var before = world.RangedReads(name);

        world.Store.HoldRangedReads();
        var reads = new[] { store.ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token).AsTask(), store.ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token).AsTask() };
        Assert.Equal(before + 1, world.RangedReads(name));
        world.Store.ReleaseRangedReads();
        foreach (var answer in await Task.WhenAll(reads))
        {
            Assert.IsType<ArchiveStoreRead.Absent>(answer.Value);
        }

        // One tail, then each read's block.
        Assert.Equal(before + 3, world.RangedReads(name));

        var failing = world.ArchiveStore(world.Manifest);
        world.Store.FailRangedReads(1);
        _ = await Assert.ThrowsAsync<IOException>(async () => await failing.ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token));
        Assert.IsType<ArchiveStoreRead.Absent>((await failing.ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token)).Value);
    }

    /// <summary>
    /// Two listed sections which hold a key's row under one tag are corruption: here a compaction listed a copy of a
    /// drain output beside it.
    /// </summary>
    [Fact]
    public async Task ARowUnderOneTagInTwoListedSectionsIsCorrupt()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        var worker = world.Worker();
        var first = (await world.DrainOpenAsync(worker, ledger, Token)).Sections[0];
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("b", RowKind.Append, "b0")], Token);
        var second = (await world.DrainOpenAsync(worker, ledger, Token)).Sections[0];
        var length = (await world.Objects.ListAsync(first.Object.Value, cancellationToken: Token).ToListAsync(Token)).Single().Length;
        var copy = RunObjectNames.Drain(ledger, Slot.Zero, RunObjectNames.NewId());
        using (var content = (await world.Objects.ReadAsync(first.Object, 0, (int)length, Token))!.Value)
        {
            _ = await world.Objects.CreateAsync(copy, content, null, Token);
        }

        var duplicate = new SectionRef(copy, first.Offset, first.Length, first.Partition, first.MinTag, first.MaxTag, first.Tail);
        _ = Assert.IsType<ManifestCommitOutcome.Applied>((await world.Committer.CommitAsync(new ManifestChange.Compaction([second], [duplicate]), Token)).Value);

        var corrupt = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await world.ArchiveStore().ReadAsync("a"u8.ToArray(), world.Ledgers.Map.Current, Token));
        Assert.Contains("Two sections hold a row", corrupt.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A store whose read of the store ID found none, and whose create then finds another's, answers the other's.
    /// </summary>
    [Fact]
    public async Task AStoreIdCreateWhichMeetsAnotherReadsTheWinners()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var winner = ArchiveStoreId.TryParse("0123456789abcdef0123456789abcdef")!.Value;
        world.Store.HoldReads();
        var pending = world.ArchiveStore().CheckedIdAsync(Parameters, Token).AsTask();
        using (var content = ArcBuffer.Wrap(ArchiveStoreRecord.Create(winner, LsmArchiveStore.Layout, new Dictionary<string, string>(), Parameters).Encode()))
        {
            _ = Assert.IsType<ObjectCreateOutcome.Created>((await world.Objects.CreateAsync(ObjectName.Store, content, null, Token)).Value);
        }

        world.Store.ReleaseReads();

        Assert.Equal(winner, await pending);
        Assert.Single(world.Store.Calls, static call => call == "Create(store)");
    }

    /// <summary>
    /// Once every section's tail is cached, absent keys complete with zero object reads, single-candidate keys read only
    /// their matching section's data block, and multi-candidate keys merge rows across sections without refetching tails.
    /// </summary>
    [Fact]
    public async Task CachedTailsServeAbsentSingleCandidateAndMultiCandidateReadsWithZeroTailRefetches()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var worker = world.Worker();

        var l1 = world.Ledgers.OpenContaining("a"u8.ToArray()).Id;
        _ = await world.Ledgers.ProposeAsync(l1, [LiveRow.Of("a", RowKind.Append, "a0"), LiveRow.Of("b", RowKind.Append, "b0-")], Token);
        await world.Ledgers.ReplaceAsync(l1, Token);
        var s1 = (await world.CompleteAsync(worker, l1, Token)).Sections[0];

        var l2 = world.Ledgers.OpenContaining("b"u8.ToArray()).Id;
        _ = await world.Ledgers.ProposeAsync(l2, [LiveRow.Of("b", RowKind.Append, "b1")], Token);
        await world.Ledgers.ReplaceAsync(l2, Token);
        var s2 = (await world.CompleteAsync(worker, l2, Token)).Sections[0];

        var dropper = new LedgerDropper(world.Ledgers.Map, world.Manifest, NullLogger<LedgerDropper>.Instance);
        _ = await dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token);

        var store = world.ArchiveStore(world.Manifest);
        await store.PreloadAsync([KeyRange.Root]);

        // 0 candidates (absent key): synchronous cached tail check, zero object reads.
        var s1BeforeAbsent = world.RangedReads(s1.Object);
        var s2BeforeAbsent = world.RangedReads(s2.Object);
        Assert.IsType<ArchiveStoreRead.Absent>((await store.ReadAsync("zzz-absent"u8.ToArray(), world.Ledgers.Map.Current, Token)).Value);
        Assert.Equal(s1BeforeAbsent, world.RangedReads(s1.Object));
        Assert.Equal(s2BeforeAbsent, world.RangedReads(s2.Object));

        // 1 candidate ("a"): reads only s1's data block, zero tail refetches and zero reads of s2.
        var s1BeforeSingle = world.RangedReads(s1.Object);
        var s2BeforeSingle = world.RangedReads(s2.Object);
        using (var readA = await ReadAsync(store, world, "a"))
        {
            Assert.Equal("a0", Content(readA));
        }

        Assert.Equal(s1BeforeSingle + 1, world.RangedReads(s1.Object));
        Assert.Equal(s2BeforeSingle, world.RangedReads(s2.Object));

        // 2 candidates ("b"): reads s1 and s2 data blocks and merges in tag order without refetching tails.
        var s1BeforeMulti = world.RangedReads(s1.Object);
        var s2BeforeMulti = world.RangedReads(s2.Object);
        using (var readB = await ReadAsync(store, world, "b"))
        {
            Assert.Equal("b0-b1", Content(readB));
        }

        Assert.Equal(s1BeforeMulti + 1, world.RangedReads(s1.Object));
        Assert.Equal(s2BeforeMulti + 1, world.RangedReads(s2.Object));
    }

    private static async Task<ArchiveStoreRead.Read> ReadAsync(LsmArchiveStore store, ArchiveWorld world, string key) =>
        Assert.IsType<ArchiveStoreRead.Read>((await store.ReadAsync(Encoding.UTF8.GetBytes(key), world.Ledgers.Map.Current, Token)).Value);

    /// <summary>Reads <paramref name="key"/> and releases whatever the store answers.</summary>
    private static async Task ReadAnyAsync(LsmArchiveStore store, ArchiveWorld world, string key) =>
        ((await store.ReadAsync(Encoding.UTF8.GetBytes(key), world.Ledgers.Map.Current, Token)).Value as ArchiveStoreRead.Read)?.Dispose();

    private static string Content(ArchiveStoreRead.Read read) => Encoding.UTF8.GetString(read.Prefix.Content.ToArray());

    private static string Invariant(FormattableString text) => text.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
