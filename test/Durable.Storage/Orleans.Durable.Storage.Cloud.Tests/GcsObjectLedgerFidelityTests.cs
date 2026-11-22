using System.Net;
using Google;
using Google.Cloud.Storage.V1;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.GoogleCloud.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// The per-slot preconditions of object-ledgers.md §3.1 on which <see cref="GoogleCloudStorageObjectStore"/> and
/// <see cref="ObjectLedgerStorage"/> rest on Google Cloud Storage, one test each, on <c>fake-gcs-server</c>,
/// <c>storage-testbench</c>, and a live GCS bucket at milestones: <c>ifGenerationMatch=0</c> create-if-absent
/// (412 <c>PreconditionFailed</c> / <c>conditionNotMet</c>), concurrent creates racing at one slot, empty SEAL objects
/// (including proto3 JSON zero-size omission), CRC-32C metadata and corrupt-checksum refusal, lost-answer create
/// settlement, ranged reads and strong delete, ordered <c>ListAsync(prefix, startAfter)</c> via <c>startOffset</c> plus
/// exclusive client filtering, and an end-to-end <see cref="ObjectLedgerStorage"/> lifecycle.
/// </summary>
/// <remarks>
/// Without <c>DURABLE_STORAGE_EMULATORS=1</c> the emulator cases skip; the live cases run on <see cref="GcsLiveAccount"/>,
/// skipping with <see cref="GcsLiveAccount.SkipReason"/> while <c>DURABLE_STORAGE_GCS_LIVE</c> is unset.
/// </remarks>
public sealed class GcsObjectLedgerFidelityTests(CloudEmulators emulators) : IDisposable
{
    private static readonly LedgerId Ledger = new(47);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>Gets the backends: <c>fake-gcs-server</c>, <c>storage-testbench</c>, and the live GCS bucket.</summary>
    public static TheoryData<GcsBackend> Backends => [GcsBackend.FakeGcsServer, GcsBackend.Testbench, GcsBackend.Live];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// <c>ifGenerationMatch=0</c> creates once, answers <see cref="ObjectCreateOutcome.Exists"/> on a second write, and
    /// fails a direct SDK upload with 412 <c>PreconditionFailed</c> (<c>conditionNotMet</c>).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task CreateIfAbsentPreconditionIsEnforced(GcsBackend backend)
    {
        await using var target = await OpenAsync(backend);
        var slot = ObjectLedgerNames.Slot(Ledger, Slot.Zero);
        byte[] first = [1, 3, 5, 7];
        byte[] second = [2, 4, 6];

        using (var content = ArcBuffer.Wrap(first))
        {
            Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await target.Store.CreateAsync(slot, content, ObjectStoreChecksums.Crc32C(content), Token));
        }

