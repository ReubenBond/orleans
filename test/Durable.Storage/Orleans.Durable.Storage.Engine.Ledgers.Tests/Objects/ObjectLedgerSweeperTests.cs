using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Objects;

/// <summary>
/// The sweeper (object-ledgers.md §2.3, §8.5): it reclaims only the ledgers the map revokes, creates each one's drop
/// marker before it deletes any object (OL-I4), keeps the markers and every SEAL (OL-I8), and a later sweep deletes a
/// batch a delayed write re-created (OL-I9); and the store's record (§2.2, OL-I1).
/// </summary>
public sealed class ObjectLedgerSweeperTests : IDisposable
{
    private static readonly LedgerId Revoked = new(3);
    private static readonly LedgerId Listed = new(4);
    private static readonly LedgerId Next = new(5);
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly FakeTimeProvider _clock = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task ASweepReclaimsTheRevokedLedgersBatchesAndKeepsItsMarkersAndFences()
    {
        var store = new RecordingStore();
        var sweeper = new ObjectLedgerSweeper(ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions(), _clock));
        foreach (var id in new[] { Revoked, Listed })
        {
            await CreateAsync(store, ObjectLedgerNames.Slot(id, Slot.Zero), [1]);
            await CreateAsync(store, ObjectLedgerNames.Slot(id, new Slot(1)), [2]);
            await CreateAsync(store, ObjectLedgerNames.Slot(id, new Slot(2)), []);
            await CreateAsync(store, ObjectLedgerNames.Sealed(id), []);
        }

        var before = await store.NamesAsync(ObjectLedgerNames.Ledger(Listed));
        store.Calls.Clear();
        Assert.Equal(2, await sweeper.SweepAsync(static id => id == Revoked, Next, Token));

        Assert.Equal(
            [ObjectLedgerNames.Dropped(Revoked).Value, ObjectLedgerNames.Slot(Revoked, new Slot(2)).Value, ObjectLedgerNames.Sealed(Revoked).Value],
            await store.NamesAsync(ObjectLedgerNames.Ledger(Revoked)));
        Assert.Equal(before, await store.NamesAsync(ObjectLedgerNames.Ledger(Listed)));
        Assert.Equal("create " + ObjectLedgerNames.Dropped(Revoked).Value, store.Calls[0]);
        Assert.All(store.Calls.Skip(1), static call => Assert.StartsWith("delete ", call, StringComparison.Ordinal));

