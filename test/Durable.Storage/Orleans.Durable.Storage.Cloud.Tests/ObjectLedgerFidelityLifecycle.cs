using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// Shared end-to-end object-ledger lifecycle verification (object-ledgers.md §3.1, §7–§9) exercised across S3, S3 Express,
/// Google Cloud Storage, and Azure Blob backends: store record creation/re-read, windowed/coalesced writes, slot and
/// byte-range reads, <c>ReadRangeAsync</c>, open seal checks, takeover recovery, stale-writer fencing, and sweeper
/// reclamation.
/// </summary>
internal static class ObjectLedgerFidelityLifecycle
{
    private const int Window = 2;

    /// <summary>
    /// Verifies the full per-slot <see cref="ObjectLedgerStorage"/> lifecycle over <paramref name="store"/>.
    /// </summary>
    public static async Task VerifyPerSlotAsync(
        IObjectStore store,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var ledgerId = new LedgerId(17);
        var nextLedgerId = new LedgerId(18);
        var options = new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.PerSlot };
        var ledgers = ObjectLedgerStore.Over(store, options, clock);

        await VerifyStoreRecordAsync(ledgers, ObjectLedgerLayout.PerSlot, cancellationToken);

        byte[][] payloads =
        [
            Payload(0, 48),
            Payload(1, 96),
            Payload(2, 64),
            Payload(3, 128),
        ];

        await using var primary = new Ledger(ledgerId, ledgers.Open(ledgerId, Window), LedgerLocation.ObjectStore.Instance);
        await using var writer = primary.CreateWriter(clock);

        // Append slots 0 and 1 sequentially, then slots 2 and 3 pipelined within K = 2.
        Assert.Equal(Slot.Zero, await AppendAsync(writer, payloads[0], cancellationToken));
        Assert.Equal(new Slot(1), await AppendAsync(writer, payloads[1], cancellationToken));
        await AppendPipelinedPairAsync(writer, expectedFirstSlot: 2, payloads[2], payloads[3], cancellationToken);

        await VerifyRecoveryAndPostRecoveryAsync(store, ledgers, ledgerId, primary, writer, payloads, cancellationToken);

        // Sweep after revocation writes dropped, deletes all batch objects, preserves SEAL fence objects (OL-I8), and
        // answers Dropped on below-bound reads and ReadRangeAsync (OL-I7).
        var sweeper = new ObjectLedgerSweeper(ledgers);
        var swept = await sweeper.SweepAsync(id => id == ledgerId, nextLedgerId, cancellationToken);
        Assert.Equal(payloads.Length, swept);

        Assert.NotNull(await store.GetPropertiesAsync(ObjectLedgerNames.Dropped(ledgerId), cancellationToken));
        Assert.NotNull(await store.GetPropertiesAsync(ObjectLedgerNames.Sealed(ledgerId), cancellationToken));
        for (var i = 0; i < payloads.Length; i++)
        {
            Assert.Null(await store.GetPropertiesAsync(ObjectLedgerNames.Slot(ledgerId, new Slot(i)), cancellationToken));
        }

        for (var i = 0; i < Window; i++)
        {
            var sealProps = await store.GetPropertiesAsync(ObjectLedgerNames.Slot(ledgerId, new Slot(payloads.Length + i)), cancellationToken);
            Assert.NotNull(sealProps);
            Assert.Equal(0L, sealProps.Length);
        }

