using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// Shared <see cref="IObjectStore"/>-level object-ledger precondition checks (object-ledgers.md §3.1) reused across
/// S3, S3 Express One Zone, Google Cloud Storage, and Azure Blob fidelity suites.
/// </summary>
internal static class ObjectLedgerPreconditionChecks
{
    private const int ConcurrentRacers = 8;

    /// <summary>
    /// Races <see cref="ConcurrentRacers"/> concurrent conditional creates at one slot object: at most one answers
    /// <see cref="ObjectCreateOutcome.Created"/> (exactly one on local/emulator backends; 0 or 1 on live cloud where a
    /// retried winner can observe 412), all others answer <see cref="ObjectCreateOutcome.Exists"/> (or an accepted
    /// provider conditional-conflict error), and the landed bytes match one submitted payload (the
    /// <see cref="ObjectCreateOutcome.Created"/> racer's payload when one observed it).
    /// </summary>
    public static async Task VerifyConcurrentCreatesAsync(
        IObjectStore store,
        ObjectName slotName,
        bool allowZeroCreatedOnRetry,
        Func<Exception, bool>? isTransientConflict,
        CancellationToken cancellationToken)
    {
        var payloads = Enumerable.Range(0, ConcurrentRacers)
            .Select(i => MakePayload((byte)(100 + i), 96 + i))
            .ToArray();

        using var start = new ManualResetEventSlim(false);
        var tasks = new Task<(int Index, bool Created, bool Loser)>[ConcurrentRacers];
        for (var i = 0; i < ConcurrentRacers; i++)
        {
            var index = i;
            var payload = payloads[i];
            tasks[i] = Task.Run(async () =>
            {
                start.Wait(cancellationToken);
                using var content = ArcBuffer.Wrap(payload);
                try
                {
                    var outcome = await store.CreateAsync(slotName, content, ObjectStoreChecksums.Crc32C(content), cancellationToken);
                    return outcome switch
                    {
                        ObjectCreateOutcome.Created => (index, Created: true, Loser: false),
                        ObjectCreateOutcome.Exists => (index, Created: false, Loser: true),
                        _ => throw new InvalidOperationException($"Unexpected create outcome {outcome}."),
                    };
                }
                catch (Exception ex) when (isTransientConflict is not null && isTransientConflict(ex))
                {
                    return (index, Created: false, Loser: true);
                }
            }, cancellationToken);
        }

        start.Set();
        var results = await Task.WhenAll(tasks);

        var createdIndices = results.Where(static r => r.Created).Select(static r => r.Index).ToList();
        var loserCount = results.Count(static r => r.Loser);

        Assert.Equal(ConcurrentRacers, createdIndices.Count + loserCount);
        if (allowZeroCreatedOnRetry)
        {
            Assert.InRange(createdIndices.Count, 0, 1);
        }
        else
        {
            Assert.Single(createdIndices);
        }

        var properties = await store.GetPropertiesAsync(slotName, cancellationToken);
        Assert.NotNull(properties);

        var read = await store.ReadAsync(slotName, 0, checked((int)properties.Length), cancellationToken);
        Assert.NotNull(read);
        using var body = read.Value;
        var bytes = body.ToArray();

        if (createdIndices.Count == 1)
        {
            Assert.Equal(payloads[createdIndices[0]], bytes);
        }
        else
        {
            Assert.Contains(payloads, candidate => candidate.SequenceEqual(bytes));
        }

        Assert.Equal(bytes.Length, properties.Length);
    }

    /// <summary>
    /// Verifies that a 0-byte SEAL fence object can be created conditionally, re-create answers
    /// <see cref="ObjectCreateOutcome.Exists"/>, <see cref="IObjectStore.GetPropertiesAsync"/> reports length 0 and no
    /// CRC-32C, listing reports length 0, and <see cref="IObjectStore.ReadAsync"/> returns a 0-byte payload.
    /// </summary>
    public static async Task VerifyEmptySealObjectAsync(
        IObjectStore store,
        ObjectName sealName,
        string listPrefix,
        CancellationToken cancellationToken)
    {
        var first = await store.CreateAsync(sealName, default, crc32c: null, cancellationToken);
        Assert.IsType<ObjectCreateOutcome.Created>(first.Value);

        var second = await store.CreateAsync(sealName, default, crc32c: null, cancellationToken);
        Assert.IsType<ObjectCreateOutcome.Exists>(second.Value);

        Assert.Equal(new ObjectProperties(0, null), await store.GetPropertiesAsync(sealName, cancellationToken));

        var listed = await store.ListAsync(listPrefix, cancellationToken: cancellationToken).ToListAsync(cancellationToken);
        Assert.Equal([new ListedObject(sealName, 0)], listed);

        var versioned = Assert.IsType<VersionedObject>(await store.ReadVersionedAsync(sealName, cancellationToken));
        using var body = versioned.Content;
        Assert.Equal(0L, body.Length);
    }

