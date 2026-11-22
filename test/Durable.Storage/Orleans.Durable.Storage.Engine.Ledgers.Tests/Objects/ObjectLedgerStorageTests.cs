using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Objects;

/// <summary>
/// The per-slot storage (object-ledgers.md §2.1, §8): writes settled by their occupant (OL-I2), reads which never answer
/// <c>Missing</c> below the bound (OL-I7), the recovery's fence (OL-I3), the drop marker read by every answer a sweep may
/// change (OL-I4, LL-I9), and its lifecycle (ledger-protocol.md §6.3).
/// </summary>
public sealed class ObjectLedgerStorageTests : IDisposable
{
    private const int Window = 2;
    private static readonly LedgerId Id = new(7);
    private static readonly LedgerId Next = new(8);
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// object-ledgers.md §1, object-store.md §2.4: a recovery's SEAL create which is held past the sweep lands on a slot the
    /// sweep emptied, re-creating its name empty. Every read of the slot, from a reader whose stale map still bounds it
    /// above the slot, and every recovery after the sweep, answers the drop (LL-I9); a write meets the SEAL or its fence;
    /// and a later sweep keeps the SEAL.
    /// </summary>
    [Fact]
    public async Task ASealWhichLandsAfterTheSweepReadsAsTheDrop()
    {
        var store = new HoldingStore();
        var ledgers = ObjectLedgerStore.OverAppendable(store, new ObjectLedgerStoreOptions(), TimeProvider.System);
        await using var writer = ledgers.Open(Id, Window);
        await using var recovery = ledgers.Open(Id, Window);

        // The recovery's SEAL at slot 0 is held, and the writer's batch lands there before the recovery repeats.
        store.Holding = async () => Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(writer, 0, 1));
        var recovered = Assert.IsType<LedgerRecovery>((await recovery.RecoverAsync(Slot.Zero, Token)).Value);
        Assert.Equal(new Slot(1), recovered.End);

        Assert.Equal(1, await new ObjectLedgerSweeper(ledgers).SweepAsync(static id => id == Id, Next, Token));
        await store.LandHeldAsync();
        Assert.Equal(0, (await store.Inner.GetPropertiesAsync(ObjectLedgerNames.Slot(Id, Slot.Zero), Token))!.Length);