        await VerifyPostDropAnswersAsync(ledgers, ledgerId, payloads.Length, cancellationToken);
    }

    /// <summary>
    /// Verifies the full append-layout (<c>CLOE</c>) <see cref="AppendObjectLedgerStorage"/> lifecycle over
    /// <paramref name="store"/>, including pipelined coalesced writes, extent rotation (<c>ROLL</c>), recovery seal,
    /// and tombstone sweep.
    /// </summary>
    public static async Task VerifyAppendAsync<TStore>(
        TStore store,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
        where TStore : IObjectStore, IAppendableObjectStore
    {
        var clock = timeProvider ?? TimeProvider.System;
        var ledgerId = new LedgerId(19);
        var nextLedgerId = new LedgerId(20);
        var options = new ObjectLedgerStoreOptions
        {
            Layout = ObjectLedgerLayout.Append,
            ExtentTargetBytes = 128,
            ExtentMaxAppends = 2,
        };
        var ledgers = ObjectLedgerStore.OverAppendable(store, options, clock);

        await VerifyStoreRecordAsync(ledgers, ObjectLedgerLayout.Append, cancellationToken);

        byte[][] payloads =
        [
            Payload(10, 64),
            Payload(11, 80),
            Payload(12, 96),
            Payload(13, 72),
            Payload(14, 112),
        ];

        await using var primary = new Ledger(ledgerId, ledgers.Open(ledgerId, Window), LedgerLocation.ObjectStore.Instance);
        await using var writer = primary.CreateWriter(clock);

        // Pipeline concurrent pairs within K = 2 to exercise append coalescing across real Azure/Azurite, followed by a
        // fifth slot forcing extent rotation.
        await AppendPipelinedPairAsync(writer, expectedFirstSlot: 0, payloads[0], payloads[1], cancellationToken);
        await AppendPipelinedPairAsync(writer, expectedFirstSlot: 2, payloads[2], payloads[3], cancellationToken);
        Assert.Equal(new Slot(4), await AppendAsync(writer, payloads[4], cancellationToken));

        // Low ExtentTargetBytes / ExtentMaxAppends guarantees at least two extents were created.
        var extents = await store.ListAsync(ObjectLedgerNames.Extents(ledgerId), cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken);
        Assert.True(extents.Count >= 2, $"Expected extent rotation to produce at least 2 extents, found {extents.Count}.");

        await VerifyRecoveryAndPostRecoveryAsync(store, ledgers, ledgerId, primary, writer, payloads, cancellationToken);

        // Sweeper creates dropped, preserves sealed, deletes every older extent, replaces the newest extent with a
        // non-appendable TombstoneLength block blob with CRC-32C (OL-I8), and causes later reads to answer Dropped (OL-I7).
        var sweeper = new ObjectLedgerSweeper(ledgers);
        var swept = await sweeper.SweepAsync(id => id == ledgerId, nextLedgerId, cancellationToken);
        Assert.Equal(extents.Count - 1, swept);

        Assert.NotNull(await store.GetPropertiesAsync(ObjectLedgerNames.Dropped(ledgerId), cancellationToken));
        Assert.NotNull(await store.GetPropertiesAsync(ObjectLedgerNames.Sealed(ledgerId), cancellationToken));
        for (var i = 1; i < extents.Count; i++)
        {
            Assert.Null(await store.GetPropertiesAsync(extents[i].Name, cancellationToken));
        }

        var tombstoneProps = await store.GetPropertiesAsync(extents[0].Name, cancellationToken);
        Assert.NotNull(tombstoneProps);
        Assert.Equal(ObjectLedgerExtentFormat.TombstoneLength, tombstoneProps.Length);
        Assert.NotNull(tombstoneProps.Crc32c);
        Assert.Equal<ObjectSealOutcome>(ObjectSealOutcome.NotAppendable.Instance, await store.SealAsync(extents[0].Name, cancellationToken));

        await VerifyPostDropAnswersAsync(ledgers, ledgerId, payloads.Length, cancellationToken);
    }

    private static async Task VerifyStoreRecordAsync(
        ObjectLedgerStore ledgers,
        ObjectLedgerLayout expectedLayout,
        CancellationToken cancellationToken)
    {
        var createdRecord = await ledgers.StoreRecordAsync(cancellationToken);
        Assert.Equal(expectedLayout, createdRecord.Layout);
        Assert.Equal(createdRecord, await ledgers.StoreRecordAsync(cancellationToken));
        Assert.Equal(createdRecord, await ledgers.ReadStoreRecordAsync(cancellationToken));
    }

    private static async Task VerifyRecoveryAndPostRecoveryAsync(
        IObjectStore store,
        ObjectLedgerStore ledgers,
        LedgerId ledgerId,
        Ledger primary,
        LedgerWriter writer,
        byte[][] payloads,
        CancellationToken cancellationToken)
    {
        // Open check from another process sees an unsealed ledger.
        await using var observer = new Ledger(ledgerId, ledgers.Open(ledgerId, Window), LedgerLocation.ObjectStore.Instance);
        var openCheck = Assert.IsType<LedgerTail>((await observer.ReadTailAsync(cancellationToken)).Value);
        Assert.False(openCheck.IsSealed);
        Assert.InRange(openCheck.ContiguousTail.Value, 0L, payloads.Length);

        // Verify slot reads, byte-range reads, outside-batch reads, and ReadRangeAsync before recovery.
        await VerifyReadsAsync(primary, payloads, cancellationToken);

        // Recovery from another process seals the ledger and returns T = payloads.Length.
        await using var taker = new Ledger(ledgerId, ledgers.Open(ledgerId, Window), LedgerLocation.ObjectStore.Instance);
        var recovered = Assert.IsType<RecoveryOutcome.Recovered>((await taker.RecoverAsync(cancellationToken)).Value);
        Assert.Equal(new Slot(payloads.Length), recovered.End);
        Assert.NotNull(await store.GetPropertiesAsync(ObjectLedgerNames.Sealed(ledgerId), cancellationToken));

        // Stale primary write meets the seal and stops.
        using (var stale = ArcBuffer.Wrap(Payload(99, 32)))
        {
            var staleSlot = await writer.AcquireAsync(cancellationToken);
            Assert.NotNull(staleSlot);
            var staleOutcome = await writer.AppendAsync(staleSlot.Value, stale, cancellationToken);
            Assert.IsType<AppendOutcome.Sealed>(staleOutcome.Value);
        }

        // Both primary and observer report sealed; reads on taker serve every slot 0..T-1.
        var primaryCheck = Assert.IsType<LedgerTail>((await primary.ReadTailAsync(cancellationToken)).Value);
        Assert.True(primaryCheck.IsSealed);
        Assert.InRange(primaryCheck.ContiguousTail.Value, 0L, payloads.Length);

        var sealedCheck = Assert.IsType<LedgerTail>((await observer.ReadTailAsync(cancellationToken)).Value);
        Assert.True(sealedCheck.IsSealed);
        Assert.InRange(sealedCheck.ContiguousTail.Value, 0L, payloads.Length);

        taker.ObserveRecordedTail(recovered.End);
        await VerifyReadsAsync(taker, payloads, cancellationToken);
    }

    private static async Task VerifyPostDropAnswersAsync(
        ObjectLedgerStore ledgers,
        LedgerId ledgerId,
        int payloadCount,
        CancellationToken cancellationToken)
    {
        await using var afterDrop = new Ledger(ledgerId, ledgers.Open(ledgerId, Window), LedgerLocation.ObjectStore.Instance);
        afterDrop.ObserveRecordedTail(new Slot(payloadCount));
        Assert.IsType<ReplicaWriteOutcome.Dropped>((await afterDrop.ReadAsync(Slot.Zero, cancellationToken)).Value);
        Assert.IsType<ReplicaWriteOutcome.Dropped>((await afterDrop.RecoverAsync(cancellationToken)).Value);

        var rangeAnswers = new List<ReplicaRead>();
        await foreach (var read in afterDrop.ReadRangeAsync(Slot.Zero, new Slot(payloadCount), cancellationToken: cancellationToken))
        {
            rangeAnswers.Add(read);
        }

        var rangeAnswer = Assert.Single(rangeAnswers);
        Assert.IsType<ReplicaWriteOutcome.Dropped>(rangeAnswer.Value);
    }

    private static async Task AppendPipelinedPairAsync(
        LedgerWriter writer,
        long expectedFirstSlot,
        byte[] firstPayload,
        byte[] secondPayload,
        CancellationToken cancellationToken)
    {
        var firstSlot = await writer.AcquireAsync(cancellationToken);
        var secondSlot = await writer.AcquireAsync(cancellationToken);
        Assert.NotNull(firstSlot);
        Assert.NotNull(secondSlot);
        Assert.Equal(new Slot(expectedFirstSlot), firstSlot.Value);
        Assert.Equal(new Slot(expectedFirstSlot + 1), secondSlot.Value);

        // Submit the second slot first so in append layout the coalescer batches both slots into one append.
        static async Task<AppendOutcome> SendAsync(LedgerWriter w, Slot s, byte[] bytes, CancellationToken ct)
        {
            using var buffer = ArcBuffer.Wrap(bytes);
            return await w.AppendAsync(s, buffer, ct);
        }

        var secondTask = SendAsync(writer, secondSlot.Value, secondPayload, cancellationToken);
        var firstTask = SendAsync(writer, firstSlot.Value, firstPayload, cancellationToken);
        var outcomes = await Task.WhenAll(firstTask, secondTask);
        Assert.All(outcomes, static outcome => Assert.IsType<AppendOutcome.Appended>(outcome.Value));
    }

    private static async Task<Slot> AppendAsync(LedgerWriter writer, byte[] bytes, CancellationToken cancellationToken)
    {
        var slot = await writer.AcquireAsync(cancellationToken);
        Assert.NotNull(slot);
        using var payload = ArcBuffer.Wrap(bytes);
        var outcome = await writer.AppendAsync(slot.Value, payload, cancellationToken);
        Assert.IsType<AppendOutcome.Appended>(outcome.Value);
        return slot.Value;
    }

    private static async Task VerifyReadsAsync(Ledger ledger, byte[][] payloads, CancellationToken cancellationToken)
    {
        for (var i = 0; i < payloads.Length; i++)
        {
            var read = await ledger.ReadAsync(new Slot(i), cancellationToken);
            using var batch = Assert.IsType<ArcBuffer>(read.Value);
            Assert.Equal(payloads[i], batch.ToArray());

            var sliceRead = await ledger.ReadAsync(new Slot(i), offset: 7, length: 19, cancellationToken);
            using var slice = Assert.IsType<ArcBuffer>(sliceRead.Value);
            Assert.Equal(payloads[i].AsSpan(7, 19).ToArray(), slice.ToArray());

            var outside = await ledger.ReadAsync(new Slot(i), offset: payloads[i].Length, length: 1, cancellationToken);
            Assert.IsType<ReplicaRead.OutsideBatch>(outside.Value);
        }

        Assert.IsType<ReplicaRead.Missing>((await ledger.ReadAsync(new Slot(payloads.Length), cancellationToken)).Value);

        var index = 0;
        await foreach (var read in ledger.ReadRangeAsync(Slot.Zero, new Slot(payloads.Length), cancellationToken: cancellationToken))
        {
            using var batch = Assert.IsType<ArcBuffer>(read.Value);
            Assert.Equal(payloads[index], batch.ToArray());
            index++;
        }

        Assert.Equal(payloads.Length, index);
    }

    private static byte[] Payload(byte seed, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = unchecked((byte)(seed * 31 + i * 7 + 1));
        }

        return bytes;
    }
}