    /// <summary>
    /// Verifies newest-first reverse-key ordering under <see cref="ObjectLedgerNames.Slots(LedgerId)"/> and
    /// <c>startAfter</c> pruning for the given ascending <paramref name="slotsAscending"/>.
    /// </summary>
    public static async Task VerifyOrderedListingAsync(
        IObjectStore store,
        LedgerId ledgerId,
        IReadOnlyList<long> slotsAscending,
        long startAfterSlot,
        CancellationToken cancellationToken)
    {
        // Write in a scrambled order so listing order proves lexicographic reverse-key ordering, not insertion order.
        var scrambled = slotsAscending
            .OrderBy(static s => unchecked((ulong)(s * 2654435761L) ^ ((ulong)s >> 7)))
            .ToArray();
        foreach (var slot in scrambled)
        {
            var bytes = MakePayload((byte)(slot & 0xFF), 32);
            using var content = ArcBuffer.Wrap(bytes);
            var created = await store.CreateAsync(
                ObjectLedgerNames.Slot(ledgerId, new Slot(slot)),
                content,
                ObjectStoreChecksums.Crc32C(content),
                cancellationToken);
            Assert.IsType<ObjectCreateOutcome.Created>(created.Value);
        }

        var prefix = ObjectLedgerNames.Slots(ledgerId);
        var expectedAll = slotsAscending
            .OrderByDescending(static s => s)
            .Select(s => ObjectLedgerNames.Slot(ledgerId, new Slot(s)))
            .ToArray();

        var listedAll = await store.ListAsync(prefix, cancellationToken: cancellationToken)
            .Select(static e => e.Name)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(expectedAll, listedAll);

        var startAfter = ObjectLedgerNames.Slot(ledgerId, new Slot(startAfterSlot)).Value;
        var expectedAfter = slotsAscending
            .Where(s => s < startAfterSlot)
            .OrderByDescending(static s => s)
            .Select(s => ObjectLedgerNames.Slot(ledgerId, new Slot(s)))
            .ToArray();

        var listedAfter = await store.ListAsync(prefix, startAfter: startAfter, cancellationToken: cancellationToken)
            .Select(static e => e.Name)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(expectedAfter, listedAfter);
    }

