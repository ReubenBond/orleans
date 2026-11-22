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
/// The per-slot preconditions of object-ledgers.md §3.1 on which <see cref="S3ObjectStore"/> and
/// <see cref="ObjectLedgerStorage"/> rest on general-purpose Amazon S3, one test each, on LocalStack and on a live S3
/// bucket at milestones: conditional create-if-absent (<c>If-None-Match: *</c>, 412 <c>PreconditionFailed</c>),
/// concurrent creates racing at one slot, empty SEAL objects read by <c>GetPropertiesAsync</c>, CRC-32C metadata and
/// corrupt-checksum refusal (<c>400 BadDigest</c>), lost-answer create settlement, ranged reads and strong delete,
/// ordered <c>ListAsync(prefix, startAfter)</c> over newest-first reverse keys, and an end-to-end
/// <see cref="ObjectLedgerStorage"/> lifecycle.
/// </summary>
/// <remarks>
/// Without <c>CLOG_EMULATORS=1</c> the LocalStack cases skip; the live cases run on <see cref="S3LiveAccount"/>,
/// skipping with <see cref="S3LiveAccount.SkipReason"/> while <c>CLOG_S3_LIVE</c> is unset.
/// </remarks>
public sealed class S3ObjectLedgerFidelityTests(CloudEmulators emulators) : IDisposable
{
    private static readonly LedgerId Ledger = new(41);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>Gets the backends: LocalStack and the live S3 bucket.</summary>
    public static TheoryData<S3Backend> Backends => [S3Backend.LocalStack, S3Backend.Live];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Conditional create-if-absent (<c>If-None-Match: *</c>) creates once, answers <see cref="ObjectCreateOutcome.Exists"/>
    /// on a second write, and fails a direct SDK <c>PutObject</c> with 412 <c>PreconditionFailed</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task CreateIfAbsentPreconditionIsEnforced(S3Backend backend)
    {
        await using var target = await OpenAsync(backend);
        var slot = ObjectLedgerNames.Slot(Ledger, Slot.Zero);
        byte[] first = [1, 2, 3, 4];
        byte[] second = [9, 8, 7];

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
                Key = target.Prefix + slot.Value,
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
    /// Of concurrent creates racing at one slot, at most one answers <see cref="ObjectCreateOutcome.Created"/> (exactly
    /// one on LocalStack), every loser answers <see cref="ObjectCreateOutcome.Exists"/> (or S3's transient 409
    /// <c>ConditionalRequestConflict</c>), and the stored slot holds the landed racer's exact bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task OfConcurrentCreatesAtOneSlotExactlyOneLands(S3Backend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyConcurrentCreatesAsync(
            target.Store,
            ObjectLedgerNames.Slot(Ledger, new Slot(5)),
            allowZeroCreatedOnRetry: backend == S3Backend.Live,
            IsConditionalConflict,
            Token);
    }

    /// <summary>
    /// An empty SEAL object (length 0) is created once, reported by <see cref="IObjectStore.GetPropertiesAsync"/> with
    /// length 0 and no CRC-32C, and listed with length 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AnEmptySealObjectIsCreatedAndReadByProperties(S3Backend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyEmptySealObjectAsync(
            target.Store,
            ObjectLedgerNames.Slot(Ledger, new Slot(2)),
            ObjectLedgerNames.Slots(Ledger),
            Token);
    }

    /// <summary>
    /// <see cref="IObjectStore.GetPropertiesAsync"/> returns the stored CRC-32C checksum metadata, and a corrupt checksum
    /// upload is refused by S3 with <c>400 BadDigest</c> and leaves the slot absent.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Crc32CMetadataIsReturnedAndCorruptChecksumIsRefused(S3Backend backend)
    {
        await using var target = await OpenAsync(backend);
        var slot = ObjectLedgerNames.Slot(Ledger, new Slot(9));
        byte[] payload = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];
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
    /// <see cref="IObjectStore.ListAsync"/> lists reverse-keyed slot names newest-first and honors exclusive
    /// <c>startAfter</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task OrderedListingReturnsNewestSlotsFirstAndHonoursStartAfter(S3Backend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyOrderedListingAsync(
            target.Store,
            Ledger,
            slotsAscending: [0, 1, 2, 5, 10, 25],
            startAfterSlot: 5,
            Token);
    }

    /// <summary>
    /// A conditional create whose response is lost after landing settles deterministically via
    /// <see cref="IObjectStore.GetPropertiesAsync"/> with the exact length, CRC-32C, and bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task LostAnswerCreateSettlesThroughProperties(S3Backend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyLostAnswerCreateSettlesAsync(
            target.Store,
            target.Transport,
            ObjectLedgerNames.Slot(Ledger, new Slot(13)),
            Token);
    }

    /// <summary>
    /// Ranged reads (206 partial content, 416 out-of-range, 404 absent) and strong read-after-delete visibility hold on S3.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task RangedReadsAndStrongDeleteAreEnforced(S3Backend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyRangedReadsAndStrongDeleteAsync(target.Store, Ledger, Token);
    }

    /// <summary>
    /// End-to-end <see cref="ObjectLedgerStorage"/> per-slot write, check, recover, read, and sweep lifecycle over S3.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ObjectLedgerStoragePerSlotLifecycleSucceeds(S3Backend backend)
    {
        await using var target = await OpenAsync(backend);
        await ObjectLedgerFidelityLifecycle.VerifyPerSlotAsync(target.Store, Token);
    }

    private static bool IsConditionalConflict(Exception exception) =>
        exception is AmazonS3Exception { StatusCode: HttpStatusCode.Conflict } s3
        && string.Equals(s3.ErrorCode, "ConditionalRequestConflict", StringComparison.Ordinal);

    private async Task<Target> OpenAsync(S3Backend backend)
    {
        if (backend == S3Backend.Live)
        {
            var live = await S3LiveAccount.OpenAsync(Token);
            return new Target(live.Client, live.BucketName, live.Prefix, live.Store, live.Transport, overTls: true, live);
        }

        var endpoint = emulators.Require(Emulator.LocalStack);
        var prefix = $"clog-fidelity-s3/{Guid.NewGuid():N}/";
        var transport = new TransportDouble();
        AmazonS3Client? client = null;
        try
        {
            client = CloudClients.S3Client(endpoint, transport);
            var store = new S3ObjectStore(client, CloudEmulators.ContainerName, prefix);
            var target = new Target(client, CloudEmulators.ContainerName, prefix, store, transport, overTls: false, new EmulatorResources(client, transport));
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

    private sealed class EmulatorResources(AmazonS3Client client, TransportDouble transport) : IAsyncDisposable
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

/// <summary>The backend an <see cref="S3ObjectLedgerFidelityTests"/> case runs against.</summary>
public enum S3Backend
{
    /// <summary>The AppHost's LocalStack emulator, under <c>CLOG_EMULATORS=1</c>.</summary>
    LocalStack,

    /// <summary>The live S3 bucket <see cref="S3LiveAccount.Variable"/> names.</summary>
    Live,
}
