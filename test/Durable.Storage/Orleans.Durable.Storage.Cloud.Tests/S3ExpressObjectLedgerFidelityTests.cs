using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.S3.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// The per-slot preconditions of object-ledgers.md §3.1 on which <see cref="S3ObjectStore"/> in directory-bucket mode
/// (<c>--x-s3</c>) and <see cref="ObjectLedgerStorage"/> rest on Amazon S3 Express One Zone, one test each, over the
/// in-process <see cref="FakeS3DirectoryBucketService"/> (runs always without Docker) and on a live S3 Express directory
/// bucket at milestones: conditional create-if-absent (<c>If-None-Match: *</c>, 412 <c>PreconditionFailed</c>),
/// concurrent creates racing at one slot, empty SEAL objects, CRC-32C metadata and corrupt-checksum refusal
/// (<c>400 BadDigest</c>), lost-answer create settlement, ranged reads and strong delete, ordered
/// <c>ListAsync(prefix, startAfter)</c> across directory chunks plus direct SDK directory-bucket listing refusals, and
/// an end-to-end <see cref="ObjectLedgerStorage"/> lifecycle.
/// </summary>
/// <remarks>
/// The fake backend runs in every test pass with no emulator or credentials; the live backend runs on
/// <see cref="S3ExpressLiveAccount"/>, skipping with <see cref="S3ExpressLiveAccount.SkipReason"/> while
/// <c>CLOG_S3_EXPRESS_LIVE</c> is unset.
/// </remarks>
public sealed class S3ExpressObjectLedgerFidelityTests : IDisposable
{
    private static readonly LedgerId Ledger = new(43);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>Gets the backends: the in-process directory-bucket fake and the live S3 Express directory bucket.</summary>
    public static TheoryData<S3ExpressBackend> Backends => [S3ExpressBackend.Fake, S3ExpressBackend.Live];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Directory-bucket conditional create-if-absent (<c>If-None-Match: *</c>) creates once, answers
    /// <see cref="ObjectCreateOutcome.Exists"/> on a second write, and fails a direct SDK <c>PutObject</c> with 412
    /// <c>PreconditionFailed</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task CreateIfAbsentPreconditionIsEnforced(S3ExpressBackend backend)
    {
        await using var target = await OpenAsync(backend);
        Assert.True(S3DirectoryBucketLayout.IsDirectoryBucket(target.BucketName));

        var slot = ObjectLedgerNames.Slot(Ledger, Slot.Zero);
        byte[] first = [10, 20, 30, 40];
        byte[] second = [99, 98, 97];

        using (var content = ArcBuffer.Wrap(first))
        {
            Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await target.Store.CreateAsync(slot, content, ObjectStoreChecksums.Crc32C(content), Token));
        }