    /// <summary>
    /// Verifies that a create whose HTTP response is lost after landing (<see cref="TransportFault.LoseAnswer"/>)
    /// settles deterministically via <see cref="IObjectStore.GetPropertiesAsync"/> and holds the exact bytes and
    /// CRC-32C.
    /// </summary>
    /// <param name="store">The store under test.</param>
    /// <param name="transport">The transport beneath the store's client.</param>
    /// <param name="slotName">The slot object to create.</param>
    /// <param name="assertUnknownOutcome">
    /// <see langword="null"/> where the client recovers the lost answer, so the create answers
    /// <see cref="ObjectCreateOutcome.Created"/> or <see cref="ObjectCreateOutcome.Exists"/>; otherwise the backend leaves
    /// the create's outcome unknown, the create throws, this asserts the exception, and the repeated create, as the
    /// object-ledger writer repeats it (object-ledgers.md §8.1), meets its own landing and answers
    /// <see cref="ObjectCreateOutcome.Exists"/>.
    /// </param>
    /// <param name="cancellationToken">The test's cancellation.</param>
    public static async Task VerifyLostAnswerCreateSettlesAsync(
        IObjectStore store,
        TransportDouble transport,
        ObjectName slotName,
        Action<Exception>? assertUnknownOutcome,
        CancellationToken cancellationToken)
    {
        var payload = MakePayload(77, 192);
        using var content = ArcBuffer.Wrap(payload);
        var expectedCrc = ObjectStoreChecksums.Crc32C(content);

        transport.FailNext(TransportFault.LoseAnswer, TransportDouble.IsUpload);
        if (assertUnknownOutcome is null)
        {
            var outcome = await store.CreateAsync(slotName, content, expectedCrc, cancellationToken);
            Assert.True(
                outcome.Value is ObjectCreateOutcome.Created or ObjectCreateOutcome.Exists,
                $"Expected Created or Exists after lost answer, got {outcome}.");
        }
        else
        {
            assertUnknownOutcome(await Assert.ThrowsAnyAsync<Exception>(async () => await store.CreateAsync(slotName, content, expectedCrc, cancellationToken)));
            Assert.IsType<ObjectCreateOutcome.Exists>((await store.CreateAsync(slotName, content, expectedCrc, cancellationToken)).Value);
        }

        Assert.Equal(1, transport.Injected);

        var properties = await store.GetPropertiesAsync(slotName, cancellationToken);
        Assert.NotNull(properties);
        Assert.Equal(payload.Length, properties.Length);
        Assert.Equal(expectedCrc, properties.Crc32c);

        var read = await store.ReadAsync(slotName, 0, payload.Length, cancellationToken);
        Assert.NotNull(read);
        using var body = read.Value;
        Assert.Equal(payload, body.ToArray());
    }

    /// <summary>
    /// Verifies ranged reads (206 partial content, 416 out-of-range throwing <see cref="EndOfStreamException"/>, 404
    /// absent returning <see langword="null"/>) and strong read-after-delete visibility (object absent from properties,
    /// read, and listing, and re-creatable immediately).
    /// </summary>
    public static async Task VerifyRangedReadsAndStrongDeleteAsync(
        IObjectStore store,
        LedgerId ledgerId,
        CancellationToken cancellationToken)
    {
        var slotName = ObjectLedgerNames.Slot(ledgerId, Slot.Zero);
        var missingName = ObjectLedgerNames.Slot(ledgerId, new Slot(999));
        var payload = MakePayload(53, 256);
        uint crc;

        using (var content = ArcBuffer.Wrap(payload))
        {
            crc = ObjectStoreChecksums.Crc32C(content);
            Assert.IsType<ObjectCreateOutcome.Created>((await store.CreateAsync(slotName, content, crc, cancellationToken)).Value);
        }

        // 206 partial content.
        var sliceRead = await store.ReadAsync(slotName, offset: 17, length: 43, cancellationToken);
        Assert.NotNull(sliceRead);
        using (var slice = sliceRead.Value)
        {
            Assert.Equal(payload.AsSpan(17, 43).ToArray(), slice.ToArray());
        }

        // 416 out-of-range (at exact EOF and past EOF).
        _ = await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await store.ReadAsync(slotName, offset: payload.Length, length: 1, cancellationToken));
        _ = await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await store.ReadAsync(slotName, offset: payload.Length + 64, length: 8, cancellationToken));

        // 404 absent (full and ranged).
        Assert.Null(await store.ReadAsync(missingName, offset: 0, length: payload.Length, cancellationToken));
        Assert.Null(await store.ReadAsync(missingName, offset: 0, length: 16, cancellationToken));

        // Strong read-after-delete.
        await store.DeleteAsync(slotName, cancellationToken);
        Assert.Null(await store.GetPropertiesAsync(slotName, cancellationToken));
        Assert.Null(await store.ReadAsync(slotName, offset: 0, length: payload.Length, cancellationToken));
        var listed = await store.ListAsync(ObjectLedgerNames.Slots(ledgerId), cancellationToken: cancellationToken)
            .Select(static e => e.Name)
            .ToArrayAsync(cancellationToken);
        Assert.DoesNotContain(slotName, listed);

        // Immediately re-creatable after strong delete.
        using var recreated = ArcBuffer.Wrap(payload);
        Assert.IsType<ObjectCreateOutcome.Created>((await store.CreateAsync(slotName, recreated, crc, cancellationToken)).Value);
    }

    internal static byte[] MakePayload(byte seed, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = unchecked((byte)(seed * 29 + i * 13 + 3));
        }

        return bytes;
    }
}