        using (var content = ArcBuffer.Wrap(second))
        {
            Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Exists.Instance, await target.Store.CreateAsync(slot, content, ObjectStoreChecksums.Crc32C(content), Token));
        }

        using var rawStream = new MemoryStream(second);
        var raw = await Assert.ThrowsAsync<GoogleApiException>(() => target.Client.UploadObjectAsync(
            target.BucketName,
            target.Prefix + slot.Value,
            "application/octet-stream",
            rawStream,
            new UploadObjectOptions { IfGenerationMatch = 0 },
            Token));
        Assert.Equal(HttpStatusCode.PreconditionFailed, raw.HttpStatusCode);
        if (backend == GcsBackend.Live || raw.Error?.Errors is { Count: > 0 })
        {
            Assert.NotNull(raw.Error?.Errors);
            Assert.Contains(raw.Error.Errors, static e => string.Equals(e.Reason, "conditionNotMet", StringComparison.Ordinal));
        }

        var read = await target.Store.ReadAsync(slot, 0, first.Length, Token);
        Assert.NotNull(read);
        using var body = read.Value;
        Assert.Equal(first, body.ToArray());
    }

    /// <summary>
    /// Of concurrent creates racing at one slot, at most one answers <see cref="ObjectCreateOutcome.Created"/> (exactly
    /// one on emulators), every other answers <see cref="ObjectCreateOutcome.Exists"/>, and the stored slot holds the
    /// landed racer's exact bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task OfConcurrentCreatesAtOneSlotExactlyOneLands(GcsBackend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyConcurrentCreatesAsync(
            target.Store,
            ObjectLedgerNames.Slot(Ledger, new Slot(7)),
            allowZeroCreatedOnRetry: backend == GcsBackend.Live,
            isTransientConflict: null,
            Token);
    }

    /// <summary>
    /// An empty SEAL object (length 0) is created once, reported by <see cref="IObjectStore.GetPropertiesAsync"/> with
    /// length 0 and no CRC-32C even when proto3 JSON omits zero <c>size</c> (<c>storage-testbench</c>), and listed with
    /// length 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AnEmptySealObjectIsCreatedAndReadByProperties(GcsBackend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyEmptySealObjectAsync(
            target.Store,
            ObjectLedgerNames.Slot(Ledger, new Slot(4)),
            ObjectLedgerNames.Slots(Ledger),
            Token);
    }

    /// <summary>
    /// <see cref="IObjectStore.GetPropertiesAsync"/> returns the stored CRC-32C checksum metadata, and a corrupt checksum
    /// upload is refused and leaves the slot absent (live GCS over TLS throws <see cref="ObjectContentChangedException"/>
    /// wrapping HTTP 400; <c>fake-gcs-server</c> throws <see cref="IOException"/> wrapping HTTP 400;
    /// <c>storage-testbench</c> returns HTTP 412, which settles as an unconfirmed-condition <see cref="IOException"/>
    /// with no inner exception).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Crc32CMetadataIsReturnedAndCorruptChecksumIsRefused(GcsBackend backend)
    {
        await using var target = await OpenAsync(backend);
        var slot = ObjectLedgerNames.Slot(Ledger, new Slot(12));
        byte[] payload = [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0];
        uint crc32c;
        using (var content = ArcBuffer.Wrap(payload))
        {
            crc32c = ObjectStoreChecksums.Crc32C(content);
            switch (backend)
            {
                case GcsBackend.Live:
                    {
                        var failure = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await target.Store.CreateAsync(slot, content, crc32c ^ 1, Token));
                        var gcs = Assert.IsType<GoogleApiException>(failure.InnerException);
                        Assert.Equal(HttpStatusCode.BadRequest, gcs.HttpStatusCode);
                        break;
                    }

                case GcsBackend.FakeGcsServer:
                    {
                        var failure = await Assert.ThrowsAsync<IOException>(async () => await target.Store.CreateAsync(slot, content, crc32c ^ 1, Token));
                        var gcs = Assert.IsType<GoogleApiException>(failure.InnerException);
                        Assert.Equal(HttpStatusCode.BadRequest, gcs.HttpStatusCode);
                        break;
                    }

                case GcsBackend.Testbench:
                    {
                        var failure = await Assert.ThrowsAsync<IOException>(async () => await target.Store.CreateAsync(slot, content, crc32c ^ 1, Token));
                        Assert.Null(failure.InnerException);
                        break;
                    }

                default:
                    throw new ArgumentOutOfRangeException(nameof(backend));
            }

            Assert.Null(await target.Store.GetPropertiesAsync(slot, Token));
            Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await target.Store.CreateAsync(slot, content, crc32c, Token));
        }

        Assert.Equal(new ObjectProperties(payload.Length, crc32c), await target.Store.GetPropertiesAsync(slot, Token));
    }

    /// <summary>
    /// <see cref="IObjectStore.ListAsync"/> lists reverse-keyed slot names newest-first via <c>startOffset</c> (inclusive
    /// on GCS) plus the store's exclusive client filter.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task OrderedListingReturnsNewestSlotsFirstAndHonoursStartAfter(GcsBackend backend)
    {
        await using var target = await OpenAsync(backend);
        long[] slots = [0, 1, 2, 5, 10, 25];
        await ObjectLedgerPreconditionChecks.VerifyOrderedListingAsync(
            target.Store,
            Ledger,
            slotsAscending: slots,
            startAfterSlot: 5,
            Token);

        // Direct SDK listing confirms startOffset is inclusive while ListAsync filters startAfter out.
        var prefix = ObjectLedgerNames.Slots(Ledger);
        var startAfter = ObjectLedgerNames.Slot(Ledger, new Slot(5)).Value;
        var rawNames = await target.Client
            .ListObjectsAsync(target.BucketName, target.Prefix + prefix, new ListObjectsOptions { StartOffset = target.Prefix + startAfter })
            .Select(static obj => obj.Name)
            .ToListAsync(Token);
        Assert.Equal(
            [
                target.Prefix + ObjectLedgerNames.Slot(Ledger, new Slot(5)).Value,
                target.Prefix + ObjectLedgerNames.Slot(Ledger, new Slot(2)).Value,
                target.Prefix + ObjectLedgerNames.Slot(Ledger, new Slot(1)).Value,
                target.Prefix + ObjectLedgerNames.Slot(Ledger, Slot.Zero).Value,
            ],
            rawNames);
    }

    /// <summary>
    /// A conditional create whose response is lost after landing settles deterministically via
    /// <see cref="IObjectStore.GetPropertiesAsync"/> with the exact length, CRC-32C, and bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task LostAnswerCreateSettlesThroughProperties(GcsBackend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyLostAnswerCreateSettlesAsync(
            target.Store,
            target.Transport,
            ObjectLedgerNames.Slot(Ledger, new Slot(13)),
            Token);
    }

    /// <summary>
    /// Ranged reads (206 partial content, 416 out-of-range, 404 absent) and strong read-after-delete visibility hold on GCS.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task RangedReadsAndStrongDeleteAreEnforced(GcsBackend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyRangedReadsAndStrongDeleteAsync(target.Store, Ledger, Token);
    }

    /// <summary>
    /// End-to-end <see cref="ObjectLedgerStorage"/> per-slot write, check, recover, read, and sweep lifecycle over GCS.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ObjectLedgerStoragePerSlotLifecycleSucceeds(GcsBackend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerFidelityLifecycle.VerifyPerSlotAsync(target.Store, Token);
    }

    private async Task<Target> OpenAsync(GcsBackend backend)
    {
        if (backend == GcsBackend.Live)
        {
            var live = await GcsLiveAccount.OpenAsync(Token);
            return new Target(live.Client, live.BucketName, live.Prefix, live.Store, live.Transport, live);
        }

        var emulator = backend == GcsBackend.FakeGcsServer ? Emulator.FakeGcsServer : Emulator.GcsTestbench;
        var endpoint = emulators.Require(emulator);
        var prefix = $"clog-fidelity-gcs/{Guid.NewGuid():N}/";
        var transport = new TransportDouble();
        StorageClient? client = null;
        try
        {
            client = CloudClients.StorageClient(endpoint, transport);
            var store = new GoogleCloudStorageObjectStore(client, CloudEmulators.ContainerName, prefix);
            var target = new Target(client, CloudEmulators.ContainerName, prefix, store, transport, new EmulatorResources(client, transport));
            client = null;
            transport = null;
            return target;
        }
        finally
        {
            client?.Dispose();
            transport?.Dispose();
        }
    }

    private sealed class Target(
        StorageClient client,
        string bucketName,
        string prefix,
        GoogleCloudStorageObjectStore store,
        TransportDouble transport,
        IAsyncDisposable owned) : IAsyncDisposable
    {
        public StorageClient Client { get; } = client;

        public string BucketName { get; } = bucketName;

        public string Prefix { get; } = prefix;

        public GoogleCloudStorageObjectStore Store { get; } = store;

        public TransportDouble Transport { get; } = transport;

        public ValueTask DisposeAsync() => owned.DisposeAsync();
    }

    private sealed class EmulatorResources(StorageClient client, TransportDouble transport) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            var open = transport.OpenResponses;
            client.Dispose();
            transport.Dispose();
            LiveBucketAccount.FinalizeDisposal(cleanupFailure: null, open);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The backend a <see cref="GcsObjectLedgerFidelityTests"/> case runs against.</summary>
public enum GcsBackend
{
    /// <summary>The AppHost's <c>fake-gcs-server</c> emulator, under <c>DURABLE_STORAGE_EMULATORS=1</c>.</summary>
    FakeGcsServer,

    /// <summary>The AppHost's <c>storage-testbench</c> emulator, under <c>DURABLE_STORAGE_EMULATORS=1</c>.</summary>
    Testbench,

    /// <summary>The live Google Cloud Storage bucket <see cref="GcsLiveAccount.Variable"/> names.</summary>
    Live,
}