        using (var content = ArcBuffer.Wrap(second))
        {
            Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Exists.Instance, await target.Store.CreateAsync(slot, content, ObjectStoreChecksums.Crc32C(content), Token));
        }

        using var rawStream = new MemoryStream(second);
        var raw = await Assert.ThrowsAsync<AmazonS3Exception>(() => target.Client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = target.BucketName,
                Key = S3DirectoryBucketLayout.Encode(target.Prefix, slot),
                InputStream = rawStream,
                IfNoneMatch = "*",
            },
            Token));
        Assert.Equal(HttpStatusCode.PreconditionFailed, raw.StatusCode);
        Assert.Equal("PreconditionFailed", raw.ErrorCode);

        var read = await target.Store.ReadAsync(slot, 0, first.Length, Token);
        Assert.NotNull(read);
        using var body = read.Value;
        Assert.Equal(first, body.ToArray());
    }

    /// <summary>
    /// Of concurrent creates racing at one slot on a directory bucket, at most one answers
    /// <see cref="ObjectCreateOutcome.Created"/> (exactly one on the in-process fake), every loser answers
    /// <see cref="ObjectCreateOutcome.Exists"/> (or S3's 409 <c>ConditionalRequestConflict</c>), and the slot holds the
    /// landed racer's exact bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task OfConcurrentCreatesAtOneSlotExactlyOneLands(S3ExpressBackend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyConcurrentCreatesAsync(
            target.Store,
            ObjectLedgerNames.Slot(Ledger, new Slot(6)),
            allowZeroCreatedOnRetry: backend == S3ExpressBackend.Live,
            IsConditionalConflict,
            Token);
    }

    /// <summary>
    /// An empty SEAL object (length 0) is created once, reported by <see cref="IObjectStore.GetPropertiesAsync"/> with
    /// length 0 and no CRC-32C, and listed with length 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AnEmptySealObjectIsCreatedAndReadByProperties(S3ExpressBackend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyEmptySealObjectAsync(
            target.Store,
            ObjectLedgerNames.Slot(Ledger, new Slot(3)),
            ObjectLedgerNames.Slots(Ledger),
            Token);
    }

    /// <summary>
    /// <see cref="IObjectStore.GetPropertiesAsync"/> returns the stored CRC-32C checksum metadata, and a corrupt checksum
    /// upload is refused by the directory bucket with <c>400 BadDigest</c> and leaves the slot absent.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Crc32CMetadataIsReturnedAndCorruptChecksumIsRefused(S3ExpressBackend backend)
    {
        await using var target = await OpenAsync(backend);
        var slot = ObjectLedgerNames.Slot(Ledger, new Slot(11));
        byte[] payload = [0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87, 0x98];
        uint crc32c;
        using (var content = ArcBuffer.Wrap(payload))
        {
            crc32c = ObjectStoreChecksums.Crc32C(content);
            Exception failure = target.OverTls
                ? await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await target.Store.CreateAsync(slot, content, crc32c ^ 1, Token))
                : await Assert.ThrowsAsync<IOException>(async () => await target.Store.CreateAsync(slot, content, crc32c ^ 1, Token));
            var s3Failure = Assert.IsType<AmazonS3Exception>(failure.InnerException);
            Assert.Equal((HttpStatusCode.BadRequest, "BadDigest"), (s3Failure.StatusCode, s3Failure.ErrorCode));

            Assert.Null(await target.Store.GetPropertiesAsync(slot, Token));
            Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await target.Store.CreateAsync(slot, content, crc32c, Token));
        }

        Assert.Equal(new ObjectProperties(payload.Length, crc32c), await target.Store.GetPropertiesAsync(slot, Token));
    }

    /// <summary>
    /// <see cref="IObjectStore.ListAsync"/> restores ordinal newest-first ordering across multiple 3-character
    /// <see cref="S3DirectoryBucketLayout"/> directory chunks and honors exclusive <c>startAfter</c>, while direct SDK
    /// <c>ListObjectsV2</c> confirms the directory bucket refuses <c>StartAfter</c> (<c>400 InvalidArgument</c>) and
    /// non-<c>'/'</c>-terminated <c>Prefix</c> (<c>400 InvalidRequest</c>).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task OrderedListingReturnsNewestSlotsFirstAndHonoursStartAfter(S3ExpressBackend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyOrderedListingAsync(
            target.Store,
            Ledger,
            slotsAscending: [0, 1, 9, 10, 1000, 1001, 2000, 65536],
            startAfterSlot: 1000,
            Token);

        // Direct SDK checks pin the S3 Express One Zone directory-bucket listing constraints.
        var startAfterRefused = await Assert.ThrowsAsync<AmazonS3Exception>(() => target.Client.ListObjectsV2Async(
            new ListObjectsV2Request
            {
                BucketName = target.BucketName,
                Prefix = target.Prefix,
                StartAfter = target.Prefix + "a",
            },
            Token));
        Assert.Equal((HttpStatusCode.BadRequest, "InvalidArgument"), (startAfterRefused.StatusCode, startAfterRefused.ErrorCode));

        var prefixRefused = await Assert.ThrowsAsync<AmazonS3Exception>(() => target.Client.ListObjectsV2Async(
            new ListObjectsV2Request
            {
                BucketName = target.BucketName,
                Prefix = target.Prefix.TrimEnd('/'),
            },
            Token));
        Assert.Equal((HttpStatusCode.BadRequest, "InvalidRequest"), (prefixRefused.StatusCode, prefixRefused.ErrorCode));
    }

    /// <summary>
    /// A conditional create whose response is lost after landing settles deterministically via
    /// <see cref="IObjectStore.GetPropertiesAsync"/> with the exact length, CRC-32C, and bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task LostAnswerCreateSettlesThroughProperties(S3ExpressBackend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyLostAnswerCreateSettlesAsync(
            target.Store,
            target.Transport,
            ObjectLedgerNames.Slot(Ledger, new Slot(13)),
            Token);
    }

    /// <summary>
    /// Ranged reads (206 partial content, 416 out-of-range, 404 absent) and strong read-after-delete visibility hold on
    /// S3 Express directory buckets.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task RangedReadsAndStrongDeleteAreEnforced(S3ExpressBackend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyRangedReadsAndStrongDeleteAsync(target.Store, Ledger, Token);
    }

    /// <summary>
    /// End-to-end <see cref="ObjectLedgerStorage"/> per-slot write, check, recover, read, and sweep lifecycle over an
    /// S3 Express directory bucket.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ObjectLedgerStoragePerSlotLifecycleSucceeds(S3ExpressBackend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerFidelityLifecycle.VerifyPerSlotAsync(target.Store, Token);
    }

    private static bool IsConditionalConflict(Exception exception) =>
        exception is AmazonS3Exception { StatusCode: HttpStatusCode.Conflict } s3
        && string.Equals(s3.ErrorCode, "ConditionalRequestConflict", StringComparison.Ordinal);

    private static async Task<Target> OpenAsync(S3ExpressBackend backend)
    {
        if (backend == S3ExpressBackend.Live)
        {
            var live = await S3ExpressLiveAccount.OpenAsync(Token);
            return new Target(live.Client, live.BucketName, live.Prefix, live.Store, live.Transport, overTls: true, live);
        }

        FakeS3DirectoryBucketService? fake = new(
            listingOrder: FakeS3ListingOrder.Shuffled,
            maxPageSize: 2,
            retainEmptyDirectories: true,
            seed: 76);
        TransportDouble? transport = null;
        AmazonS3Client? client = null;
        try
        {
            transport = new TransportDouble(fake);
            fake = null; // Owned by transport's InnerHandler.
            var prefix = $"clog-fidelity-s3e/{Guid.NewGuid():N}/";
            client = CloudClients.S3Client(StubResponses.Unserved, transport, disableS3ExpressSessionAuth: true);
            var store = new S3ObjectStore(client, FakeS3DirectoryBucketService.DefaultBucketName, prefix);
            var target = new Target(client, FakeS3DirectoryBucketService.DefaultBucketName, prefix, store, transport, overTls: false, new FakeResources(client, transport));
            client = null;
            transport = null;
            return target;
        }
        finally
        {
            client?.Dispose();
            transport?.Dispose();
            fake?.Dispose();
        }
    }

    private sealed class Target(
        AmazonS3Client client,
        string bucketName,
        string prefix,
        S3ObjectStore store,
        TransportDouble transport,
        bool overTls,
        IAsyncDisposable owned) : IAsyncDisposable
    {
        public AmazonS3Client Client { get; } = client;

        public string BucketName { get; } = bucketName;

        public string Prefix { get; } = prefix;

        public S3ObjectStore Store { get; } = store;

        public TransportDouble Transport { get; } = transport;

        public bool OverTls { get; } = overTls;

        public ValueTask DisposeAsync() => owned.DisposeAsync();
    }

    private sealed class FakeResources(AmazonS3Client client, TransportDouble transport) : IAsyncDisposable
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

/// <summary>The backend an <see cref="S3ExpressObjectLedgerFidelityTests"/> case runs against.</summary>
public enum S3ExpressBackend
{
    /// <summary>The in-process <see cref="FakeS3DirectoryBucketService"/>, which runs without Docker or credentials.</summary>
    Fake,

    /// <summary>The live S3 Express One Zone directory bucket <see cref="S3ExpressLiveAccount.Variable"/> names.</summary>
    Live,
}