        await using var reader = ledgers.Open(Id, Window);
        Assert.True((await reader.ReadAsync(Slot.Zero, new Slot(1), Token)).TryGetValue(out ReplicaWriteOutcome.Dropped? _));
        Assert.True((await reader.ReadAsync(Slot.Zero, 0, 1, new Slot(1), Token)).TryGetValue(out ReplicaWriteOutcome.Dropped? _));
        Assert.Equal<LedgerRecoveryOutcome>(ReplicaWriteOutcome.Dropped.Instance, await reader.RecoverAsync(new Slot(1), Token));
        Assert.Equal(new LedgerTail(new Slot(1), IsSealed: true), (await reader.CheckAsync(new Slot(1), Token)).Value);

        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Sealed.Instance, await WriteAsync(writer, 0, 1));
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Sealed.Instance, await WriteAsync(writer, 1, 2));
        Assert.Equal(0, await new ObjectLedgerSweeper(ledgers).SweepAsync(static id => id == Id, Next, Token));
    }

    /// <summary>OL-I2: a create which meets an occupant reads it: a SEAL answers sealed, its own batch written, other bytes corruption.</summary>
    [Fact]
    public async Task AWriteSettlesTheOccupantItMeets()
    {
        var store = new ForwardingObjectStore();
        await using var storage = Open(store);
        await CreateAsync(store, 0, []);
        await CreateAsync(store, 1, Batch(1));
        await CreateAsync(store, 2, Batch(9));

        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Sealed.Instance, await WriteAsync(storage, 0, 1));
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 1, 1));
        _ = await Assert.ThrowsAsync<LedgerStorageCorruptException>(async () => await WriteAsync(storage, 2, 1));
    }

    /// <summary>
    /// OL-I7: below the bound a read answers the slot's batch, or the drop once the marker exists, and an absent batch
    /// otherwise is corruption; at or above it, an absent slot answers <c>Missing</c>. A batch which fails its CRC-32C is
    /// corruption, and a byte range past the batch answers <c>OutsideBatch</c>.
    /// </summary>
    [Fact]
    public async Task AReadBelowTheBoundAnswersTheBatchOrTheDropAndNeverMissing()
    {
        var store = new ForwardingObjectStore();
        await using var storage = Open(store);
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 0, 1));

        var read = await storage.ReadAsync(Slot.Zero, new Slot(1), Token);
        Assert.True(read.TryGetValue(out ArcBuffer batch));
        using (batch)
        {
            Assert.Equal(Batch(1), batch.ToArray());
        }

        var range = await storage.ReadAsync(Slot.Zero, 1, 2, new Slot(1), Token);
        Assert.True(range.TryGetValue(out ArcBuffer part));
        using (part)
        {
            Assert.Equal(Batch(1)[1..3], part.ToArray());
        }

        Assert.Equal(new ReplicaRead.OutsideBatch(Batch(1).Length), (await storage.ReadAsync(Slot.Zero, 1, Batch(1).Length, new Slot(1), Token)).Value);
        Assert.True((await storage.ReadAsync(new Slot(1), new Slot(1), Token)).TryGetValue(out ReplicaRead.Missing? _));
        _ = await Assert.ThrowsAsync<LedgerStorageCorruptException>(async () => await storage.ReadAsync(new Slot(1), new Slot(2), Token));

        _ = await store.Inner.CreateAsync(ObjectLedgerNames.Dropped(Id), default, crc32c: null, Token);
        Assert.True((await storage.ReadAsync(new Slot(1), new Slot(2), Token)).TryGetValue(out ReplicaWriteOutcome.Dropped? _));

        var flipped = new ChecksumFlippingStore();
        await using var corrupt = Open(flipped);
        await CreateAsync(flipped, 0, Batch(1));
        _ = await Assert.ThrowsAsync<LedgerStorageCorruptException>(async () => await corrupt.ReadAsync(Slot.Zero, new Slot(1), Token));
    }

    /// <summary>
    /// OL-I3, OL-I6: a recovery which finds every slot of its window filled by the writer first lists again above it, and
    /// ends the ledger at the first slot without a batch; a second recovery answers the same end.
    /// </summary>
    [Fact]
    public async Task ARecoveryWhichFindsItsWindowFilledListsAgainAboveIt()
    {
        var store = new OutrunningStore(below: Window);
        await using var storage = Open(store);

        var recovered = Assert.IsType<LedgerRecovery>((await storage.RecoverAsync(Slot.Zero, Token)).Value);
        Assert.Equal(new Slot(Window), recovered.End);
        await using var again = Open(store);
        Assert.Equal(new LedgerRecovery(new Slot(Window), new Slot(Window)), (await again.RecoverAsync(Slot.Zero, Token)).Value);
        Assert.Equal(0, (await store.Inner.GetPropertiesAsync(ObjectLedgerNames.Slot(Id, new Slot(Window)), Token))!.Length);
    }

    /// <summary>
    /// OL-I3, LL-I4: when the writer wins the window's first slot, the end lies above it, and the recovery fences the
    /// window above the end too, so a write the writer pipelined past the end after the recovery meets a SEAL.
    /// </summary>
    [Fact]
    public async Task ARecoveryFencesTheWindowAboveItsEnd()
    {
        var store = new OutrunningStore(below: 1);
        await using var storage = Open(store);

        Assert.Equal(new LedgerRecovery(new Slot(1), new Slot(1)), (await storage.RecoverAsync(Slot.Zero, Token)).Value);
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Sealed.Instance, await WriteAsync(storage, 1, 1));
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Sealed.Instance, await WriteAsync(storage, 2, 1));
    }

    /// <summary>
    /// OL-I3: a recovery of a ledger whose batches reach the greatest slot a writer writes ends it at <c>2^63 − 1</c>,
    /// with no window to fence above it, and a recovery which carries that end answers it again.
    /// </summary>
    [Fact]
    public async Task ARecoveryAtTheGreatestSlotEndsWithoutOverflow()
    {
        var store = new ForwardingObjectStore();
        await using var storage = Open(store);
        await CreateAsync(store, ObjectLedgerNames.MaxSlot - 1, Batch(1));
        await CreateAsync(store, ObjectLedgerNames.MaxSlot, Batch(2));
        var end = new Slot(long.MaxValue);

        Assert.Equal(new LedgerRecovery(end, end), (await storage.RecoverAsync(Slot.Zero, Token)).Value);
        await using var again = Open(store);
        Assert.Equal(new LedgerRecovery(end, end), (await again.RecoverAsync(end, Token)).Value);
    }

    /// <summary>
    /// ledger-protocol.md §8.2: a learn sends no request and answers the greater of what it carries and the bound the
    /// storage holds; stopped reads refuse it.
    /// </summary>
    [Fact]
    public async Task ALearnAnswersTheBoundWithNoRequest()
    {
        var store = new CountingStore();
        await using var storage = Open(store);
        Assert.Equal(new Slot(3), await storage.LearnTailAsync(new Slot(3), Token));
        Assert.Equal(new Slot(3), await storage.LearnTailAsync(new Slot(1), Token));
        Assert.Equal(0, store.Properties);

        await storage.StopReadsAsync();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await storage.LearnTailAsync(Slot.Zero, Token));
    }

    /// <summary>ledger-protocol.md §6.3: concurrent disposals, with a stop among them, all complete and end a write in flight.</summary>
    [Fact]
    public async Task ConcurrentDisposalsCompleteTogether()
    {
        var store = new BlockingStore();
        var storage = Open(store);
        var write = WriteAsync(storage, 0, 1);
        await store.Blocked.Task;

        await Task.WhenAll(
            Task.Run(async () => await storage.DisposeAsync(), Token),
            Task.Run(async () => await storage.DisposeAsync(), Token),
            Task.Run(storage.StopReadsAsync, Token));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => write);
        await storage.DisposeAsync();
    }

    /// <summary>ledger-protocol.md §6.3: stopped reads refuse reads, checks, and recoveries while writes go on; disposal refuses writes.</summary>
    [Fact]
    public async Task StoppedReadsAndADisposedStorageRefuseTheirCalls()
    {
        var storage = Open(new ForwardingObjectStore());
        await storage.StopReadsAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await storage.ReadAsync(Slot.Zero, Slot.Zero, Token));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await storage.CheckAsync(Slot.Zero, Token));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await storage.RecoverAsync(Slot.Zero, Token));
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 0, 1));

        await storage.DisposeAsync();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await WriteAsync(storage, 1, 1));
    }

    /// <summary>
    /// conventions.md §14: a batch which changed after its CRC-32C was computed is a defect, reported once and never
    /// repeated, so the writer cannot write the changed bytes under a checksum computed afresh.
    /// </summary>
    [Fact]
    public async Task AChangedBatchIsReportedAsADefectAndNeverRepeated()
    {
        var store = new ChangingStore();
        await using var storage = Open(store);
        var since = FatalErrors.ReportedCount;

        var changed = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await WriteAsync(storage, 0, 1));

        var report = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal(FatalErrorSite.Defect, report.Site);
        Assert.Same(changed, report.Failure.Exception);
        Assert.False(StorageOutageRetry.IsRepeatable(changed, CancellationToken.None));
        Assert.True(LedgerReplicaFailures.IsTransient(changed, CancellationToken.None));
        Assert.Equal(1, store.Creates);

        // The writer which holds the slot stops its lifetime rather than repeat the write (LP-I13).
