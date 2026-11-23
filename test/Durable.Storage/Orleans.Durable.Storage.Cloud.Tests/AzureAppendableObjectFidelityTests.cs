using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// The preconditions of object-store.md §9.1 on which <see cref="AzureBlobObjectStore"/>'s appendable objects rest, one
/// test each, on Azurite and on a live account at milestones: <c>appendpos</c> and the <c>durable-open</c> tag condition
/// on <c>Append Block</c>, a seal by a tag-conditioned <c>Set Blob Tags</c> atomic with appends in flight, a tombstone's
/// <c>Put Blob</c> over an append blob, an append whose answer is lost before a seal, and the failure codes the store
/// classifies, each asserted beside the store's answer; and the listing start and empty objects of
/// <see cref="IObjectStore"/>.
/// </summary>
/// <remarks>
/// Each test runs in a container of its own, which it deletes. Without <c>DURABLE_STORAGE_EMULATORS=1</c> the Azurite cases skip,
/// and the live cases run on <see cref="AzureLiveAccount"/>, skipping with its reason while its variable is unset.
/// </remarks>
public sealed class AzureAppendableObjectFidelityTests(CloudEmulators emulators) : IDisposable
{
    private const int Racers = 8;

    private static readonly ObjectName Extent = new("ledgers/x/1");
    private static readonly LedgerId PerSlotLedger = new(49);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>Gets the backends: Azurite and the live account.</summary>
    public static TheoryData<AzureBackend> Backends => [AzureBackend.Azurite, AzureBackend.Live];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// An appendable create lands only where no blob is (409 <c>BlobAlreadyExists</c>), and an append only at the blob's
    /// length (412 <c>AppendPositionConditionNotMet</c>), answering the length it met.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AnAppendLandsOnlyAtItsExpectedLength(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await account.Store.CreateAppendableAsync(Extent, Token));
        Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Exists.Instance, await account.Store.CreateAppendableAsync(Extent, Token));
        Assert.Equal((409, "BlobAlreadyExists"), await OutcomeOf(() => account.AppendBlob(Extent).CreateAsync(new AppendBlobCreateOptions { Conditions = new AppendBlobRequestConditions { IfNoneMatch = ETag.All } }, Token)));

        Assert.Equal<ObjectAppendOutcome>(new ObjectAppendOutcome.Appended(3), await AppendAsync(account.Store, 0, [1, 2, 3]));
        Assert.Equal("1", (await account.Container.GetBlobClient(account.Prefix + Extent.Value).GetTagsAsync(cancellationToken: Token)).Value.Tags[AzureBlobObjectStore.OpenTag]);
        Assert.Equal<ObjectAppendOutcome>(new ObjectAppendOutcome.LengthMismatch(3), await AppendAsync(account.Store, 0, [4]));
        Assert.Equal<ObjectAppendOutcome>(new ObjectAppendOutcome.LengthMismatch(3), await AppendAsync(account.Store, 5, [4]));
        Assert.Equal((412, "AppendPositionConditionNotMet"), await OutcomeOf(() => account.AppendBlob(Extent).AppendBlockAsync(Bytes([4]), Condition(appendPosition: 0), Token)));
        Assert.Equal<ObjectAppendOutcome>(new ObjectAppendOutcome.Appended(5), await AppendAsync(account.Store, 3, [4, 5]));

        using var read = await account.Store.ReadAsync(Extent, 0, 5, Token);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, read!.Value.ToArray());
        Assert.Equal(new ObjectProperties(5, null), await account.Store.GetPropertiesAsync(Extent, Token));
    }

    /// <summary>
    /// Of appends racing at one length, exactly one lands, and every other answers the length it left.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task OfConcurrentAppendsAtOneLengthExactlyOneLands(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        _ = await account.Store.CreateAppendableAsync(Extent, Token);
        var answers = await Task.WhenAll(Enumerable.Range(0, Racers).Select(index => AppendAsync(account.Store, 0, [(byte)(index + 1), 0]).AsTask()));

        var landed = Assert.Single(answers.Select((answer, index) => (answer, index)), static pair => pair.answer.Value is ObjectAppendOutcome.Appended);
        Assert.All(answers.Where(static answer => answer.Value is not ObjectAppendOutcome.Appended), static answer => Assert.Equal<ObjectAppendOutcome>(new ObjectAppendOutcome.LengthMismatch(2), answer));
        using var read = await account.Store.ReadAsync(Extent, 0, 2, Token);
        Assert.Equal(new byte[] { (byte)(landed.index + 1), 0 }, read!.Value.ToArray());
    }

    /// <summary>
    /// A seal, <c>Set Blob Tags</c> with <c>durable-open = 0</c>, stops every later append, which its tag condition fails
    /// with 412 <c>ConditionNotMet</c> whatever its position, unless the blob already holds the append's bytes; a seal is
    /// idempotent, its own tag condition failing on a sealed blob, and finds an absent blob (404 <c>BlobNotFound</c>).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ASealStopsEveryLaterAppend(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        _ = await account.Store.CreateAppendableAsync(Extent, Token);
        _ = await AppendAsync(account.Store, 0, [1, 2]);

        Assert.Equal<ObjectSealOutcome>(ObjectSealOutcome.Sealed.Instance, await account.Store.SealAsync(Extent, Token));
        // Azurite ignores x-ms-if-tags on Set Blob Tags, so only a live run establishes the seal's own condition.
        Assert.Equal(
            backend == AzureBackend.Live ? (412, "ConditionNotMet") : (204, null),
            await OutcomeOf(() => account.Container.GetBlobClient(account.Prefix + Extent.Value).SetTagsAsync(Open(false), new BlobRequestConditions { TagConditions = OpenCondition }, Token)));
        Assert.Equal<ObjectSealOutcome>(ObjectSealOutcome.Sealed.Instance, await account.Store.SealAsync(Extent, Token));
        Assert.Equal<ObjectAppendOutcome>(ObjectAppendOutcome.Sealed.Instance, await AppendAsync(account.Store, 2, [3]));
        Assert.Equal<ObjectAppendOutcome>(ObjectAppendOutcome.Sealed.Instance, await AppendAsync(account.Store, 0, [3]));
        Assert.Equal<ObjectAppendOutcome>(new ObjectAppendOutcome.Appended(2), await AppendAsync(account.Store, 0, [1, 2]));
        Assert.Equal((412, "ConditionNotMet"), await OutcomeOf(() => account.AppendBlob(Extent).AppendBlockAsync(Bytes([3]), Condition(appendPosition: 2), Token)));
        Assert.Equal(new ObjectProperties(2, null), await account.Store.GetPropertiesAsync(Extent, Token));

        var absent = new ObjectName("ledgers/x/absent");
        Assert.Equal<ObjectSealOutcome>(ObjectSealOutcome.Absent.Instance, await account.Store.SealAsync(absent, Token));
        Assert.Equal((404, "BlobNotFound"), await OutcomeOf(() => account.Container.GetBlobClient(account.Prefix + absent.Value).SetTagsAsync(new Dictionary<string, string>(StringComparer.Ordinal), cancellationToken: Token)));
    }

    /// <summary>
    /// An append whose answer is lost, after which a seal lands before the client's retry, answers appended: the retry's
    /// tag condition fails, and the sealed blob holds the append's bytes. A store which answers sealed there, the mutant,
    /// would report a write which the blob holds as never made.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AnAppendWhoseAnswerIsLostBeforeASealAnswersAppended(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        foreach (var mutant in new[] { false, true })
        {
            var name = new ObjectName(mutant ? "ledgers/x/mutant" : "ledgers/x/lost");
            var store = new AzureBlobObjectStore(account.Container, account.Prefix) { AnswersSealedForALandedAppendMutant = mutant };
            _ = await store.CreateAppendableAsync(name, Token);
            account.Transport.OnAnswerLost = async () => Assert.Equal<ObjectSealOutcome>(ObjectSealOutcome.Sealed.Instance, await store.SealAsync(name, Token));
            account.Transport.FailNext(TransportFault.LoseAnswer, static request => request.RequestUri!.Query.Contains("comp=appendblock", StringComparison.Ordinal));

            using var content = ArcBuffer.Wrap(new byte[] { 4, 5, 6 });
            var answer = await store.AppendAsync(name, 0, content, null, Token);

            Assert.Equal(new ObjectProperties(3, null), await store.GetPropertiesAsync(name, Token));
            Assert.Equal<ObjectAppendOutcome>(mutant ? ObjectAppendOutcome.Sealed.Instance : new ObjectAppendOutcome.Appended(3), answer);
        }
    }

    /// <summary>
    /// A seal is atomic with the appends in flight against it: once it answers, the blob's length is final, so every
    /// append which lands, lands before the seal, and every other answers sealed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task NoAppendLandsAfterASealAnswers(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        _ = await account.Store.CreateAppendableAsync(Extent, Token);
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appenders = Enumerable.Range(0, Racers).Select(index => Task.Run(async () =>
        {
            var ends = new List<long>();
            var length = 0L;
            for (var round = 0; round < 200; round++)
            {
                switch ((await AppendAsync(account.Store, length, [(byte)(index + 1)])).Value)
                {
                    case ObjectAppendOutcome.Appended appended:
                        ends.Add(appended.Length);
                        length = appended.Length;
                        if (length >= Racers)
                        {
                            _ = landed.TrySetResult();
                        }

                        break;
                    case ObjectAppendOutcome.LengthMismatch mismatch:
                        length = mismatch.Length;
                        break;
                    case ObjectAppendOutcome.Sealed:
                        return ends;
                    default:
                        throw new InvalidOperationException("An append to an open or sealed extent answered absent.");
                }
            }

            return ends;
        })).ToList();

        await landed.Task.WaitAsync(Token);
        Assert.Equal<ObjectSealOutcome>(ObjectSealOutcome.Sealed.Instance, await account.Store.SealAsync(Extent, Token));
        var sealedLength = (await account.Store.GetPropertiesAsync(Extent, Token))!.Length;
        var ends = (await Task.WhenAll(appenders)).SelectMany(static end => end).ToList();

        Assert.Equal(sealedLength, (await account.Store.GetPropertiesAsync(Extent, Token))!.Length);
        Assert.All(ends, end => Assert.True(end <= sealedLength, $"An append answered a length of {end}, past the {sealedLength} bytes the blob held once sealed."));
        Assert.Equal(sealedLength, ends.Count);
    }

    /// <summary>
    /// A tombstone's unconditional <c>Put Blob</c> replaces an append blob whole with a block blob, open or sealed, whose
    /// CRC-32C its properties give; an append to it fails with 409 <c>InvalidBlobType</c> and answers sealed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ATombstoneReplacesAnAppendBlobWhole(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        _ = await account.Store.CreateAppendableAsync(Extent, Token);
        _ = await AppendAsync(account.Store, 0, [1, 2, 3, 4, 5]);

        byte[] tombstone = [9, 8, 7];
        using (var content = ArcBuffer.Wrap(tombstone))
        {
            await account.Store.ReplaceWithTombstoneAsync(Extent, content, ObjectStoreChecksums.Crc32C(content), Token);
        }

        Assert.Equal(BlobType.Block, (await account.Container.GetBlobClient(account.Prefix + Extent.Value).GetPropertiesAsync(cancellationToken: Token)).Value.BlobType);
        Assert.Equal("0", (await account.Container.GetBlobClient(account.Prefix + Extent.Value).GetTagsAsync(cancellationToken: Token)).Value.Tags[AzureBlobObjectStore.OpenTag]);
        Assert.Equal(new ObjectProperties(3, ObjectStoreChecksums.Crc32C(new System.Buffers.ReadOnlySequence<byte>(tombstone))), await account.Store.GetPropertiesAsync(Extent, Token));
        Assert.Equal<ObjectAppendOutcome>(ObjectAppendOutcome.Sealed.Instance, await AppendAsync(account.Store, 3, [1]));
        Assert.Equal((409, "InvalidBlobType"), await OutcomeOf(() => account.AppendBlob(Extent).AppendBlockAsync(Bytes([1]), Condition(appendPosition: 3), Token)));
        Assert.Equal<ObjectSealOutcome>(ObjectSealOutcome.NotAppendable.Instance, await account.Store.SealAsync(Extent, Token));
        using var read = await account.Store.ReadAsync(Extent, 0, 3, Token);
        Assert.Equal(tombstone, read!.Value.ToArray());

        var sealedExtent = new ObjectName("ledgers/x/2");
        _ = await account.Store.CreateAppendableAsync(sealedExtent, Token);
        _ = await account.Store.SealAsync(sealedExtent, Token);
        using (var content = ArcBuffer.Wrap(tombstone))
        {
            await account.Store.ReplaceWithTombstoneAsync(sealedExtent, content, null, Token);
        }

        Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Exists.Instance, await account.Store.CreateAppendableAsync(sealedExtent, Token));
        Assert.Equal(3, (await account.Store.GetPropertiesAsync(sealedExtent, Token))!.Length);

        // An object created whole carries no tag, so a seal's tag condition fails, and it is not appendable.
        var plain = new ObjectName("ledgers/s/1");
        using (var content = ArcBuffer.Wrap(new byte[] { 1 }))
        {
            _ = await account.Store.CreateAsync(plain, content, null, Token);
        }

        Assert.Equal<ObjectSealOutcome>(ObjectSealOutcome.NotAppendable.Instance, await account.Store.SealAsync(plain, Token));
        Assert.Empty((await account.Container.GetBlobClient(account.Prefix + plain.Value).GetTagsAsync(cancellationToken: Token)).Value.Tags);
        using var appended = ArcBuffer.Wrap(new byte[] { 2 });
        Assert.Equal<ObjectAppendOutcome>(ObjectAppendOutcome.Sealed.Instance, await account.Store.AppendAsync(plain, 1, appended, null, Token));
    }

    /// <summary>
    /// An append to an absent blob fails with 404 <c>BlobNotFound</c> and answers absent, and so does a properties read,
    /// whose 404 carries its error code in a header.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AnAppendToAnAbsentObjectAnswersAbsent(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        Assert.Equal<ObjectAppendOutcome>(ObjectAppendOutcome.Absent.Instance, await AppendAsync(account.Store, 0, [1]));
        Assert.Null(await account.Store.GetPropertiesAsync(Extent, Token));
        Assert.Equal((404, "BlobNotFound"), await OutcomeOf(() => account.AppendBlob(Extent).AppendBlockAsync(Bytes([1]), Condition(appendPosition: 0), Token)));
    }

    /// <summary>
    /// An empty object is created and read back by its properties, and a listing from a start name lists only the names
    /// after it, its own name excluded, as <c>startFrom</c>, which includes it, starts the service's listing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AnEmptyObjectAndAListingFromAStartNameAreServed(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var sealMarker = new ObjectName("ledgers/s/2");
        Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await account.Store.CreateAsync(sealMarker, default, null, Token));
        Assert.Equal(new ObjectProperties(0, null), await account.Store.GetPropertiesAsync(sealMarker, Token));
        foreach (var name in new[] { "ledgers/s/1", "ledgers/s/3", "ledgers/s/4" })
        {
            using var content = ArcBuffer.Wrap(new byte[] { 1 });
            _ = await account.Store.CreateAsync(new ObjectName(name), content, null, Token);
        }

        var listed = await account.Store.ListAsync("ledgers/s/", "ledgers/s/2", Token).ToListAsync(Token);
        Assert.Equal(["ledgers/s/3", "ledgers/s/4"], listed.Select(static entry => entry.Name.Value));
        var served = await account.Container.GetBlobsAsync(new GetBlobsOptions { Prefix = account.Prefix + "ledgers/s/", StartFrom = account.Prefix + "ledgers/s/2" }, Token).ToListAsync(Token);
        Assert.Equal([account.Prefix + "ledgers/s/2", account.Prefix + "ledgers/s/3", account.Prefix + "ledgers/s/4"], served.Select(static blob => blob.Name));
    }

    /// <summary>
    /// Per-slot conditional create-if-absent (<c>If-None-Match: *</c>) creates once, answers
    /// <see cref="ObjectCreateOutcome.Exists"/> on a second write, and fails a direct SDK <c>BlobClient.UploadAsync</c>
    /// with <c>409 BlobAlreadyExists</c> (object-ledgers.md §3.1).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task PerSlotCreateIfAbsentPreconditionIsEnforced(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var slot = ObjectLedgerNames.Slot(PerSlotLedger, Slot.Zero);
        byte[] first = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60];
        byte[] second = [0x99, 0x88, 0x77];

        using (var content = ArcBuffer.Wrap(first))
        {
            Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await account.Store.CreateAsync(slot, content, ObjectStoreChecksums.Crc32C(content), Token));
        }

        using (var content = ArcBuffer.Wrap(second))
        {
            Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Exists.Instance, await account.Store.CreateAsync(slot, content, ObjectStoreChecksums.Crc32C(content), Token));
        }

        using var rawStream = new MemoryStream(second);
        var raw = await OutcomeOf(() => account.Container.GetBlobClient(account.Prefix + slot.Value).UploadAsync(
            rawStream,
            new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } },
            Token));
        Assert.Equal((409, BlobErrorCode.BlobAlreadyExists.ToString()), raw);

        var read = await account.Store.ReadAsync(slot, 0, first.Length, Token);
        Assert.NotNull(read);
        using var body = read.Value;
        Assert.Equal(first, body.ToArray());
    }

    /// <summary>
    /// Of concurrent per-slot creates racing at one slot on Azure Blob, at most one answers
    /// <see cref="ObjectCreateOutcome.Created"/> (exactly one on Azurite), every other answers
    /// <see cref="ObjectCreateOutcome.Exists"/>, and the stored slot holds the landed racer's exact bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task PerSlotOfConcurrentCreatesAtOneSlotExactlyOneLands(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyConcurrentCreatesAsync(
            account.Store,
            ObjectLedgerNames.Slot(PerSlotLedger, new Slot(5)),
            allowZeroCreatedOnRetry: backend == AzureBackend.Live,
            isTransientConflict: null,
            Token);
    }

    /// <summary>
    /// An empty SEAL object (length 0) is created once, reported by <see cref="IObjectStore.GetPropertiesAsync"/> with
    /// length 0 and no CRC-32C, and listed with length 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task PerSlotAnEmptySealObjectIsCreatedAndReadByProperties(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyEmptySealObjectAsync(
            account.Store,
            ObjectLedgerNames.Slot(PerSlotLedger, new Slot(2)),
            ObjectLedgerNames.Slots(PerSlotLedger),
            Token);
    }

    /// <summary>
    /// Per-slot CRC-32C metadata round-trips through <see cref="IObjectStore.GetPropertiesAsync"/>, client-side
    /// <c>Crc32CCheckingStream</c> refuses a mismatched CRC-32C, and the service refuses a damaged wire upload with
    /// <c>400 Md5Mismatch</c> while leaving the slot absent (object-ledgers.md §3.1).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task PerSlotCrc32CMetadataIsReturnedAndCorruptChecksumIsRefused(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var slot = ObjectLedgerNames.Slot(PerSlotLedger, new Slot(9));
        byte[] payload = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80];
        uint crc32c;
        using (var content = ArcBuffer.Wrap(payload))
        {
            crc32c = ObjectStoreChecksums.Crc32C(content);

            // Client-side CRC-32C checking stream refuses a mismatched checksum before upload completes.
            _ = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await account.Store.CreateAsync(slot, content, crc32c ^ 1, Token));
            Assert.Null(await account.Store.GetPropertiesAsync(slot, Token));

            // Service-side Content-MD5 wire refusal returns 400 Md5Mismatch and leaves the slot absent.
            account.Transport.FailNext(TransportFault.Corrupt, TransportDouble.IsUpload);
            Exception wireFailure = backend == AzureBackend.Live
                ? await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await account.Store.CreateAsync(slot, content, crc32c, Token))
                : await Assert.ThrowsAsync<IOException>(async () => await account.Store.CreateAsync(slot, content, crc32c, Token));
            var azureFailure = Assert.IsType<RequestFailedException>(wireFailure.InnerException);
            Assert.Equal((400, BlobErrorCode.Md5Mismatch.ToString()), (azureFailure.Status, azureFailure.ErrorCode));
            Assert.Null(await account.Store.GetPropertiesAsync(slot, Token));

            Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await account.Store.CreateAsync(slot, content, crc32c, Token));
        }

        Assert.Equal(new ObjectProperties(payload.Length, crc32c), await account.Store.GetPropertiesAsync(slot, Token));
    }

    /// <summary>
    /// A per-slot conditional create whose response is lost after landing settles deterministically via
    /// <see cref="IObjectStore.GetPropertiesAsync"/> with the exact length, CRC-32C, and bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task PerSlotLostAnswerCreateSettlesThroughProperties(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyLostAnswerCreateSettlesAsync(
            account.Store,
            account.Transport,
            ObjectLedgerNames.Slot(PerSlotLedger, new Slot(13)),
            assertUnknownOutcome: null,
            Token);
    }

    /// <summary>
    /// Ranged reads (206 partial content, 416 out-of-range, 404 absent) and strong read-after-delete visibility hold on
    /// Azure Blob.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task PerSlotRangedReadsAndStrongDeleteAreEnforced(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        await ObjectLedgerPreconditionChecks.VerifyRangedReadsAndStrongDeleteAsync(account.Store, PerSlotLedger, Token);
    }

    /// <summary>
    /// End-to-end <see cref="Engine.Ledgers.Objects.ObjectLedgerStorage"/> per-slot write, check, recover, read, and
    /// sweep lifecycle over Azure Blob.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ObjectLedgerStoragePerSlotLifecycleSucceeds(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        await ObjectLedgerFidelityLifecycle.VerifyPerSlotAsync(account.Store, Token);
    }

    /// <summary>
    /// End-to-end <see cref="Engine.Ledgers.Objects.AppendObjectLedgerStorage"/> append-layout (<c>CLOE</c>) write,
    /// rotation, check, recover, read, and tombstone-sweep lifecycle over Azure Blob.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AppendObjectLedgerStorageLifecycleSucceeds(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        await ObjectLedgerFidelityLifecycle.VerifyAppendAsync(account.Store, Token);
    }

    private static async ValueTask<ObjectAppendOutcome> AppendAsync(AzureBlobObjectStore store, long expectedLength, byte[] bytes)
    {
        using var content = ArcBuffer.Wrap(bytes);
        return await store.AppendAsync(Extent, expectedLength, content, ObjectStoreChecksums.Crc32C(content), Token);
    }

    private const string OpenCondition = "\"durable-open\" = '1'";

    private static AppendBlobAppendBlockOptions Condition(long appendPosition) =>
        new() { Conditions = new AppendBlobRequestConditions { IfAppendPositionEqual = appendPosition, TagConditions = OpenCondition } };

    private static Dictionary<string, string> Open(bool open) => new(StringComparer.Ordinal) { [AzureBlobObjectStore.OpenTag] = open ? "1" : "0" };

    private static MemoryStream Bytes(byte[] bytes) => new(bytes);

    private static async Task<(int Status, string? ErrorCode)> OutcomeOf<T>(Func<Task<Response<T>>> request)
    {
        try
        {
            return ((await request()).GetRawResponse().Status, null);
        }
        catch (RequestFailedException exception)
        {
            return (exception.Status, exception.ErrorCode);
        }
    }

    private static async Task<(int Status, string? ErrorCode)> OutcomeOf(Func<Task<Response>> request)
    {
        try
        {
            return ((await request()).Status, null);
        }
        catch (RequestFailedException exception)
        {
            return (exception.Status, exception.ErrorCode);
        }
    }

    private async Task<Account> AccountAsync(AzureBackend backend)
    {
        if (backend == AzureBackend.Live)
        {
            AzureLiveAccount.Require();
            await AzureLiveAccount.SweepStaleContainersAsync(Token);
        }

        var endpoint = backend == AzureBackend.Azurite ? emulators.Require(Emulator.Azurite) : null;
        var transport = new TransportDouble();
        try
        {
            var service = backend switch
            {
                AzureBackend.Azurite => CloudEmulators.AzureService(endpoint!, transport),
                AzureBackend.Live => AzureLiveAccount.Service(CloudEmulators.AzureOptions(transport)),
                _ => throw new ArgumentOutOfRangeException(nameof(backend)),
            };

            // The account owns the container and transport from before container creation, so a creation which throws
            // leaves neither behind.
            var account = new Account(service.GetBlobContainerClient(FormattableString.Invariant($"{AzureLiveAccount.FidelityContainerPrefix}{Guid.NewGuid():N}")), transport);
            transport = null;
            try
            {
                _ = await account.Container.CreateAsync(cancellationToken: Token);
            }
            catch
            {
                await account.DisposeAsync();
                throw;
            }

            return account;
        }
        finally
        {
            transport?.Dispose();
        }
    }

    /// <summary>A container of its own on a backend, and the store over it, which the test deletes.</summary>
    private sealed class Account(BlobContainerClient container, TransportDouble transport) : IAsyncDisposable
    {
        public TransportDouble Transport { get; } = transport;

        public string Prefix { get; } = "p/";

        public BlobContainerClient Container { get; } = container;

        public AzureBlobObjectStore Store { get; } = new(container, "p/");

        public AppendBlobClient AppendBlob(ObjectName name) => Container.GetAppendBlobClient(Prefix + name.Value);

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(AzureLiveAccount.DeleteTimeout, TimeProvider.System);
                _ = await Container.DeleteIfExistsAsync(cancellationToken: timeout.Token);
            }
            finally
            {
                Transport.Dispose();
            }
        }
    }
}