        // A delayed write re-creates a batch after the sweep; the next sweep deletes it and creates nothing.
        Assert.Equal(0, await sweeper.SweepAsync(static id => id == Revoked, Next, Token));
        await CreateAsync(store, ObjectLedgerNames.Slot(Revoked, new Slot(1)), [2]);
        store.Calls.Clear();
        Assert.Equal(1, await sweeper.SweepAsync(static id => id == Revoked, Next, Token));
        Assert.Equal(["delete " + ObjectLedgerNames.Slot(Revoked, new Slot(1)).Value], store.Calls);
    }

    /// <summary>LL-I9: a revoked ledger which holds no batch still gets its drop marker, so its reads answer the drop.</summary>
    [Fact]
    public async Task ARevokedLedgerWithoutBatchesIsMarkedDropped()
    {
        var store = new RecordingStore();
        var sweeper = new ObjectLedgerSweeper(ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions(), _clock));
        await CreateAsync(store, ObjectLedgerNames.Sealed(Revoked), []);
        await CreateAsync(store, ObjectLedgerNames.Slot(Revoked, Slot.Zero), []);

        Assert.Equal(0, await sweeper.SweepAsync(static id => id == Revoked, Next, Token));
        Assert.NotNull(await store.Inner.GetPropertiesAsync(ObjectLedgerNames.Dropped(Revoked), Token));
    }

    /// <summary>
    /// The public sweep reclaims the ledgers a map value revokes, below its next ledger ID and not listed, and leaves its
    /// open and closed ledgers, and any ledger at or above its next ID, untouched.
    /// </summary>
    [Fact]
    public async Task ASweepOfAMapValueReclaimsOnlyTheLedgersItRevokes()
    {
        var store = new RecordingStore();
        var sweeper = new ObjectLedgerSweeper(ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions(), _clock));
        var (open, closed, revoked, future) = (new LedgerId(1), new LedgerId(2), new LedgerId(3), new LedgerId(5));
        foreach (var id in new[] { open, closed, revoked, future })
        {
            await CreateAsync(store, ObjectLedgerNames.Slot(id, Slot.Zero), [1]);
        }

        var claim = new PrimaryClaim(NodeId.New(), SequencerInstanceId.New());
        var map = new LedgerMapValue(
            1,
            new LedgerId(4),
            0,
            [
                new LedgerMetadata(open, KeyRange.Root, LedgerLocation.ObjectStore.Instance, 2, claim, LedgerStatus.Open.Instance),
                new LedgerMetadata(closed, KeyRange.Root, LedgerLocation.ObjectStore.Instance, 2, claim, new LedgerStatus.Closed(new Slot(1))),
            ]);

        Assert.Equal(1, await sweeper.SweepAsync(map, Token));
        Assert.Equal([ObjectLedgerNames.Dropped(revoked).Value], await store.NamesAsync(ObjectLedgerNames.Ledger(revoked)));
        foreach (var id in new[] { open, closed, future })
        {
            Assert.Equal([ObjectLedgerNames.Slot(id, Slot.Zero).Value], await store.NamesAsync(ObjectLedgerNames.Ledger(id)));
        }
    }

    /// <summary>
    /// A sweep passes a live ledger's objects a page at most, then lists again after the ledger, and deletes a revoked
    /// ledger's batches more than a flight at a time.
    /// </summary>
    [Fact]
    public async Task ASweepPassesALiveLedgerAndDeletesInFlights()
    {
        var store = new RecordingStore();
        var sweeper = new ObjectLedgerSweeper(ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions(), _clock));
        var batches = ObjectLedgerSweeper.DeletesInFlight * 2 + 1;
        for (var slot = 0; slot < ObjectLedgerSweeper.LiveObjectsPassed * 2; slot++)
        {
            await CreateAsync(store, ObjectLedgerNames.Slot(Revoked, new Slot(slot)), [1]);
        }

        for (var slot = 0; slot < batches; slot++)
        {
            await CreateAsync(store, ObjectLedgerNames.Slot(Listed, new Slot(slot)), [1]);
        }

        store.Calls.Clear();
        Assert.Equal(batches, await sweeper.SweepAsync(static id => id == Listed, Next, Token));
        Assert.Equal(2, store.Listings);
        Assert.Equal(ObjectLedgerSweeper.LiveObjectsPassed * 2, (await store.NamesAsync(ObjectLedgerNames.Ledger(Revoked))).Count);
        Assert.Equal([ObjectLedgerNames.Dropped(Listed).Value], await store.NamesAsync(ObjectLedgerNames.Ledger(Listed)));
    }

    /// <summary>
    /// OL-I1: the first call creates the store's record and every later one, in any process, answers it; a read-only read
    /// creates nothing; another layout, a corrupt or oversized record, a record which fails its CRC-32C, and one which
    /// vanished after it existed are refused; and an append-layout store creates and checks its own record.
    /// </summary>
    [Fact]
    public async Task TheStoresRecordIsCreatedOnceAndChecked()
    {
        var store = new RecordingStore();
        Assert.Null(await ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions(), _clock).ReadStoreRecordAsync(Token));
        Assert.Empty(await store.NamesAsync(string.Empty));
        var first = await ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions(), _clock).StoreRecordAsync(Token);
        var again = await ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions(), _clock).StoreRecordAsync(Token);
        Assert.Equal(first, again);
        Assert.Equal(first, await ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions(), _clock).ReadStoreRecordAsync(Token));
        Assert.Equal(ObjectLedgerLayout.PerSlot, first.Layout);

        var appendOverPerSlot = ObjectLedgerStore.OverAppendable(store, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        _ = await Assert.ThrowsAsync<ObjectLedgerStoreMismatchException>(async () => await appendOverPerSlot.StoreRecordAsync(Token));
        _ = await Assert.ThrowsAsync<ObjectLedgerStoreMismatchException>(async () => await appendOverPerSlot.ReadStoreRecordAsync(Token));
        _ = Assert.Throws<ArgumentException>(() => ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock));

        var appendBacking = new RecordingStore();
        var append = ObjectLedgerStore.OverAppendable(appendBacking, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        var appendRecord = await append.StoreRecordAsync(Token);
        Assert.Equal(ObjectLedgerLayout.Append, appendRecord.Layout);
        Assert.Equal(appendRecord, await append.ReadStoreRecordAsync(Token));
        var perSlotOverAppend = ObjectLedgerStore.Over(appendBacking, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.PerSlot }, _clock);
        _ = await Assert.ThrowsAsync<ObjectLedgerStoreMismatchException>(async () => await perSlotOverAppend.StoreRecordAsync(Token));

        foreach (var content in new[] { "id=0\nlayout=per-slot"u8.ToArray(), new byte[5000] })
        {
            var corrupt = new RecordingStore();
            await CreateAsync(corrupt, ObjectLedgerNames.Store, content);
            _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await ObjectLedgerStore.Over(corrupt, new ObjectLedgerStoreOptions(), _clock).StoreRecordAsync(Token));
        }

        var empty = new RecordingStore();
        await CreateAsync(empty, ObjectLedgerNames.Store, []);
        _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await ObjectLedgerStore.Over(empty, new ObjectLedgerStoreOptions(), _clock).ReadStoreRecordAsync(Token));

        var flipped = new RecordingStore { FlipsChecksums = true };
        _ = await ObjectLedgerStore.Over(flipped.Inner, new ObjectLedgerStoreOptions(), _clock).StoreRecordAsync(Token);
        _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await ObjectLedgerStore.Over(flipped, new ObjectLedgerStoreOptions(), _clock).ReadStoreRecordAsync(Token));
    }

    /// <summary>
    /// object-ledgers.md §8.5 (OL-I4, OL-I8, OL-I9): in the append layout, a sweep creates the drop marker first,
    /// replaces the newest extent with its 17-byte <c>CLOE</c> tombstone, deletes older extents, leaves live ledgers
    /// untouched, and reclaims a delayed extent on a later sweep without re-tombstoning the already-tombstoned newest extent.
    /// </summary>
    [Fact]
    public async Task AnAppendLayoutSweepTombstonesTheNewestExtentAndDeletesOlderExtents()
    {
        var store = new RecordingStore();
        var options = new ObjectLedgerStoreOptions
        {
            Layout = ObjectLedgerLayout.Append,
            ExtentTargetBytes = 1,
            ExtentMaxAppends = 1,
        };
        var ledgers = ObjectLedgerStore.OverAppendable(store, options, _clock);
        var sweeper = new ObjectLedgerSweeper(ledgers);

        await using (var revoked = ledgers.Open(Revoked, 2))
        await using (var listed = ledgers.Open(Listed, 2))
        {
            using var b0 = ArcBuffer.Wrap(new byte[] { 1, 2, 3 });
            using var b1 = ArcBuffer.Wrap(new byte[] { 4, 5, 6 });
            using var b2 = ArcBuffer.Wrap(new byte[] { 7, 8, 9 });
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await revoked.WriteAsync(Slot.Zero, b0, Slot.Zero, Token));
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await revoked.WriteAsync(new Slot(1), b1, new Slot(1), Token));
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await revoked.WriteAsync(new Slot(2), b2, new Slot(2), Token));
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await listed.WriteAsync(Slot.Zero, b0, Slot.Zero, Token));
        }

        var listedBefore = await store.NamesAsync(ObjectLedgerNames.Ledger(Listed));
        var newestExtent = ObjectLedgerNames.Extent(Revoked, new Slot(2));
        var olderExtents = new[] { ObjectLedgerNames.Extent(Revoked, Slot.Zero), ObjectLedgerNames.Extent(Revoked, new Slot(1)) };
        store.Calls.Clear();

        Assert.Equal(2, await sweeper.SweepAsync(static id => id == Revoked, Next, Token));
        Assert.Equal("create " + ObjectLedgerNames.Dropped(Revoked).Value, store.Calls[0]);
        Assert.Contains("tombstone " + newestExtent.Value, store.Calls);
        foreach (var older in olderExtents)
        {
            Assert.Contains("delete " + older.Value, store.Calls);
        }

        Assert.Equal(
            [ObjectLedgerNames.Dropped(Revoked).Value, newestExtent.Value],
            await store.NamesAsync(ObjectLedgerNames.Ledger(Revoked)));
        Assert.Equal(listedBefore, await store.NamesAsync(ObjectLedgerNames.Ledger(Listed)));

        var tombProps = await store.Inner.GetPropertiesAsync(newestExtent, Token);
        Assert.NotNull(tombProps);
        Assert.Equal(ObjectLedgerExtentFormat.TombstoneLength, tombProps.Length);
        using (var tombBytes = (await store.Inner.ReadAsync(newestExtent, 0, ObjectLedgerExtentFormat.TombstoneLength, Token))!.Value)
        {
            var decoded = ObjectLedgerExtentFormat.DecodeTombstone(tombBytes.ToArray());
            Assert.Equal(new Slot(2), Assert.IsType<Slot>(decoded.Value));
        }

        // A second sweep is a no-op; a delayed extent arriving later is reclaimed on the next sweep.
        store.Calls.Clear();
        Assert.Equal(0, await sweeper.SweepAsync(static id => id == Revoked, Next, Token));
        Assert.Empty(store.Calls);

        var delayedOlder = ObjectLedgerNames.Extent(Revoked, new Slot(1));
        _ = await store.Inner.CreateAppendableAsync(delayedOlder, Token);
        store.Calls.Clear();
        Assert.Equal(1, await sweeper.SweepAsync(static id => id == Revoked, Next, Token));
        Assert.Equal(["delete " + delayedOlder.Value], store.Calls);
    }

    /// <summary>
    /// object-ledgers.md §8.5: an append-layout revoked ledger with only a <c>sealed</c> marker and no extents yet still
    /// receives its <c>dropped</c> marker and an extent-0 tombstone so delayed creates or appends meet a sealed tombstone.
    /// </summary>
    [Fact]
    public async Task AnAppendLayoutRevokedEmptyLedgerGetsDroppedAndAnExtentZeroTombstone()
    {
        var store = new RecordingStore();
        var ledgers = ObjectLedgerStore.OverAppendable(store, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        var sweeper = new ObjectLedgerSweeper(ledgers);

        // Revoked ledger with only the sealed marker and no extents at all.
        await CreateAsync(store, ObjectLedgerNames.Sealed(Revoked), []);

        store.Calls.Clear();
        Assert.Equal(0, await sweeper.SweepAsync(static id => id == Revoked, Next, Token));
        Assert.Equal("create " + ObjectLedgerNames.Dropped(Revoked).Value, store.Calls[0]);
        Assert.Contains("tombstone " + ObjectLedgerNames.Extent(Revoked, Slot.Zero).Value, store.Calls);
        var tombProps = await store.Inner.GetPropertiesAsync(ObjectLedgerNames.Extent(Revoked, Slot.Zero), Token);
        Assert.NotNull(tombProps);
        Assert.Equal(ObjectLedgerExtentFormat.TombstoneLength, tombProps.Length);
        Assert.NotNull(tombProps.Crc32c);
    }

    /// <summary>
    /// object-ledgers.md §8.5: a 17-byte appendable extent is not mistaken for an already-tombstoned block blob (whose
    /// <see cref="ObjectProperties.Crc32c"/> is non-null); the sweeper still replaces it with a block-blob tombstone.
    /// </summary>
    [Fact]
    public async Task AnAppendLayoutSeventeenByteAppendableExtentIsStillReplacedWithATombstone()
    {
        var store = new RecordingStore();
        var ledgers = ObjectLedgerStore.OverAppendable(store, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        var sweeper = new ObjectLedgerSweeper(ledgers);

        var ext0 = ObjectLedgerNames.Extent(Revoked, Slot.Zero);
        _ = await store.Inner.CreateAppendableAsync(ext0, Token);
        using (var seventeenBytes = ArcBuffer.Wrap(new byte[ObjectLedgerExtentFormat.TombstoneLength]))
        {
            Assert.IsType<ObjectAppendOutcome.Appended>((await store.Inner.AppendAsync(ext0, 0, seventeenBytes, crc32c: null, Token)).Value);
        }

        store.Calls.Clear();
        Assert.Equal(0, await sweeper.SweepAsync(static id => id == Revoked, Next, Token));
        Assert.Contains("tombstone " + ext0.Value, store.Calls);
        var props = await store.Inner.GetPropertiesAsync(ext0, Token);
        Assert.NotNull(props);
        Assert.Equal(ObjectLedgerExtentFormat.TombstoneLength, props.Length);
        Assert.NotNull(props.Crc32c);
    }

    /// <summary>
    /// conventions.md §14: a record whose content changed after its CRC-32C was computed is a defect, reported once.
    /// </summary>
    [Fact]
    public async Task AChangedRecordIsReportedAsADefect()
    {
        var store = new ChangingRecordStore();
        var since = FatalErrors.ReportedCount;

        var changed = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions(), _clock).StoreRecordAsync(Token));

        var report = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal(FatalErrorSite.Defect, report.Site);
        Assert.Same(changed, report.Failure.Exception);
    }

    /// <summary>
    /// OL-I1: a create which meets another process's record answers that record, and one whose record then reads as
    /// absent is refused rather than given a new ID.
    /// </summary>
    [Fact]
    public async Task ARecordCreateWhichMeetsAnotherProcesssAnswersIt()
    {
        var store = new RacingRecordStore();
        var won = await ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions(), _clock).StoreRecordAsync(Token);
        Assert.Equal(store.Winner, won);

        var vanishing = new RacingRecordStore { DeletesTheWinner = true };
        _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await ObjectLedgerStore.Over(vanishing, new ObjectLedgerStoreOptions(), _clock).StoreRecordAsync(Token));
    }

    private static async Task CreateAsync(ForwardingObjectStore store, ObjectName name, byte[] content)
    {
        using var buffer = ArcBuffer.Wrap(content);
        _ = await store.Inner.CreateAsync(name, content.Length == 0 ? default : buffer, crc32c: null, Token);
    }

    /// <summary>Records each create and delete, in order, and counts listings; may report every CRC-32C flipped.</summary>
    private sealed class RecordingStore : ForwardingObjectStore
    {
        public List<string> Calls { get; } = [];

        public int Listings { get; private set; }

        public bool FlipsChecksums { get; init; }

        public override ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add("create " + name.Value);
            }

            return base.CreateAsync(name, content, crc32c, cancellationToken);
        }

        public override ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add("delete " + name.Value);
            }

            return base.DeleteAsync(name, cancellationToken);
        }

        public override ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add("tombstone " + name.Value);
            }

            return base.ReplaceWithTombstoneAsync(name, content, crc32c, cancellationToken);
        }

        public override IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
        {
            Listings++;
            return base.ListAsync(prefix, startAfter, cancellationToken);
        }

        public override async ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            await base.GetPropertiesAsync(name, cancellationToken) is { } properties && FlipsChecksums && properties.Crc32c is { } crc
                ? properties with { Crc32c = crc ^ 1 }
                : await base.GetPropertiesAsync(name, cancellationToken);
    }

    /// <summary>Refuses the record's create as content changed after verification.</summary>
    private sealed class ChangingRecordStore : ForwardingObjectStore
    {
        public override ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ObjectCreateOutcome>(ObjectContentChangedException.For(name));
    }

    /// <summary>
    /// Lets another process create the store's record between this one's read and create, and may delete it before this
    /// one reads it again.
    /// </summary>
    private sealed class RacingRecordStore : ForwardingObjectStore
    {
        public ObjectLedgerStoreRecord Winner { get; } = ObjectLedgerStoreRecord.NewRandom(ObjectLedgerLayout.PerSlot);

        public bool DeletesTheWinner { get; init; }

        public override async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            if (name == ObjectLedgerNames.Store)
            {
                using var winner = ArcBuffer.Wrap(Winner.Encode());
                _ = await Inner.CreateAsync(name, winner, crc32c: null, cancellationToken);
                var outcome = await base.CreateAsync(name, content, crc32c, cancellationToken);
                if (DeletesTheWinner)
                {
                    await Inner.DeleteAsync(name, cancellationToken);
                }

                return outcome;
            }

            return await base.CreateAsync(name, content, crc32c, cancellationToken);
        }
    }
}