#pragma warning disable CA2000 // Ownership of the storage transfers to the ledger.
        await using var ledger = new Ledger(Id, Open(store), LedgerLocation.ObjectStore.Instance);
#pragma warning restore CA2000
        await using var writer = ledger.CreateWriter(TimeProvider.System);
        using var payload = ArcBuffer.Wrap(Batch(1));
        _ = Assert.IsType<AppendOutcome.Unresolved>((await writer.AppendAsync(payload, Token)).Value);
        Assert.True(writer.Sequencer.IsStopped);
        Assert.Equal(2, store.Creates);
        _ = FatalErrors.TakeMine(since);
    }

    /// <summary>OL-I6, LL-I9: a sweep which drops the ledger while a recovery places its fence makes the recovery answer the drop.</summary>
    [Fact]
    public async Task ARecoveryWhichADropOvertakesAnswersTheDrop()
    {
        var store = new DroppingStore();
        await using var storage = Open(store);

        Assert.Equal<LedgerRecoveryOutcome>(ReplicaWriteOutcome.Dropped.Instance, await storage.RecoverAsync(Slot.Zero, Token));
        Assert.NotNull(await store.Inner.GetPropertiesAsync(ObjectLedgerNames.Sealed(Id), Token));
    }

    /// <summary>
    /// OL-I7: once the ledger is dropped, a batch a delayed write re-created at or above the bound answers the drop, whole
    /// or by range; a batch which vanishes between its properties and its bytes answers the drop, and below the bound of a
    /// ledger not dropped is corruption, as is an object longer than any batch.
    /// </summary>
    [Fact]
    public async Task ReadsWhichASweepRacesAnswerTheDropOrCorruption()
    {
        var store = new ForwardingObjectStore();
        await using var storage = Open(store);
        await CreateAsync(store, 3, Batch(3));
        _ = await store.Inner.CreateAsync(ObjectLedgerNames.Dropped(Id), default, crc32c: null, Token);
        Assert.True((await storage.ReadAsync(new Slot(3), new Slot(1), Token)).TryGetValue(out ReplicaWriteOutcome.Dropped? _));
        Assert.True((await storage.ReadAsync(new Slot(3), 0, 1, new Slot(1), Token)).TryGetValue(out ReplicaWriteOutcome.Dropped? _));

        var vanishing = new VanishingStore();
        await using var racing = Open(vanishing);
        await CreateAsync(vanishing, 0, Batch(1));
        _ = await Assert.ThrowsAsync<LedgerStorageCorruptException>(async () => await racing.ReadAsync(Slot.Zero, new Slot(1), Token));
        _ = await vanishing.Inner.CreateAsync(ObjectLedgerNames.Dropped(Id), default, crc32c: null, Token);
        Assert.True((await racing.ReadAsync(Slot.Zero, new Slot(1), Token)).TryGetValue(out ReplicaWriteOutcome.Dropped? _));

        var huge = new HugeStore();
        await using var oversized = Open(huge);
        await CreateAsync(huge, 0, Batch(1));
        _ = await Assert.ThrowsAsync<LedgerStorageCorruptException>(async () => await oversized.ReadAsync(Slot.Zero, new Slot(1), Token));
    }

    /// <summary>object-ledgers.md §8.4: a byte-range read of a batch is one ranged read, with no properties read.</summary>
    [Fact]
    public async Task AByteRangeReadOfABatchIsOneRequest()
    {
        var store = new CountingStore();
        await using var storage = Open(store);
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 0, 1));
        store.Properties = 0;

        var range = await storage.ReadAsync(Slot.Zero, 1, 2, new Slot(1), Token);
        Assert.True(range.TryGetValue(out ArcBuffer part));
        part.Dispose();
        Assert.Equal(0, store.Properties);
    }

    /// <summary>
    /// OL-I2: a create which answers that the object exists, which then reads as absent, is repeated, by a write and by a
    /// recovery's SEAL alike.
    /// </summary>
    [Fact]
    public async Task ACreateWhoseOccupantVanishesIsRepeated()
    {
        var store = new PhantomStore();
        await using var storage = Open(store);

        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 0, 1));
        Assert.Equal(new LedgerRecovery(new Slot(1), new Slot(1)), (await storage.RecoverAsync(new Slot(1), Token)).Value);
        Assert.True(store.Phantoms >= 2);
    }

    /// <summary>OL-I7: a listing which names an object no slot names is corruption, which the recovery does not repeat.</summary>
    [Fact]
    public async Task ARecoveryRefusesAListingWhichNamesNoSlot()
    {
        var store = new ForwardingObjectStore();
        await using var storage = Open(store);
        _ = await store.Inner.CreateAsync(new ObjectName(ObjectLedgerNames.Slots(Id) + "stray"), default, crc32c: null, Token);

        _ = await Assert.ThrowsAsync<LedgerStorageCorruptException>(async () => await storage.RecoverAsync(Slot.Zero, Token));
    }

    /// <summary>
    /// ledger-protocol.md §6.3: a stop ends a read in flight with <see cref="ObjectDisposedException"/>, and disposal a
    /// write in flight; a caller's own cancellation ends either with <see cref="OperationCanceledException"/>.
    /// </summary>
    [Fact]
    public async Task AStopEndsCallsInFlightAndACallersCancellationIsItsOwn()
    {
        var store = new BlockingStore();
        await using var storage = Open(store);

        var check = storage.CheckAsync(Slot.Zero, Token).AsTask();
        await store.Blocked.Task;
        await storage.StopReadsAsync();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => check);

        store.Reset();
        using var cancelled = new CancellationTokenSource();
        var write = WriteUnderAsync(storage, 0, 1, cancelled.Token);
        await store.Blocked.Task;
        await cancelled.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);

        store.Reset();
        var disposedWrite = WriteAsync(storage, 1, 1);
        await store.Blocked.Task;
        await Task.WhenAll(storage.DisposeAsync().AsTask(), storage.StopReadsAsync());
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => disposedWrite);
    }

    private static byte[] Batch(byte value) => [value, (byte)(value + 1), (byte)(value + 2), (byte)(value + 3)];

    private static ILedgerStorage Open(IObjectStore store) =>
        ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions(), TimeProvider.System).Open(Id, Window);

    private static Task<LedgerWriteOutcome> WriteAsync(ILedgerStorage storage, long slot, byte value) => WriteUnderAsync(storage, slot, value, Token);

    private static async Task<LedgerWriteOutcome> WriteUnderAsync(ILedgerStorage storage, long slot, byte value, CancellationToken cancellationToken)
    {
        using var payload = ArcBuffer.Wrap(Batch(value));
        return await storage.WriteAsync(new Slot(slot), payload, Slot.Zero, cancellationToken);
    }

    private static async Task CreateAsync(ForwardingObjectStore store, long slot, byte[] content)
    {
        using var buffer = ArcBuffer.Wrap(content);
        _ = await store.Inner.CreateAsync(ObjectLedgerNames.Slot(Id, new Slot(slot)), content.Length == 0 ? default : buffer, crc32c: null, Token);
    }

    /// <summary>Holds the first SEAL create at slot 0: it runs <see cref="Holding"/>, then fails, and lands only when asked.</summary>
    private sealed class HoldingStore : ForwardingObjectStore
    {
        private ObjectName? _held;

        public Func<Task>? Holding { get; set; }

        public override async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            if (_held is null && content.Length == 0 && name == ObjectLedgerNames.Slot(Id, Slot.Zero))
            {
                _held = name;
                await Holding!();
                throw new IOException("The create's answer was lost; the create is held.");
            }

            return await base.CreateAsync(name, content, crc32c, cancellationToken);
        }

        public async Task LandHeldAsync() => _ = await Inner.CreateAsync(_held!, default, crc32c: null, Token);
    }

    /// <summary>A writer which lands its batch at each slot below <c>below</c> just before the recovery's SEAL create there.</summary>
    private sealed class OutrunningStore(int below) : ForwardingObjectStore
    {
        public override async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            if (content.Length == 0 && ObjectLedgerNames.ParseSlot(Id, name) is { } slot && slot.Value < below)
            {
                using var batch = ArcBuffer.Wrap(Batch((byte)(slot.Value + 1)));
                _ = await Inner.CreateAsync(name, batch, crc32c: null, cancellationToken);
            }

            return await base.CreateAsync(name, content, crc32c, cancellationToken);
        }
    }

    /// <summary>Refuses every create's content as changed after verification, counting the creates.</summary>
    private sealed class ChangingStore : ForwardingObjectStore
    {
        public int Creates { get; private set; }

        public override ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            Creates++;
            return ValueTask.FromException<ObjectCreateOutcome>(ObjectContentChangedException.For(name));
        }
    }

    /// <summary>Drops the ledger, as a sweep's first step does, when the recovery places its first SEAL.</summary>
    private sealed class DroppingStore : ForwardingObjectStore
    {
        public override async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            if (ObjectLedgerNames.ParseSlot(Id, name) is not null)
            {
                _ = await Inner.CreateAsync(ObjectLedgerNames.Dropped(Id), default, crc32c: null, cancellationToken);
            }

            return await base.CreateAsync(name, content, crc32c, cancellationToken);
        }
    }

    /// <summary>Answers every slot's whole read as shorter than its properties, as an object a sweep deleted between them is.</summary>
    private sealed class VanishingStore : ForwardingObjectStore
    {
        public override ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            ObjectLedgerNames.ParseSlot(Id, name) is not null
                ? ValueTask.FromException<ArcBuffer?>(new EndOfStreamException("The object is shorter than the range."))
                : base.ReadAsync(name, offset, length, cancellationToken);
    }

    /// <summary>Reports every slot's object longer than any batch.</summary>
    private sealed class HugeStore : ForwardingObjectStore
    {
        public override async ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            await base.GetPropertiesAsync(name, cancellationToken) is { } properties && ObjectLedgerNames.ParseSlot(Id, name) is not null
                ? properties with { Length = int.MaxValue + 1L }
                : await base.GetPropertiesAsync(name, cancellationToken);
    }

    /// <summary>Counts properties reads.</summary>
    private sealed class CountingStore : ForwardingObjectStore
    {
        public int Properties { get; set; }

        public override ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            Properties++;
            return base.GetPropertiesAsync(name, cancellationToken);
        }
    }

    /// <summary>Answers each slot's first create as existing without creating it, as a create racing a sweep's delete may.</summary>
    private sealed class PhantomStore : ForwardingObjectStore
    {
        private readonly HashSet<ObjectName> _answered = [];

        public int Phantoms { get; private set; }

        public override ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            if (ObjectLedgerNames.ParseSlot(Id, name) is not null && _answered.Add(name))
            {
                Phantoms++;
                return ValueTask.FromResult<ObjectCreateOutcome>(ObjectCreateOutcome.Exists.Instance);
            }

            return base.CreateAsync(name, content, crc32c, cancellationToken);
        }
    }

    /// <summary>Holds each properties read and create until its token is cancelled, telling the test it is blocked.</summary>
    private sealed class BlockingStore : ForwardingObjectStore
    {
        public TaskCompletionSource Blocked { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Reset() => Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            await BlockAsync(cancellationToken);
            return null;
        }

        public override async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            await BlockAsync(cancellationToken);
            return ObjectCreateOutcome.Created.Instance;
        }

        private async Task BlockAsync(CancellationToken cancellationToken)
        {
            _ = Blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
        }
    }

    /// <summary>Reports every object's CRC-32C with its low bit flipped.</summary>
    private sealed class ChecksumFlippingStore : ForwardingObjectStore
    {
        public override async ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            await base.GetPropertiesAsync(name, cancellationToken) is { Crc32c: { } crc } properties ? properties with { Crc32c = crc ^ 1 } : null;
    }
}
