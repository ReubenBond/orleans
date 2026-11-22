using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Objects;

/// <summary>
/// Focused tests for the Azure append layout (<c>CLOE</c>, object-ledgers.md §7–§8): startup payload-limit validation,
/// single-in-flight coalescing up to <see cref="IAppendableObjectStore.MaxAppendLength"/>, <c>ROLL</c> rotation,
/// tag-seal recovery, tombstones, cached extent slot index, 8 MiB ranged reads, and fail-stop defect reporting.
/// </summary>
public sealed class AppendObjectLedgerStorageTests : IDisposable
{
    private const int Window = 4;
    private static readonly LedgerId Id = new(42);
    private static readonly LedgerId Next = new(43);
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly FakeTimeProvider _clock = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// object-ledgers.md §4, §8.1: startup validates <see cref="ObjectLedgerStoreOptions"/> boundaries unconditionally
    /// and verifies that the payload limit's largest batch framed with its trailer fits one append.
    /// </summary>
    [Fact]
    public void StartupRejectsPayloadLimitWhoseFramedBatchExceedsMaxAppendLength()
    {
        var store = new ForwardingObjectStore();
        var maxPayload = IAppendableObjectStore.MaxAppendLength - ObjectLedgerExtentFormat.FrameHeaderLength - ObjectLedgerExtentFormat.TrailerLength;

        var ok = ObjectLedgerStore.OverAppendable(
            store,
            new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append, MaxBatchBytes = maxPayload },
            _clock);
        Assert.NotNull(ok);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => ObjectLedgerStore.OverAppendable(
            store,
            new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append, MaxBatchBytes = maxPayload + 1 },
            _clock));

        // Unconditional option validation (even for PerSlot layout).
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectLedgerStoreOptions { MaxBatchBytes = 0 }.Validate());
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectLedgerStoreOptions { ExtentTargetBytes = 0 }.Validate());
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectLedgerStoreOptions { ExtentTargetBytes = ObjectLedgerStoreOptions.MaxExtentTargetBytes + 1 }.Validate());
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectLedgerStoreOptions { ExtentMaxAppends = 0 }.Validate());
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectLedgerStoreOptions { ExtentMaxAppends = ObjectLedgerStoreOptions.MaxExtentAppends + 1 }.Validate());
        new ObjectLedgerStoreOptions
        {
            Layout = ObjectLedgerLayout.PerSlot,
            ExtentTargetBytes = ObjectLedgerStoreOptions.MaxExtentTargetBytes,
            ExtentMaxAppends = ObjectLedgerStoreOptions.MaxExtentAppends,
        }.Validate();
    }

    /// <summary>
    /// object-ledgers.md §8.1 (OL-I10): at most one append is in flight per ledger at a time, and queued batches coalesce
    /// into a single append up to <see cref="IAppendableObjectStore.MaxAppendLength"/>, splitting cleanly across appends
    /// when adding the next batch would exceed that ceiling.
    /// </summary>
    [Fact]
    public async Task CoalescesQueuedBatchesUpToMaxAppendLengthWithOneAppendInFlight()
    {
        var store = new BlockingAppendStore();
        var ledgers = ObjectLedgerStore.OverAppendable(
            store,
            new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append },
            _clock);
        await using var storage = ledgers.Open(Id, Window);

        // Slot 0 blocks in flight so slots 1, 2, 3 queue up behind it.
        // Slot 1 + slot 2 fit in one 4 MiB append; slot 3 pushes past MaxAppendLength and lands in the next append.
        var half = IAppendableObjectStore.MaxAppendLength / 2 - 1024;
        var p0 = FilledPayload(0x10, 64);
        var p1 = FilledPayload(0x20, half);
        var p2 = FilledPayload(0x30, half);
        var p3 = FilledPayload(0x40, 8192);

        using var b0 = ArcBuffer.Wrap(p0);
        using var b1 = ArcBuffer.Wrap(p1);
        using var b2 = ArcBuffer.Wrap(p2);
        using var b3 = ArcBuffer.Wrap(p3);

        var w0 = storage.WriteAsync(Slot.Zero, b0, Slot.Zero, Token).AsTask();
        await store.FirstAppendEntered.Task.WaitAsync(Token);

        var w1 = storage.WriteAsync(new Slot(1), b1, Slot.Zero, Token).AsTask();
        var w2 = storage.WriteAsync(new Slot(2), b2, Slot.Zero, Token).AsTask();
        var w3 = storage.WriteAsync(new Slot(3), b3, Slot.Zero, Token).AsTask();

        store.ReleaseFirstAppend.TrySetResult();
        var outcomes = await Task.WhenAll(w0, w1, w2, w3);
        Assert.All(outcomes, static o => Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, o));

        Assert.Equal(1, store.MaxConcurrentAppends);
        Assert.Equal(3, store.AppendLengths.Count);
        Assert.Equal(ObjectLedgerExtentFormat.FrameHeaderLength + p0.Length + ObjectLedgerExtentFormat.TrailerLength, store.AppendLengths[0]);
        Assert.Equal(
            ObjectLedgerExtentFormat.FrameHeaderLength * 2 + p1.Length + p2.Length + ObjectLedgerExtentFormat.TrailerLength,
            store.AppendLengths[1]);
        Assert.Equal(ObjectLedgerExtentFormat.FrameHeaderLength + p3.Length + ObjectLedgerExtentFormat.TrailerLength, store.AppendLengths[2]);
        Assert.All(store.AppendLengths, static len => Assert.True(len <= IAppendableObjectStore.MaxAppendLength));

        // Verify the resulting extent decodes cleanly and every slot reads back identically.
        var extentName = ObjectLedgerNames.Extent(Id, Slot.Zero);
        var props = await store.Inner.GetPropertiesAsync(extentName, Token);
        Assert.NotNull(props);
        using (var raw = (await store.Inner.ReadAsync(extentName, 0, (int)props.Length, Token))!.Value)
        {
            var decoded = ObjectLedgerExtentFormat.DecodeExtent(raw.ToArray());
            var extent = Assert.IsType<ObjectLedgerDecodedExtent>(decoded.Value);
            Assert.Equal(Slot.Zero, extent.FirstSlot);
            Assert.Equal(new Slot(4), extent.NextSlot);
            Assert.Null(extent.RollNextSlot);
        }

        var expected = new[] { p0, p1, p2, p3 };
        for (var i = 0; i < expected.Length; i++)
        {
            var read = await storage.ReadAsync(new Slot(i), new Slot(4), Token);
            Assert.True(read.TryGetValue(out ArcBuffer actual));
            using (actual)
            {
                Assert.Equal(expected[i], actual.ToArray());
            }
        }
    }

    /// <summary>
    /// object-ledgers.md §7.3, §8.1 (OL-I10): when an extent reaches <see cref="ObjectLedgerStoreOptions.ExtentTargetBytes"/>
    /// or <see cref="ObjectLedgerStoreOptions.ExtentMaxAppends"/>, the writer appends a <c>ROLL</c> frame to the current
    /// extent before creating the next extent.
    /// </summary>
    [Fact]
    public async Task RollFramePrecedesNextExtentCreation()
    {
        var store = new EventRecordingStore();
        var options = new ObjectLedgerStoreOptions
        {
            Layout = ObjectLedgerLayout.Append,
            ExtentTargetBytes = 1024 * 1024,
            ExtentMaxAppends = 2,
        };
        var ledgers = ObjectLedgerStore.OverAppendable(store, options, _clock);
        await using var storage = ledgers.Open(Id, Window);

        for (var i = 0; i < 4; i++)
        {
            using var payload = ArcBuffer.Wrap(FilledPayload((byte)(i + 1), 32));
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await storage.WriteAsync(new Slot(i), payload, new Slot(i), Token));
        }

        var ext0 = ObjectLedgerNames.Extent(Id, Slot.Zero);
        var ext2 = ObjectLedgerNames.Extent(Id, new Slot(2));

        // Verify ROLL on ext0 happened strictly before CreateAppendable(ext2).
        var rollIndex = store.Events.IndexOf("append-roll " + ext0.Value);
        var createExt2Index = store.Events.IndexOf("create-appendable " + ext2.Value);
        Assert.True(rollIndex >= 0, "Expected ROLL append on extent 0.");
        Assert.True(createExt2Index > rollIndex, "Expected ROLL append on extent 0 before creating extent 2.");

        // Decode ext0 and confirm its RollNextSlot is 2.
        var props0 = (await store.Inner.GetPropertiesAsync(ext0, Token))!;
        using var raw0 = (await store.Inner.ReadAsync(ext0, 0, (int)props0.Length, Token))!.Value;
        var decoded0 = ObjectLedgerExtentFormat.DecodeExtent(raw0.ToArray());
        var parsed0 = Assert.IsType<ObjectLedgerDecodedExtent>(decoded0.Value);
        Assert.Equal(Slot.Zero, parsed0.FirstSlot);
        Assert.Equal(new Slot(2), parsed0.NextSlot);
        Assert.Equal(new Slot(2), parsed0.RollNextSlot);
    }

    /// <summary>
    /// object-ledgers.md §8.2 (OL-I3, OL-I10): tag-seal recovery handles an empty ledger, a populated extent, a rolled
    /// extent whose successor was never created, a rolled extent whose successor holds batches, repeated idempotent
    /// recovery, and a dropped ledger.
    /// </summary>
    [Fact]
    public async Task TagSealRecoveryHandlesEmptyDataRollAndDroppedLedgers()
    {
        // 1. Empty ledger: recovery creates and seals extent 0, ending at slot 0.
        var emptyStore = new ForwardingObjectStore();
        var emptyLedgers = ObjectLedgerStore.OverAppendable(emptyStore, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        await using (var recovery = emptyLedgers.Open(Id, Window))
        await using (var writer = emptyLedgers.Open(Id, Window))
        {
            var outcome = Assert.IsType<LedgerRecovery>((await recovery.RecoverAsync(Slot.Zero, Token)).Value);
            Assert.Equal(Slot.Zero, outcome.End);
            Assert.NotNull(await emptyStore.Inner.GetPropertiesAsync(ObjectLedgerNames.Extent(Id, Slot.Zero), Token));

            using var payload = ArcBuffer.Wrap(new byte[] { 1, 2, 3 });
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Sealed.Instance, await writer.WriteAsync(Slot.Zero, payload, Slot.Zero, Token));
        }

        // 2. Rolled extent whose next extent was never created: recovery follows ROLL to extent 1, creates and seals it.
        var rolledStore = new ForwardingObjectStore();
        var ext0 = ObjectLedgerNames.Extent(Id, Slot.Zero);
        var ext1 = ObjectLedgerNames.Extent(Id, new Slot(1));
        _ = await rolledStore.Inner.CreateAppendableAsync(ext0, Token);
        using (var dataFrame = EncodeSingleDataAppend(Slot.Zero, [10, 20], startOffset: 0))
        {
            Assert.IsType<ObjectAppendOutcome.Appended>((await rolledStore.Inner.AppendAsync(ext0, 0, dataFrame, crc32c: null, Token)).Value);
            using var rollFrame = ObjectLedgerExtentFormat.EncodeRollAppend(new Slot(1), dataFrame.Length);
            Assert.IsType<ObjectAppendOutcome.Appended>((await rolledStore.Inner.AppendAsync(ext0, dataFrame.Length, rollFrame, crc32c: null, Token)).Value);
        }

        var rolledLedgers = ObjectLedgerStore.OverAppendable(rolledStore, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        await using (var recovery = rolledLedgers.Open(Id, Window))
        {
            var outcome = Assert.IsType<LedgerRecovery>((await recovery.RecoverAsync(Slot.Zero, Token)).Value);
            Assert.Equal(new Slot(1), outcome.End);
            Assert.NotNull(await rolledStore.Inner.GetPropertiesAsync(ext1, Token));
            using var probe = ArcBuffer.Wrap(new byte[] { 0 });
            Assert.IsType<ObjectAppendOutcome.Sealed>((await rolledStore.Inner.AppendAsync(ext1, 0, probe, crc32c: null, Token)).Value);

            // Idempotent second recovery on a fresh storage instance returns the same end.
            await using var again = rolledLedgers.Open(Id, Window);
            Assert.Equal(outcome, (await again.RecoverAsync(Slot.Zero, Token)).Value);
        }

        // 3. After sweep (dropped marker + tombstone), reads and recovery report Dropped and writes report Sealed.
        Assert.Equal(1, await new ObjectLedgerSweeper(rolledLedgers).SweepAsync(static id => id == Id, Next, Token));
        await using (var afterDrop = rolledLedgers.Open(Id, Window))
        {
            Assert.Equal<LedgerRecoveryOutcome>(ReplicaWriteOutcome.Dropped.Instance, await afterDrop.RecoverAsync(Slot.Zero, Token));
            Assert.True((await afterDrop.ReadAsync(Slot.Zero, new Slot(1), Token)).TryGetValue(out ReplicaWriteOutcome.Dropped? _));
            using var payload = ArcBuffer.Wrap(new byte[] { 0x80 });
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Sealed.Instance, await afterDrop.WriteAsync(new Slot(1), payload, new Slot(1), Token));
        }
    }

    /// <summary>
    /// object-ledgers.md §8.3: once an extent's slot locations are cached (either from writes or from a trailer walk),
    /// repeated point reads fetch only the slot's batch bytes without re-listing extents or re-reading trailers/headers.
    /// </summary>
    [Fact]
    public async Task PointReadsUseCachedExtentSlotIndexWithoutRewalkingTrailers()
    {
        var store = new EventRecordingStore();
        // Disable the derived batch cache so the test directly verifies the extent-slot index read path.
        var ledgers = ObjectLedgerStore.OverAppendable(
            store,
            new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append, CacheBytes = 0 },
            _clock);

        var p0 = FilledPayload(0xA1, 40);
        var p1 = FilledPayload(0xB2, 50);
        await using (var writer = ledgers.Open(Id, Window))
        {
            using var b0 = ArcBuffer.Wrap(p0);
            using var b1 = ArcBuffer.Wrap(p1);
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await writer.WriteAsync(Slot.Zero, b0, Slot.Zero, Token));
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await writer.WriteAsync(new Slot(1), b1, new Slot(1), Token));

            // Writer already cached both slots: reading slot 1 issues exactly one exact-length ReadAsync and zero listings.
            store.ResetCounts();
            var read = await writer.ReadAsync(new Slot(1), new Slot(2), Token);
            Assert.True(read.TryGetValue(out ArcBuffer actual));
            using (actual)
            {
                Assert.Equal(p1, actual.ToArray());
            }

            Assert.Equal(0, store.ListCount);
            Assert.Equal(1, store.ReadCount);
            Assert.Equal(p1.Length, store.ReadLengths[0]);
        }

        // A fresh reader walks trailers on its first read, then serves a repeat read with a single payload ReadAsync.
        await using var reader = ledgers.Open(Id, Window);
        var first = await reader.ReadAsync(new Slot(1), new Slot(2), Token);
        Assert.True(first.TryGetValue(out ArcBuffer firstArc));
        firstArc.Dispose();

        store.ResetCounts();
        var second = await reader.ReadAsync(new Slot(1), new Slot(2), Token);
        Assert.True(second.TryGetValue(out ArcBuffer secondArc));
        using (secondArc)
        {
            Assert.Equal(p1, secondArc.ToArray());
        }

        Assert.Equal(0, store.ListCount);
        Assert.Equal(1, store.ReadCount);
        Assert.Equal(p1.Length, store.ReadLengths[0]);
    }

    /// <summary>
    /// object-ledgers.md §8.4: <see cref="ILedgerStorage.ReadRangeAsync"/> reads extents in 8 MiB ranged chunks across
    /// chunk boundaries and multiple extents, and works end-to-end through <see cref="Ledger"/> range reads.
    /// </summary>
    [Fact]
    public async Task ReadRangeReadsExtentsInEightMiBChunksAndSpansMultipleExtents()
    {
        var store = new EventRecordingStore();
        var ledgers = ObjectLedgerStore.OverAppendable(
            store,
            new ObjectLedgerStoreOptions
            {
                Layout = ObjectLedgerLayout.Append,
                ExtentTargetBytes = 12L * 1024 * 1024,
                ExtentMaxAppends = 3,
            },
            _clock);

        // 3 batches of 3.5 MiB each in extent 0 (~10.5 MiB total > 8 MiB chunk size), then 2 smaller batches in extent 1.
        const int LargeSize = 3_670_016;
        var payloads = new[]
        {
            FilledPayload(0x11, LargeSize),
            FilledPayload(0x22, LargeSize),
            FilledPayload(0x33, LargeSize),
            FilledPayload(0x44, 128),
            FilledPayload(0x55, 256),
        };

        await using (var writer = ledgers.Open(Id, Window))
        {
            for (var i = 0; i < payloads.Length; i++)
            {
                using var buf = ArcBuffer.Wrap(payloads[i]);
                Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await writer.WriteAsync(new Slot(i), buf, new Slot(i), Token));
            }
        }

        // Fresh reader scans all 5 slots through Ledger.ReadRangeAsync.
        await using var storage = ledgers.Open(Id, Window);
        await using var ledger = new Ledger(Id, storage, LedgerLocation.ObjectStore.Instance);
        store.ResetCounts();

        List<byte[]> scanned = [];
        await foreach (var item in ledger.ReadRangeAsync(Slot.Zero, new Slot(payloads.Length), window: null, cancellationToken: Token))
        {
            Assert.True(item.TryGetValue(out ArcBuffer batch));
            using (batch)
            {
                scanned.Add(batch.ToArray());
            }
        }

        Assert.Equal(payloads.Length, scanned.Count);
        for (var i = 0; i < payloads.Length; i++)
        {
            Assert.Equal(payloads[i], scanned[i]);
        }

        // Extent 0 (> 8 MiB) must be read in exactly two chunks: 8 MiB followed by the remainder, plus one read for extent 1.
        Assert.Equal(3, store.ReadLengths.Count);
        Assert.Equal(8 * 1024 * 1024, store.ReadLengths[0]);
        Assert.True(store.ReadLengths[1] is > 0 and < 8 * 1024 * 1024);
        Assert.True(store.ReadLengths[2] is > 0 and < 8 * 1024 * 1024);

        // Mid-extent scan starting at slot 2 skips earlier appends via trailer walk and still crosses into extent 1.
        List<byte[]> tailScan = [];
        await foreach (var item in ledger.ReadRangeAsync(new Slot(2), new Slot(payloads.Length), window: null, cancellationToken: Token))
        {
            Assert.True(item.TryGetValue(out ArcBuffer batch));
            using (batch)
            {
                tailScan.Add(batch.ToArray());
            }
        }

        Assert.Equal(3, tailScan.Count);
        Assert.Equal(payloads[2], tailScan[0]);
        Assert.Equal(payloads[3], tailScan[1]);
        Assert.Equal(payloads[4], tailScan[2]);
    }

    /// <summary>
    /// conventions.md §14: an <see cref="ObjectContentChangedException"/> on an append or tombstone replacement is a defect
    /// reported to <see cref="FatalErrorHandler"/>.
    /// </summary>
    [Fact]
    public async Task ContentChangedOnAppendOrTombstoneIsReportedAsADefect()
    {
        var appendStore = new ChangingAppendStore { FailAppend = true };
        var appendLedgers = ObjectLedgerStore.OverAppendable(appendStore, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        var sinceAppend = FatalErrors.ReportedCount;
        await using (var storage = appendLedgers.Open(Id, Window))
        {
            using var payload = ArcBuffer.Wrap(new byte[] { 1, 2, 3 });
            var changed = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await storage.WriteAsync(Slot.Zero, payload, Slot.Zero, Token));
            var report = Assert.Single(FatalErrors.TakeMine(sinceAppend));
            Assert.Equal(FatalErrorSite.Defect, report.Site);
            Assert.Same(changed, report.Failure.Exception);
        }

        var tombStore = new ChangingAppendStore { FailTombstone = true };
        var tombLedgers = ObjectLedgerStore.OverAppendable(tombStore, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        await using (var storage = tombLedgers.Open(Id, Window))
        {
            using var payload = ArcBuffer.Wrap(new byte[] { 1, 2, 3 });
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await storage.WriteAsync(Slot.Zero, payload, Slot.Zero, Token));
        }

        var sinceTomb = FatalErrors.ReportedCount;
        var sweeper = new ObjectLedgerSweeper(tombLedgers);
        var tombChanged = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await sweeper.SweepAsync(static id => id == Id, Next, Token));
        var tombReport = Assert.Single(FatalErrors.TakeMine(sinceTomb));
        Assert.Equal(FatalErrorSite.Defect, tombReport.Site);
        Assert.Same(tombChanged, tombReport.Failure.Exception);
    }

    /// <summary>
    /// OL-I7 regression: when a reader cached an extent's <c>AppendSpan</c> from a trailer walk before the sweeper
    /// replaced the fence extent with a block-blob <c>SEAL</c> tombstone, a subsequent read of that span meets the
    /// tombstone's <c>SEAL</c> frame and answers <c>Dropped</c> once <c>dropped</c> exists.
    /// </summary>
    [Fact]
    public async Task ReadOfCachedAppendSpanAfterTombstoneReplacementAnswersDropped()
    {
        var store = new ForwardingObjectStore();
        var ledgers = ObjectLedgerStore.OverAppendable(
            store,
            new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append },
            _clock);

        await using var storage = ledgers.Open(Id, Window);
        using (var payload = ArcBuffer.Wrap(FilledPayload(0x41, 8)))
        {
            Assert.Equal<LedgerWriteOutcome>(
                ReplicaWriteOutcome.Written.Instance,
                await storage.WriteAsync(Slot.Zero, payload, Slot.Zero, Token));
        }

        // Recover seals extent 0 at End = 1.
        var recovered = await storage.RecoverAsync(Slot.Zero, Token);
        var rec = Assert.IsType<LedgerRecovery>(recovered.Value);
        Assert.Equal(new Slot(1), rec.End);

        // Reading slot 1 walks extent 0's trailers and caches AppendSpan(0..1) without indexing slot 0's frame header.
        var miss = await storage.ReadAsync(new Slot(1), new Slot(1), Token);
        Assert.Equal<ReplicaRead>(ReplicaRead.Missing.Instance, miss);

        // The sweeper writes `dropped` and replaces extent 0 with a 17-byte block-blob SEAL tombstone.
        var sweeper = new ObjectLedgerSweeper(ledgers);
        _ = await sweeper.SweepAsync(static id => id == Id, Next, Token);

        // Reading slot 0 hits the cached AppendSpan, reads the tombstone's SEAL frame at offset 0, and must answer Dropped.
        var point = await storage.ReadAsync(Slot.Zero, new Slot(1), Token);
        Assert.Equal<ReplicaRead>(ReplicaWriteOutcome.Dropped.Instance, point);

        // Range read of slot 0 must also answer Dropped.
        List<ReplicaRead> ranged = [];
        await foreach (var item in storage.ReadRangeAsync(Slot.Zero, new Slot(1), new Slot(1), window: null, cancellationToken: Token))
        {
            ranged.Add(item);
        }

        Assert.Equal([ReplicaWriteOutcome.Dropped.Instance], ranged);
    }

    /// <summary>
    /// object-ledgers.md §8.1: exceeding <see cref="ObjectLedgerStoreOptions.ExtentTargetBytes"/> alone triggers a
    /// <c>ROLL</c> frame and next extent even when <see cref="ObjectLedgerStoreOptions.ExtentMaxAppends"/> is large.
    /// </summary>
    [Fact]
    public async Task ExtentTargetBytesTriggersRollBeforeNextExtent()
    {
        var store = new EventRecordingStore();
        var ledgers = ObjectLedgerStore.OverAppendable(
            store,
            new ObjectLedgerStoreOptions
            {
                Layout = ObjectLedgerLayout.Append,
                ExtentTargetBytes = 128,
                ExtentMaxAppends = 1000,
            },
            _clock);
        await using var storage = ledgers.Open(Id, Window);

        using var b0 = ArcBuffer.Wrap(FilledPayload(0x10, 100));
        using var b1 = ArcBuffer.Wrap(FilledPayload(0x20, 100));
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await storage.WriteAsync(Slot.Zero, b0, Slot.Zero, Token));
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await storage.WriteAsync(new Slot(1), b1, new Slot(1), Token));

        var ext0 = ObjectLedgerNames.Extent(Id, Slot.Zero);
        var ext1 = ObjectLedgerNames.Extent(Id, new Slot(1));
        Assert.Contains("append-roll " + ext0.Value, store.Events);
        Assert.Contains("create-appendable " + ext1.Value, store.Events);
        Assert.NotNull(await store.Inner.GetPropertiesAsync(ext1, Token));
    }

    /// <summary>
    /// object-ledgers.md §8.3: byte-range <see cref="ILedgerStorage.ReadAsync(Slot, int, int, Slot, CancellationToken)"/>
    /// returns exact slices and answers <see cref="ReplicaRead.OutsideBatch"/> when the range passes the batch length.
    /// </summary>
    [Fact]
    public async Task ByteRangePointReadReturnsExactSliceAndRefusesOutOfRange()
    {
        var store = new ForwardingObjectStore();
        var ledgers = ObjectLedgerStore.OverAppendable(store, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        var payload = FilledPayload(0x33, 64);
        await using (var writer = ledgers.Open(Id, Window))
        {
            using var buf = ArcBuffer.Wrap(payload);
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await writer.WriteAsync(Slot.Zero, buf, Slot.Zero, Token));
        }

        await using var reader = ledgers.Open(Id, Window);
        var slice = await reader.ReadAsync(Slot.Zero, offset: 10, length: 20, acknowledged: new Slot(1), Token);
        Assert.True(slice.TryGetValue(out ArcBuffer actual));
        using (actual)
        {
            Assert.Equal(payload.AsSpan(10, 20).ToArray(), actual.ToArray());
        }

        var outside = await reader.ReadAsync(Slot.Zero, offset: 60, length: 10, acknowledged: new Slot(1), Token);
        Assert.True(outside.TryGetValue(out ReplicaRead.OutsideBatch? _));
    }

    /// <summary>
    /// Regression for in-flight duplicate writes and caller cancellation: a duplicate <see cref="ILedgerStorage.WriteAsync"/>
    /// for a slot already in flight joins the in-flight item without hanging or leaking its <see cref="ArcBuffer"/>, a
    /// conflicting payload throws <see cref="InvalidOperationException"/>, and one waiter canceling neither cancels the
    /// other waiter nor strands higher queued slots.
    /// </summary>
    [Fact]
    public async Task ConcurrentDuplicateInFlightWriteJoinsAndCancelingOneWaiterDoesNotStrandOthers()
    {
        var store = new BlockingAppendStore();
        var ledgers = ObjectLedgerStore.OverAppendable(store, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        await using var storage = ledgers.Open(Id, Window);

        var p0 = FilledPayload(0x50, 32);
        var p0Conflict = FilledPayload(0x60, 32);
        var p1 = FilledPayload(0x70, 32);
        using var b0 = ArcBuffer.Wrap(p0);
        using var b0Dup = ArcBuffer.Wrap(p0);
        using var b0Conflict = ArcBuffer.Wrap(p0Conflict);
        using var b1 = ArcBuffer.Wrap(p1);

        var w0 = storage.WriteAsync(Slot.Zero, b0, Slot.Zero, Token).AsTask();
        await store.FirstAppendEntered.Task.WaitAsync(Token);

        using var callerCancel = new CancellationTokenSource();
        var w0Dup = storage.WriteAsync(Slot.Zero, b0Dup, Slot.Zero, callerCancel.Token).AsTask();
        _ = await Assert.ThrowsAsync<LedgerStorageCorruptException>(async () => await storage.WriteAsync(Slot.Zero, b0Conflict, Slot.Zero, Token));

        // Queue slot 1 behind in-flight slot 0, cancel the duplicate waiter on slot 0, then release the append.
        var w1 = storage.WriteAsync(new Slot(1), b1, Slot.Zero, Token).AsTask();
        await callerCancel.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w0Dup);

        store.ReleaseFirstAppend.TrySetResult();
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await w0);
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await w1);

        // Both slots are durable and readable.
        for (var i = 0; i < 2; i++)
        {
            var read = await storage.ReadAsync(new Slot(i), new Slot(2), Token);
            Assert.True(read.TryGetValue(out ArcBuffer got));
            using (got)
            {
                Assert.Equal(i == 0 ? p0 : p1, got.ToArray());
            }
        }
    }

    /// <summary>
    /// Unknown-outcome recovery on both <c>DATA</c> and <c>ROLL</c> appends: when an append actually lands in storage
    /// before a transient <see cref="IOException"/> or <see cref="ObjectAppendOutcome.LengthMismatch"/>, the writer
    /// settles the append from storage without duplicating frames.
    /// </summary>
    [Fact]
    public async Task TransientFailureAfterDataAndRollAppendsLandSettlesFromStorage()
    {
        var store = new LoseAnswerAfterLandingStore { FailFirstData = true, FailFirstRoll = true };
        var ledgers = ObjectLedgerStore.OverAppendable(
            store,
            new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append, ExtentTargetBytes = 1024, ExtentMaxAppends = 1 },
            _clock);
        await using var storage = ledgers.Open(Id, Window);

        var p0 = FilledPayload(0x11, 24);
        var p1 = FilledPayload(0x22, 24);
        using var b0 = ArcBuffer.Wrap(p0);
        using var b1 = ArcBuffer.Wrap(p1);

        // First DATA append lands on the store before its answer is lost with IOException; retry settles it without duplicating.
        _ = await Assert.ThrowsAsync<IOException>(async () => await storage.WriteAsync(Slot.Zero, b0, Slot.Zero, Token));
        Assert.True(store.FailedData);
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await storage.WriteAsync(Slot.Zero, b0, Slot.Zero, Token));

        // ROLL append lands on the store before its answer is lost with IOException; retry settles the ROLL and writes slot 1 in extent 1.
        _ = await Assert.ThrowsAsync<IOException>(async () => await storage.WriteAsync(new Slot(1), b1, new Slot(1), Token));
        Assert.True(store.FailedRoll);
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await storage.WriteAsync(new Slot(1), b1, new Slot(1), Token));

        await using var reader = ledgers.Open(Id, Window);
        for (var i = 0; i < 2; i++)
        {
            var read = await reader.ReadAsync(new Slot(i), new Slot(2), Token);
            Assert.True(read.TryGetValue(out ArcBuffer got));
            using (got)
            {
                Assert.Equal(i == 0 ? p0 : p1, got.ToArray());
            }
        }
    }

    /// <summary>
    /// Corruption detection: a non-advancing <c>ROLL</c> frame throws <see cref="LedgerStorageCorruptException"/>
    /// instead of looping forever in recovery, and a truncated final append missing its trailer throws
    /// <see cref="LedgerStorageCorruptException"/> in <see cref="ILedgerStorage.ReadRangeAsync"/>.
    /// </summary>
    [Fact]
    public async Task CorruptNonAdvancingRollAndTruncatedFinalAppendThrowCorrupt()
    {
        // 1. Non-advancing ROLL on extent 0 naming nextSlot = 0.
        var rollStore = new ForwardingObjectStore();
        var ext0 = ObjectLedgerNames.Extent(Id, Slot.Zero);
        _ = await rollStore.Inner.CreateAppendableAsync(ext0, Token);
        using (var badRoll = ObjectLedgerExtentFormat.EncodeRollAppend(Slot.Zero, startOffset: 0))
        {
            Assert.IsType<ObjectAppendOutcome.Appended>((await rollStore.Inner.AppendAsync(ext0, 0, badRoll, crc32c: null, Token)).Value);
        }

        var rollLedgers = ObjectLedgerStore.OverAppendable(rollStore, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        await using (var recovery = rollLedgers.Open(Id, Window))
        {
            _ = await Assert.ThrowsAsync<LedgerStorageCorruptException>(async () => await recovery.RecoverAsync(Slot.Zero, Token));
        }

        // 2. Truncated final append (valid first append + second DATA frame with no trailer) in ReadRangeAsync.
        var truncStore = new ForwardingObjectStore();
        _ = await truncStore.Inner.CreateAppendableAsync(ext0, Token);
        using (var first = EncodeSingleDataAppend(Slot.Zero, [1, 2, 3], startOffset: 0))
        {
            Assert.IsType<ObjectAppendOutcome.Appended>((await truncStore.Inner.AppendAsync(ext0, 0, first, crc32c: null, Token)).Value);
            var orphanHeader = new byte[ObjectLedgerExtentFormat.FrameHeaderLength + 2];
            var crc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Data, new Slot(1), [4, 5]);
            ObjectLedgerExtentFormat.WriteFrameHeader(orphanHeader, ObjectLedgerFrameKind.Data, new Slot(1), 2, crc);
            orphanHeader[ObjectLedgerExtentFormat.FrameHeaderLength] = 4;
            orphanHeader[ObjectLedgerExtentFormat.FrameHeaderLength + 1] = 5;
            using var orphanBuf = ArcBuffer.Wrap(orphanHeader);
            Assert.IsType<ObjectAppendOutcome.Appended>((await truncStore.Inner.AppendAsync(ext0, first.Length, orphanBuf, crc32c: null, Token)).Value);
        }

        var truncLedgers = ObjectLedgerStore.OverAppendable(truncStore, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        await using var reader = truncLedgers.Open(Id, Window);
        _ = await Assert.ThrowsAsync<LedgerStorageCorruptException>(async () =>
        {
            await foreach (var item in reader.ReadRangeAsync(Slot.Zero, new Slot(2), new Slot(2), window: null, cancellationToken: Token))
            {
                if (item.TryGetValue(out ArcBuffer buf))
                {
                    buf.Dispose();
                }
            }
        });
    }

    /// <summary>
    /// Breaking out of <see cref="ILedgerStorage.ReadRangeAsync"/> early releases all rented chunk buffers cleanly.
    /// </summary>
    [Fact]
    public async Task EarlyBreakFromReadRangeReleasesAllBuffers()
    {
        var store = new ForwardingObjectStore();
        var ledgers = ObjectLedgerStore.OverAppendable(store, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        await using var storage = ledgers.Open(Id, Window);

        for (var i = 0; i < 4; i++)
        {
            using var payload = ArcBuffer.Wrap(FilledPayload((byte)(i + 1), 128));
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await storage.WriteAsync(new Slot(i), payload, new Slot(i), Token));
        }

        var seen = 0;
        await foreach (var item in storage.ReadRangeAsync(Slot.Zero, new Slot(4), new Slot(4), window: null, cancellationToken: Token))
        {
            Assert.True(item.TryGetValue(out ArcBuffer buf));
            buf.Dispose();
            seen++;
            break;
        }

        Assert.Equal(1, seen);
    }

    /// <summary>
    /// <see cref="ILedgerStorage.StopReadsAsync"/> and <see cref="IAsyncDisposable.DisposeAsync"/> complete queued and
    /// in-flight writes cleanly without hanging or leaking <see cref="ArcBuffer"/>s.
    /// </summary>
    [Fact]
    public async Task DisposeWithInFlightAndQueuedWritesCancelsCleanlyWithoutLeaks()
    {
        var store = new BlockingAppendStore();
        var ledgers = ObjectLedgerStore.OverAppendable(store, new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }, _clock);
        var storage = ledgers.Open(Id, Window);

        using var b0 = ArcBuffer.Wrap(FilledPayload(0x01, 16));
        using var b1 = ArcBuffer.Wrap(FilledPayload(0x02, 16));

        var w0 = storage.WriteAsync(Slot.Zero, b0, Slot.Zero, Token).AsTask();
        await store.FirstAppendEntered.Task.WaitAsync(Token);
        var w1 = storage.WriteAsync(new Slot(1), b1, Slot.Zero, Token).AsTask();

        await storage.StopReadsAsync();
        await storage.DisposeAsync();

        _ = await Assert.ThrowsAnyAsync<Exception>(() => w0);
        _ = await Assert.ThrowsAnyAsync<Exception>(() => w1);
    }

    [Fact]
    public async Task RangeScanFollowedByPointReadHandlesCombinedDataAndRollAppend()
    {
        var store = new ForwardingObjectStore();
        var ledger = new LedgerId(0x410UL);

        // Case 1: single DATA + trailing ROLL in one append (Count == 2).
        var p0 = FilledPayload(0x11, 24);
        var p1 = FilledPayload(0x22, 24);
        var e0 = ObjectLedgerNames.Extent(ledger, Slot.Zero);
        var e1 = ObjectLedgerNames.Extent(ledger, new Slot(1));
        _ = await store.CreateAppendableAsync(e0, Token);
        using (var combined = EncodeDataAndRollAppend(Slot.Zero, [p0], rollNextSlot: new Slot(1), startOffset: 0))
        {
            _ = await store.AppendAsync(e0, 0, combined, ObjectStoreChecksums.Crc32C(combined.AsReadOnlySequence()), Token);
        }

        _ = await store.CreateAppendableAsync(e1, Token);
        using (var append1 = EncodeSingleDataAppend(new Slot(1), p1, startOffset: 0))
        {
            _ = await store.AppendAsync(e1, 0, append1, ObjectStoreChecksums.Crc32C(append1.AsReadOnlySequence()), Token);
        }

        // Case 2: multi-DATA + trailing ROLL in one append (Count == 3).
        var ledger2 = new LedgerId(0x411UL);
        var q0 = FilledPayload(0x33, 20);
        var q1 = FilledPayload(0x44, 20);
        var q2 = FilledPayload(0x55, 20);
        var l2e0 = ObjectLedgerNames.Extent(ledger2, Slot.Zero);
        var l2e2 = ObjectLedgerNames.Extent(ledger2, new Slot(2));
        _ = await store.CreateAppendableAsync(l2e0, Token);
        using (var combined2 = EncodeDataAndRollAppend(Slot.Zero, [q0, q1], rollNextSlot: new Slot(2), startOffset: 0))
        {
            _ = await store.AppendAsync(l2e0, 0, combined2, ObjectStoreChecksums.Crc32C(combined2.AsReadOnlySequence()), Token);
        }

        _ = await store.CreateAppendableAsync(l2e2, Token);
        using (var append2 = EncodeSingleDataAppend(new Slot(2), q2, startOffset: 0))
        {
            _ = await store.AppendAsync(l2e2, 0, append2, ObjectStoreChecksums.Crc32C(append2.AsReadOnlySequence()), Token);
        }

        await using var reader1 = Open(store, ledger);
        var scanned1 = new List<byte[]>();
        await foreach (var item in reader1.ReadRangeAsync(Slot.Zero, new Slot(2), new Slot(2), cancellationToken: Token))
        {
            using var arc = Assert.IsType<ArcBuffer>(item.Value);
            scanned1.Add(arc.ToArray());
        }

        Assert.Equal([p0, p1], scanned1);

        // Point read after range scan must resolve the cached [DATA, ROLL] AppendSpan without false corruption.
        var point0 = await reader1.ReadAsync(Slot.Zero, new Slot(2), Token);
        using (var arc0 = Assert.IsType<ArcBuffer>(point0.Value))
        {
            Assert.Equal(p0, arc0.ToArray());
        }

        await using var reader2 = Open(store, ledger2);
        var scanned2 = new List<byte[]>();
        await foreach (var item in reader2.ReadRangeAsync(Slot.Zero, new Slot(3), new Slot(3), cancellationToken: Token))
        {
            using var arc = Assert.IsType<ArcBuffer>(item.Value);
            scanned2.Add(arc.ToArray());
        }

        Assert.Equal([q0, q1, q2], scanned2);

        var point20 = await reader2.ReadAsync(Slot.Zero, new Slot(3), Token);
        using (var arc20 = Assert.IsType<ArcBuffer>(point20.Value))
        {
            Assert.Equal(q0, arc20.ToArray());
        }

        var point21 = await reader2.ReadAsync(new Slot(1), new Slot(3), Token);
        using var arc21 = Assert.IsType<ArcBuffer>(point21.Value);
        Assert.Equal(q1, arc21.ToArray());
    }

    [Fact]
    public async Task ConcurrentReaderJoiningInFlightTrailerWalkRewalksForFreshlyAcknowledgedSlot()
    {
        var store = new BlockingWalkReadStore();
        var ledger = new LedgerId(0x412UL);
        var p0 = FilledPayload(0x61, 32);
        var p1 = FilledPayload(0x62, 32);

        await using (var writer = Open(store.Inner, ledger))
        {
            using var b0 = ArcBuffer.Wrap(p0);
            Assert.IsType<ReplicaWriteOutcome.Written>((await writer.WriteAsync(Slot.Zero, b0, Slot.Zero, Token)).Value);
        }

        await using var reader = Open(store, ledger);

        // Start Reader A: its GetPropertiesAsync snapshots extent 0 with only slot 0, then pauses on the trailer read.
        var readA = reader.ReadAsync(Slot.Zero, new Slot(1), Token).AsTask();
        await store.FirstReadEntered.Task.WaitAsync(Token);

        // Append slot 1 directly to extent 0 so it is durable before Reader B starts.
        var e0 = ObjectLedgerNames.Extent(ledger, Slot.Zero);
        var props = await store.Inner.GetPropertiesAsync(e0, Token);
        Assert.NotNull(props);
        using (var append1 = EncodeSingleDataAppend(new Slot(1), p1, startOffset: props.Length))
        {
            var appended = await store.Inner.AppendAsync(
                e0,
                props.Length,
                append1,
                ObjectStoreChecksums.Crc32C(append1.AsReadOnlySequence()),
                Token);
            Assert.IsType<ObjectAppendOutcome.Appended>(appended.Value);
        }

        // Reader B starts after slot 1 is durable and joins Reader A's in-flight walk.
        var readB = reader.ReadAsync(new Slot(1), new Slot(2), Token).AsTask();

        store.ReleaseFirstRead.TrySetResult();

        var outcomeA = await readA;
        using (var arcA = Assert.IsType<ArcBuffer>(outcomeA.Value))
        {
            Assert.Equal(p0, arcA.ToArray());
        }

        var outcomeB = await readB;
        using var arcB = Assert.IsType<ArcBuffer>(outcomeB.Value);
        Assert.Equal(p1, arcB.ToArray());
    }

    [Fact]
    public async Task ReadRangeDisposesPayloadWhenIsDroppedCheckThrows()
    {
        var store = new FailingDroppedCheckStore();
        var ledger = new LedgerId(0x413UL);
        var p0 = FilledPayload(0x71, 64);

        await using (var writer = Open(store.Inner, ledger))
        {
            using var b0 = ArcBuffer.Wrap(p0);
            Assert.IsType<ReplicaWriteOutcome.Written>((await writer.WriteAsync(Slot.Zero, b0, Slot.Zero, Token)).Value);
        }

        await using var reader = Open(store, ledger);
        _ = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var item in reader.ReadRangeAsync(Slot.Zero, new Slot(1), acknowledged: Slot.Zero, cancellationToken: Token))
            {
                if (item.Value is ArcBuffer arc)
                {
                    arc.Dispose();
                }
            }
        });
    }

    [Fact]
    public async Task SettlementAfterLostAnswerSucceedsWhenConcurrentWritePrunesWrittenCrcs()
    {
        var store = new PruneDuringSettlementStore();
        var ledger = new LedgerId(0x414UL);
        await using var storage = Open(store, ledger);

        // Fill >256 slots so PruneWrittenCrcsLocked will prune once acknowledged advances past 256.
        for (var i = 0; i <= 260; i++)
        {
            using var buf = ArcBuffer.Wrap(FilledPayload((byte)(i & 0x7F), 16));
            Assert.IsType<ReplicaWriteOutcome.Written>((await storage.WriteAsync(new Slot(i), buf, Slot.Zero, Token)).Value);
        }

        store.ArmLostAnswerAndPauseTailRead();

        using var b261 = ArcBuffer.Wrap(FilledPayload(0xA1, 16));
        using var b262 = ArcBuffer.Wrap(FilledPayload(0xA2, 16));

        // Slot 261 lands, loses its answer, then enters settlement on retry.
        _ = await Assert.ThrowsAsync<IOException>(() => storage.WriteAsync(new Slot(261), b261, Slot.Zero, Token).AsTask());

        var retry261 = storage.WriteAsync(new Slot(261), b261, Slot.Zero, Token).AsTask();
        await store.TailReadEntered.Task.WaitAsync(Token);

        // While settlement is reading the extent tail, a concurrent write with acknowledged=262 prunes _writtenCrcs below 262.
        var write262 = storage.WriteAsync(new Slot(262), b262, acknowledged: new Slot(262), Token).AsTask();
        store.ReleaseTailRead.TrySetResult();

        Assert.IsType<ReplicaWriteOutcome.Written>((await retry261).Value);
        Assert.IsType<ReplicaWriteOutcome.Written>((await write262).Value);
    }

    [Fact]
    public async Task ConcurrentWriteAndDisposeCompleteWithoutDeadlockOrLeak()
    {
        var store = new ForwardingObjectStore();
        for (var round = 0; round < 16; round++)
        {
            var ledger = new LedgerId(0x420UL + (ulong)round);
            var storage = Open(store, ledger);
            using var b0 = ArcBuffer.Wrap(FilledPayload(0x81, 32));
            using var b1 = ArcBuffer.Wrap(FilledPayload(0x82, 32));
            using var b2 = ArcBuffer.Wrap(FilledPayload(0x83, 32));

            var w0 = Task.Run(async () =>
            {
                try
                {
                    _ = await storage.WriteAsync(Slot.Zero, b0, Slot.Zero, Token);
                }
                catch (ObjectDisposedException)
                {
                }
            }, Token);
            var w1 = Task.Run(async () =>
            {
                try
                {
                    _ = await storage.WriteAsync(new Slot(1), b1, Slot.Zero, Token);
                }
                catch (ObjectDisposedException)
                {
                }
            }, Token);
            var w2 = Task.Run(async () =>
            {
                try
                {
                    _ = await storage.WriteAsync(new Slot(2), b2, Slot.Zero, Token);
                }
                catch (ObjectDisposedException)
                {
                }
            }, Token);

            await storage.DisposeAsync();
            await Task.WhenAll(w0, w1, w2).WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    [Fact]
    public async Task VerifyPrunedWrittenSlotCancelsCleanlyOnDispose()
    {
        var store = new BlockingFirstExtentReadStore();
        var ledger = new LedgerId(0x430UL);
        var options = new ObjectLedgerStoreOptions
        {
            Layout = ObjectLedgerLayout.Append,
            ExtentMaxAppends = 4,
        };
        var storage = Open(store, ledger, options);

        // Write slot 0 into extent 0, then fill >MaxCachedSlots across subsequent extents so extent 0's Slots map is evicted.
        var p0 = FilledPayload(0x91, 16);
        using (var b0 = ArcBuffer.Wrap(p0))
        {
            Assert.IsType<ReplicaWriteOutcome.Written>((await storage.WriteAsync(Slot.Zero, b0, Slot.Zero, Token)).Value);
        }

        const int waveSize = 512;
        var payload = FilledPayload(0x92, 16);
        long slotCursor = 1;
        while (slotCursor <= AppendExtentIndex.MaxCachedSlots + waveSize)
        {
            var tasks = new Task<LedgerWriteOutcome>[waveSize];
            var buffers = new ArcBuffer[waveSize];
            try
            {
                for (var i = 0; i < waveSize; i++)
                {
                    buffers[i] = ArcBuffer.Wrap(payload);
                    tasks[i] = storage.WriteAsync(new Slot(slotCursor + i), buffers[i], Slot.Zero, Token).AsTask();
                }

                var outcomes = await Task.WhenAll(tasks);
                Assert.All(outcomes, o => Assert.IsType<ReplicaWriteOutcome.Written>(o.Value));
            }
            finally
            {
                foreach (var b in buffers)
                {
                    b.Dispose();
                }
            }

            slotCursor += waveSize;
        }

        // Now retry slot 0 with acknowledged = slotCursor: _writtenCrcs prunes slot 0 and extent 0's Slots was evicted,
        // forcing VerifyPrunedWrittenSlotAsync to issue an object read that blocks until DisposeAsync cancels _writes.
        store.ArmBlockOnRead();
        using var retry0 = ArcBuffer.Wrap(p0);
        var retryTask = storage.WriteAsync(Slot.Zero, retry0, acknowledged: new Slot(slotCursor), Token).AsTask();
        await store.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        var disposeTask = storage.DisposeAsync().AsTask();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => retryTask.WaitAsync(TimeSpan.FromSeconds(10), Token));
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(10), Token);
    }

    private ILedgerStorage Open<TStore>(TStore store, LedgerId ledger, ObjectLedgerStoreOptions? options = null)
        where TStore : IObjectStore, IAppendableObjectStore =>
        ObjectLedgerStore.OverAppendable(
            store,
            options ?? new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append },
            _clock).Open(ledger, Window);

    private static byte[] FilledPayload(byte seed, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + (i & 0x1F));
        }

        return bytes;
    }

    private static ArcBuffer EncodeDataAndRollAppend(Slot firstSlot, byte[][] payloads, Slot rollNextSlot, long startOffset)
    {
        using var writer = new ArcBufferWriter();
        Span<byte> header = stackalloc byte[ObjectLedgerExtentFormat.FrameHeaderLength];
        for (var i = 0; i < payloads.Length; i++)
        {
            var slot = new Slot(firstSlot.Value + i);
            var payload = payloads[i];
            var crc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Data, slot, payload);
            ObjectLedgerExtentFormat.WriteFrameHeader(header, ObjectLedgerFrameKind.Data, slot, payload.Length, crc);
            writer.Write(header);
            writer.Write(payload);
        }

        var rollCrc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Roll, rollNextSlot, []);
        ObjectLedgerExtentFormat.WriteFrameHeader(header, ObjectLedgerFrameKind.Roll, rollNextSlot, 0, rollCrc);
        writer.Write(header);

        Span<byte> trailer = stackalloc byte[ObjectLedgerExtentFormat.TrailerLength];
        ObjectLedgerExtentFormat.WriteTrailer(trailer, firstSlot, payloads.Length + 1, startOffset);
        writer.Write(trailer);
        return writer.ConsumeSlice(writer.Length);
    }

    private static ArcBuffer EncodeSingleDataAppend(Slot slot, byte[] payload, long startOffset)
    {
        using var writer = new ArcBufferWriter();
        Span<byte> header = stackalloc byte[ObjectLedgerExtentFormat.FrameHeaderLength];
        var crc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Data, slot, payload);
        ObjectLedgerExtentFormat.WriteFrameHeader(header, ObjectLedgerFrameKind.Data, slot, payload.Length, crc);
        writer.Write(header);
        writer.Write(payload);
        Span<byte> trailer = stackalloc byte[ObjectLedgerExtentFormat.TrailerLength];
        ObjectLedgerExtentFormat.WriteTrailer(trailer, slot, count: 1, startOffset);
        writer.Write(trailer);
        return writer.ConsumeSlice(writer.Length);
    }

    private sealed class BlockingAppendStore : ForwardingObjectStore
    {
        private int _inFlight;
        private int _appendCount;

        public TaskCompletionSource FirstAppendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstAppend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxConcurrentAppends { get; private set; }

        public List<int> AppendLengths { get; } = [];

        public override async ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _inFlight);
            lock (AppendLengths)
            {
                if (current > MaxConcurrentAppends)
                {
                    MaxConcurrentAppends = current;
                }

                AppendLengths.Add(content.Length);
            }

            try
            {
                if (Interlocked.Increment(ref _appendCount) == 1)
                {
                    FirstAppendEntered.TrySetResult();
                    await ReleaseFirstAppend.Task.WaitAsync(cancellationToken);
                }

                return await base.AppendAsync(name, expectedLength, content, crc32c, cancellationToken);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed class EventRecordingStore : ForwardingObjectStore
    {
        public List<string> Events { get; } = [];

        public List<int> ReadLengths { get; } = [];

        public int ListCount { get; private set; }

        public int ReadCount => ReadLengths.Count;

        public void ResetCounts()
        {
            lock (Events)
            {
                Events.Clear();
                ReadLengths.Clear();
                ListCount = 0;
            }
        }

        public override ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            lock (Events)
            {
                Events.Add("create-appendable " + name.Value);
            }

            return base.CreateAppendableAsync(name, cancellationToken);
        }

        public override ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            var lead = content.Length > 0 ? content.AsReadOnlySequence().FirstSpan[0] : (byte)0;
            var tag = lead == (byte)ObjectLedgerFrameKind.Roll ? "append-roll " : "append-data ";
            lock (Events)
            {
                Events.Add(tag + name.Value);
            }

            return base.AppendAsync(name, expectedLength, content, crc32c, cancellationToken);
        }

        public override ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
        {
            lock (Events)
            {
                ReadLengths.Add(length);
            }

            return base.ReadAsync(name, offset, length, cancellationToken);
        }

        public override IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
        {
            lock (Events)
            {
                ListCount++;
            }

            return base.ListAsync(prefix, startAfter, cancellationToken);
        }
    }

    private sealed class LoseAnswerAfterLandingStore : ForwardingObjectStore
    {
        public bool FailFirstData { get; init; }

        public bool FailFirstRoll { get; init; }

        public bool FailedData { get; private set; }

        public bool FailedRoll { get; private set; }

        public override async ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            var isRoll = content.Length > 0 && content.AsReadOnlySequence().FirstSpan[0] == (byte)ObjectLedgerFrameKind.Roll;
            var outcome = await base.AppendAsync(name, expectedLength, content, crc32c, cancellationToken);
            if (!isRoll && FailFirstData && !FailedData)
            {
                FailedData = true;
                throw new IOException("Lost answer after DATA append landed.");
            }

            if (isRoll && FailFirstRoll && !FailedRoll)
            {
                FailedRoll = true;
                throw new IOException("Lost answer after ROLL append landed.");
            }

            return outcome;
        }
    }

    private sealed class ChangingAppendStore : ForwardingObjectStore
    {
        public bool FailAppend { get; init; }

        public bool FailTombstone { get; init; }

        public override ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            FailAppend
                ? ValueTask.FromException<ObjectAppendOutcome>(ObjectContentChangedException.For(name))
                : base.AppendAsync(name, expectedLength, content, crc32c, cancellationToken);

        public override ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            FailTombstone
                ? ValueTask.FromException(ObjectContentChangedException.For(name))
                : base.ReplaceWithTombstoneAsync(name, content, crc32c, cancellationToken);
    }

    private sealed class BlockingWalkReadStore : ForwardingObjectStore
    {
        private int _reads;

        public TaskCompletionSource FirstReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                FirstReadEntered.TrySetResult();
                await ReleaseFirstRead.Task.WaitAsync(cancellationToken);
            }

            return await base.ReadAsync(name, offset, length, cancellationToken);
        }
    }

    private sealed class FailingDroppedCheckStore : ForwardingObjectStore
    {
        public override ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            name.Value.EndsWith("/dropped", StringComparison.Ordinal)
                ? ValueTask.FromException<ObjectProperties?>(new IOException("Simulated dropped-check I/O failure."))
                : base.GetPropertiesAsync(name, cancellationToken);
    }

    private sealed class PruneDuringSettlementStore : ForwardingObjectStore
    {
        private int _armed;
        private int _failedOnce;
        private int _pausedOnce;

        public TaskCompletionSource TailReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseTailRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ArmLostAnswerAndPauseTailRead() => Volatile.Write(ref _armed, 1);

        public override async ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            var outcome = await base.AppendAsync(name, expectedLength, content, crc32c, cancellationToken);
            if (Volatile.Read(ref _armed) != 0 && Interlocked.Exchange(ref _failedOnce, 1) == 0)
            {
                throw new IOException("Lost answer after landing.");
            }

            return outcome;
        }

        public override async ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _failedOnce) != 0 && Interlocked.Exchange(ref _pausedOnce, 1) == 0)
            {
                TailReadEntered.TrySetResult();
                await ReleaseTailRead.Task.WaitAsync(cancellationToken);
            }

            return await base.ReadAsync(name, offset, length, cancellationToken);
        }
    }

    private sealed class BlockingFirstExtentReadStore : ForwardingObjectStore
    {
        private int _armed;
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ArmBlockOnRead() => Volatile.Write(ref _armed, 1);

        public override async ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) != 0)
            {
                ReadBlocked.TrySetResult();
                await _never.Task.WaitAsync(cancellationToken);
            }

            return await base.ReadAsync(name, offset, length, cancellationToken);
        }
    }
}
