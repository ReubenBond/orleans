using System.IO.Hashing;
using System.Net;
using Amazon.S3;
using Azure;
using Google;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Cloud.Tests.StubResponses;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// Each cloud store over a stubbed service, in the default gate: the conditions and checksums its requests carry, the
/// outcome each of the service's answers maps to (archive LSM §3, §9), exact ranges, listing pages, the Cloud Storage
/// store's reads of an upload's object, and cancellation before a request.
/// </summary>
/// <remarks>
/// The stores' clients are the SDKs' own, so the requests are what each SDK sends; only the service is canned.
/// </remarks>
public sealed class CloudObjectStoreStubTests : IDisposable
{
    private static readonly ObjectName Name = new("d/1/0/a1");
    /// <summary>A CRC-32C which <see cref="Content"/> does not have, so only the caller could have supplied it.</summary>
    private const uint WritersCrc32C = 0x0BAD_C0DE;

    private static readonly byte[] Content = [.. Enumerable.Range(0, 13).Select(static index => (byte)(index * 7 + 3))];

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.FakeGcsServer)]
    public async Task EachStoreRefusesTheContractsInvalidArgumentsWithoutARequest(Emulator provider)
    {
        await using var store = Stub(provider, _ => throw new InvalidOperationException("No request is expected."));

        await ObjectStoreContract.InvalidArgumentsAreRefused(store.Store);

        Assert.Empty(store.Transport.Requests);
    }

    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.FakeGcsServer)]
    public async Task ACancelledCallSendsNoRequest(Emulator provider)
    {
        await using var store = Stub(provider, _ => throw new InvalidOperationException("No request is expected."));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var content = ArcBuffer.Wrap(Content);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.Store.CreateAsync(Name, content, null, cancelled.Token));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.Store.ReplaceAsync(ObjectName.Manifest, null, content, null, cancelled.Token));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.Store.ReadAsync(Name, 0, 1, cancelled.Token));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.Store.ReadVersionedAsync(Name, cancelled.Token));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.Store.DeleteAsync(Name, cancelled.Token));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.Store.ListAsync(string.Empty, cancellationToken: cancelled.Token).ToListAsync(Token));

        Assert.Empty(store.Transport.Requests);
    }

    [Fact]
    public async Task TheAzureStoreSendsItsConditionsAndChecksum()
    {
        await using var store = Stub(Emulator.Azurite, static _ => AzureWritten());
        using var content = ArcBuffer.Wrap(Content);

        Assert.IsType<ObjectCreateOutcome.Created>((await store.Store.CreateAsync(Name, content, null, Token)).Value);
        Assert.Equal(new ObjectVersion(StubETag), Assert.IsType<ObjectReplaceOutcome.Applied>((await store.Store.ReplaceAsync(ObjectName.Manifest, new ObjectVersion("\"v1\""), content, null, Token)).Value).Version);
        _ = await store.Store.ReplaceAsync(ObjectName.Manifest, null, content, null, Token);

        var requests = store.Transport.Requests;
        Assert.Equal(3, requests.Count);
        Assert.All(requests, static request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.DoesNotContain("comp=", request.Uri.Query, StringComparison.Ordinal);
            Assert.Equal("BlockBlob", request.Header("x-ms-blob-type"));
            Assert.Equal(Md5Base64(Content), request.Header("Content-MD5"));
            Assert.Equal(Content, request.Body);
        });
        Assert.EndsWith("/d/1/0/a1", requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("*", requests[0].Header("If-None-Match"));
        Assert.Null(requests[0].Header("If-Match"));
        Assert.Equal("\"v1\"", requests[1].Header("If-Match"));
        Assert.Null(requests[1].Header("If-None-Match"));
        Assert.Equal("*", requests[2].Header("If-None-Match"));
    }

    /// <summary>
    /// An object above the SDK's default single-shot size, 256 MiB, still goes as one <c>Put Blob</c>, never as staged
    /// blocks committed by a block list.
    /// </summary>
    [Fact]
    public async Task TheAzureStoreSendsAnObjectAboveTheSdksSingleShotSizeAsOnePutBlob()
    {
        await using var store = Stub(Emulator.Azurite, static _ => AzureWritten());
        store.Transport.RecordBodies = false;
        using var writer = new ArcBufferWriter(int.MaxValue);
        var mebibyte = new byte[1 << 20];
        for (var written = 0; written < 256; written++)
        {
            writer.Write(mebibyte);
        }

        writer.Write([1]);
        using var content = writer.ConsumeSlice(writer.Length);

        Assert.IsType<ObjectCreateOutcome.Created>((await store.Store.CreateAsync(Name, content, null, Token)).Value);

        var request = Assert.Single(store.Transport.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.DoesNotContain("comp=", request.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("BlockBlob", request.Header("x-ms-blob-type"));
        Assert.Equal(content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Header("Content-Length"));
    }

    /// <summary>
    /// The Cloud Storage store checks its bucket with a listing of at most one object, which needs only the
    /// <c>storage.objects.list</c> permission its listings need, never with a read of the bucket.
    /// </summary>
    [Fact]
    public async Task TheCloudStorageBucketCheckListsObjectsRatherThanReadingTheBucket()
    {
        await using var store = Stub(Emulator.FakeGcsServer, static request =>
            IsGcsListing(request) ? GcsListing(string.Empty, [], null) : GcsError(HttpStatusCode.NotFound, "notFound"));

        Assert.Null(await store.Store.ReadVersionedAsync(Name, Token));

        var requests = store.Transport.Requests;
        Assert.Equal(2, requests.Count);
        Assert.DoesNotContain(requests, static request => request.Uri.AbsolutePath.EndsWith("/b/" + CloudEmulators.ContainerName, StringComparison.Ordinal));
        var listing = Assert.Single(requests, IsGcsListing);
        Assert.Contains("maxResults=1", listing.Uri.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// S3 sends the writer's CRC-32C, not one it computes, as <c>x-amz-checksum-crc32c</c>: the value is one the content
    /// does not have, which the stub accepts, so only the writer could have given it.
    /// </summary>
    [Fact]
    public async Task TheS3StoreSendsTheWritersCrc32C()
    {
        await using var store = Stub(Emulator.LocalStack, static _ => S3Written());
        using var content = ArcBuffer.Wrap(Content);

        _ = await store.Store.CreateAsync(Name, content, WritersCrc32C, Token);
        _ = await store.Store.ReplaceAsync(ObjectName.Manifest, new ObjectVersion("\"v1\""), content, WritersCrc32C, Token);

        Assert.Equal(2, store.Transport.Requests.Count);
        Assert.All(store.Transport.Requests, static request => Assert.Equal(Crc32CBase64(WritersCrc32C), request.Header("x-amz-checksum-crc32c")));
    }

    /// <summary>
    /// Over HTTPS, where S3's payload goes unsigned in production, the store still sends the writer's CRC-32C as the header
    /// and no checksum of its own: no trailer, no chunked encoding, and the body as it is.
    /// </summary>
    [Fact]
    public async Task TheS3StoreSendsTheWritersCrc32CUnsignedOverHttps()
    {
        await using var store = Stub(Emulator.LocalStack, static _ => S3Written(), new Uri("https://127.0.0.1:9"));
        using var content = ArcBuffer.Wrap(Content);

        _ = await store.Store.CreateAsync(Name, content, WritersCrc32C, Token);

        var request = Assert.Single(store.Transport.Requests);
        Assert.Equal(Uri.UriSchemeHttps, request.Uri.Scheme);
        Assert.Equal(Crc32CBase64(WritersCrc32C), request.Header("x-amz-checksum-crc32c"));
        Assert.Equal("UNSIGNED-PAYLOAD", request.Header("x-amz-content-sha256"));
        Assert.Null(request.Header("x-amz-trailer"));
        Assert.DoesNotContain("aws-chunked", request.Header("Content-Encoding") ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(Content, request.Body);
    }

    /// <summary>
    /// Cloud Storage sends the writer's CRC-32C as the object's <c>crc32c</c>, and answers only once its read-back shows
    /// that value: a read-back of the content's own CRC-32C, which is not the writer's, leaves the outcome unknown.
    /// </summary>
    [Fact]
    public async Task TheCloudStorageStoreSendsTheWritersCrc32CAndReadsItBack()
    {
        var writers = Crc32CBase64(WritersCrc32C);
        using var content = ArcBuffer.Wrap(Content);
        await using (var store = Stub(Emulator.FakeGcsServer, request => request.Method == HttpMethod.Post
            ? GcsSession()
            : GcsObject("stored", 7, Content.Length, writers, Md5Base64(Content))))
        {
            Assert.IsType<ObjectCreateOutcome.Created>((await store.Store.CreateAsync(Name, content, WritersCrc32C, Token)).Value);
            Assert.IsType<ObjectReplaceOutcome.Applied>((await store.Store.ReplaceAsync(ObjectName.Manifest, new ObjectVersion("5"), content, WritersCrc32C, Token)).Value);

            var sessions = store.Transport.Requests.Where(static request => request.Method == HttpMethod.Post).ToList();
            Assert.Equal(2, sessions.Count);
            Assert.All(sessions, session => Assert.Contains($"\"crc32c\":\"{writers}\"", BodyText(session), StringComparison.Ordinal));
        }

        await using var readsBackContentsOwnCrc32C = Stub(Emulator.FakeGcsServer, static request => request.Method == HttpMethod.Post
            ? GcsSession()
            : GcsObject("stored", 7, Content.Length, Crc32CBase64(Content), Md5Base64(Content)));
        var created = await Assert.ThrowsAsync<IOException>(async () => await readsBackContentsOwnCrc32C.Store.CreateAsync(Name, content, WritersCrc32C, Token));
        var replaced = await Assert.ThrowsAsync<IOException>(async () => await readsBackContentsOwnCrc32C.Store.ReplaceAsync(ObjectName.Manifest, new ObjectVersion("5"), content, WritersCrc32C, Token));

        Assert.All([created, replaced], static refused => Assert.Contains("which holds other content", refused.Message, StringComparison.Ordinal));
        Assert.Equal(2, readsBackContentsOwnCrc32C.Transport.Requests.Count(static request => request.Method == HttpMethod.Get && request.Uri.Query.Contains("generation=7", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Azure takes no CRC-32C on <c>Put Blob</c>, so the store checks the writer's over the bytes its SDK reads for the
    /// <c>Content-MD5</c>: a wrong one is refused before any request, and a right one sends one <c>Put Blob</c> carrying
    /// the content's MD5.
    /// </summary>
    [Fact]
    public async Task TheAzureStoreChecksTheWritersCrc32CBeforeItSendsAnything()
    {
        await using var store = Stub(Emulator.Azurite, static _ => AzureWritten());
        using var content = ArcBuffer.Wrap(Content);
        var crc32c = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, Content);

        _ = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await store.Store.CreateAsync(Name, content, crc32c ^ 1, Token));
        _ = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await store.Store.ReplaceAsync(ObjectName.Manifest, null, content, crc32c ^ 1, Token));
        Assert.Empty(store.Transport.Requests);

        Assert.IsType<ObjectCreateOutcome.Created>((await store.Store.CreateAsync(Name, content, crc32c, Token)).Value);
        var request = Assert.Single(store.Transport.Requests);
        Assert.Equal(Md5Base64(Content), request.Header("Content-MD5"));
    }

    [Fact]
    public async Task TheS3StoreSendsItsConditionsAndChecksum()
    {
        await using var store = Stub(Emulator.LocalStack, static _ => S3Written());
        using var content = ArcBuffer.Wrap(Content);

        Assert.IsType<ObjectCreateOutcome.Created>((await store.Store.CreateAsync(Name, content, null, Token)).Value);
        Assert.Equal(new ObjectVersion(StubETag), Assert.IsType<ObjectReplaceOutcome.Applied>((await store.Store.ReplaceAsync(ObjectName.Manifest, new ObjectVersion("\"v1\""), content, null, Token)).Value).Version);
        _ = await store.Store.ReplaceAsync(ObjectName.Manifest, null, content, null, Token);

        var requests = store.Transport.Requests;
        Assert.Equal(3, requests.Count);
        Assert.All(requests, static request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal(Crc32CBase64(Content), request.Header("x-amz-checksum-crc32c"));
        });
        Assert.EndsWith("/d/1/0/a1", requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("*", requests[0].Header("If-None-Match"));
        Assert.Equal("\"v1\"", requests[1].Header("If-Match"));
        Assert.Equal("*", requests[2].Header("If-None-Match"));
    }

    [Fact]
    public async Task TheCloudStorageStoreSendsItsConditionsAndChecksum()
    {
        await using var store = Stub(Emulator.FakeGcsServer, static request => request.Method == HttpMethod.Post
            ? GcsSession()
            : GcsObject("stored", 7, Content.Length, Crc32CBase64(Content), Md5Base64(Content)));
        using var content = ArcBuffer.Wrap(Content);

        Assert.IsType<ObjectCreateOutcome.Created>((await store.Store.CreateAsync(Name, content, null, Token)).Value);
        Assert.Equal(new ObjectVersion("7"), Assert.IsType<ObjectReplaceOutcome.Applied>((await store.Store.ReplaceAsync(ObjectName.Manifest, new ObjectVersion("5"), content, null, Token)).Value).Version);

        var sessions = store.Transport.Requests.Where(static request => request.Method == HttpMethod.Post).ToList();
        Assert.Equal(2, sessions.Count);
        Assert.Contains("ifGenerationMatch=0", sessions[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("ifGenerationMatch=5", sessions[1].Uri.Query, StringComparison.Ordinal);
        Assert.All(sessions, static session => Assert.Contains($"\"crc32c\":\"{Crc32CBase64(Content)}\"", BodyText(session), StringComparison.Ordinal));
        Assert.All(
            store.Transport.Requests.Where(static request => request.Method == HttpMethod.Put),
            static upload => Assert.Equal(Content, upload.Body));

        // Each upload's object is read back at the generation the upload named.
        Assert.Equal(2, store.Transport.Requests.Count(static request => request.Method == HttpMethod.Get && request.Uri.Query.Contains("generation=7", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheAzureStoresAnswersMapToOutcomes()
    {
        using var content = ArcBuffer.Wrap(Content);
        Assert.IsType<ObjectCreateOutcome.Exists>((await WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.Conflict, "BlobAlreadyExists"), store => store.CreateAsync(Name, content, null, Token))).Value);
        Assert.IsType<ObjectCreateOutcome.Exists>((await WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.PreconditionFailed, "ConditionNotMet"), store => store.CreateAsync(Name, content, null, Token))).Value);
        Assert.IsType<ObjectReplaceOutcome.Mismatch>((await WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.PreconditionFailed, "ConditionNotMet"), store => store.ReplaceAsync(Name, new ObjectVersion("\"v\""), content, null, Token))).Value);
        Assert.IsType<ObjectReplaceOutcome.Mismatch>((await WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.NotFound, "BlobNotFound"), store => store.ReplaceAsync(Name, new ObjectVersion("\"v\""), content, null, Token))).Value);
        Assert.IsType<ObjectReplaceOutcome.Mismatch>((await WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.Conflict, "BlobAlreadyExists"), store => store.ReplaceAsync(Name, null, content, null, Token))).Value);
        Assert.Null(await WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.NotFound, "BlobNotFound"), store => store.ReadAsync(Name, 0, 1, Token)));
        Assert.Null(await WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.NotFound, "BlobNotFound"), store => store.ReadVersionedAsync(Name, Token)));
        await WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.NotFound, "BlobNotFound"), DeleteAsync);

        // No object lies in a missing container, so a read answers its absence; a write or a delete there, and a request
        // the service refuses for its credential or access, is a definite refusal; a 5xx leaves the outcome unknown.
        Assert.Null(await WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.NotFound, "ContainerNotFound"), store => store.ReadAsync(Name, 0, 1, Token)));
        Assert.Null(await WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.NotFound, "ContainerNotFound"), store => store.ReadVersionedAsync(Name, Token)));
        Assert.False((await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.NotFound, "ContainerNotFound"), DeleteAsync))).IsAuthorization);
        _ = await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.NotFound, "BlobNotFound"), store => store.CreateAsync(Name, content, null, Token)));
        _ = await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.NotFound, "ContainerNotFound"), store => store.ReplaceAsync(Name, new ObjectVersion("\"v\""), content, null, Token)));
        Assert.True((await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch"), store => store.ReadVersionedAsync(Name, Token)))).IsAuthorization);
        Assert.True((await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.Unauthorized, "InvalidAuthenticationInfo"), store => store.ReadVersionedAsync(Name, Token)))).IsAuthorization);
        Assert.False((await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.BadRequest, "InvalidQueryParameterValue"), store => store.ReadVersionedAsync(Name, Token)))).IsAuthorization);
        _ = await Assert.ThrowsAsync<EndOfStreamException>(() => WithAnswerAsync(Emulator.Azurite, () => AzureError(HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange"), store => store.ReadAsync(Name, 99, 1, Token)));
    }

    [Fact]
    public async Task TheS3StoresAnswersMapToOutcomes()
    {
        using var content = ArcBuffer.Wrap(Content);
        Assert.IsType<ObjectCreateOutcome.Exists>((await WithAnswerAsync(Emulator.LocalStack, () => S3Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed"), store => store.CreateAsync(Name, content, null, Token))).Value);
        Assert.IsType<ObjectReplaceOutcome.Mismatch>((await WithAnswerAsync(Emulator.LocalStack, () => S3Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed"), store => store.ReplaceAsync(Name, new ObjectVersion("\"v\""), content, null, Token))).Value);
        Assert.IsType<ObjectReplaceOutcome.Mismatch>((await WithAnswerAsync(Emulator.LocalStack, () => S3Error(HttpStatusCode.NotFound, "NoSuchKey"), store => store.ReplaceAsync(Name, new ObjectVersion("\"v\""), content, null, Token))).Value);
        Assert.Null(await WithAnswerAsync(Emulator.LocalStack, () => S3Error(HttpStatusCode.NotFound, "NoSuchKey"), store => store.ReadAsync(Name, 0, 1, Token)));
        Assert.Null(await WithAnswerAsync(Emulator.LocalStack, () => S3Error(HttpStatusCode.NotFound, "NoSuchKey"), store => store.ReadVersionedAsync(Name, Token)));

        // A conditional write which raced another of the key changed nothing and leaves its outcome to the caller.
        var conflict = await Assert.ThrowsAsync<AmazonS3Exception>(() => WithAnswerAsync(Emulator.LocalStack, () => S3Error(HttpStatusCode.Conflict, "ConditionalRequestConflict"), store => store.CreateAsync(Name, content, null, Token)));
        Assert.Equal("ConditionalRequestConflict", conflict.ErrorCode);
        _ = await Assert.ThrowsAsync<AmazonS3Exception>(() => WithAnswerAsync(Emulator.LocalStack, () => S3Error(HttpStatusCode.Conflict, "ConditionalRequestConflict"), store => store.ReplaceAsync(Name, new ObjectVersion("\"v\""), content, null, Token)));
        _ = await Assert.ThrowsAsync<AmazonS3Exception>(() => WithAnswerAsync(Emulator.LocalStack, () => S3Error(HttpStatusCode.NotFound, "NoSuchBucket"), store => store.ReadAsync(Name, 0, 1, Token)));
        _ = await Assert.ThrowsAsync<AmazonS3Exception>(() => WithAnswerAsync(Emulator.LocalStack, () => S3Error(HttpStatusCode.NotFound, "NoSuchBucket"), store => store.ReplaceAsync(Name, new ObjectVersion("\"v\""), content, null, Token)));
        _ = await Assert.ThrowsAsync<EndOfStreamException>(() => WithAnswerAsync(Emulator.LocalStack, () => S3Error(HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange"), store => store.ReadAsync(Name, 99, 1, Token)));
    }

    /// <summary>
    /// A service's refusal of an upload's bytes for their checksum (Azure's <c>Md5Mismatch</c>, S3's <c>BadDigest</c>, and
    /// Cloud Storage's 400 naming the crc32c) means the bytes changed in this process only over TLS, which carries them
    /// intact or not at all: over HTTPS it is <see cref="ObjectContentChangedException"/>, and over plain HTTP, where they
    /// may have changed in transit, an <see cref="IOException"/>, an unknown outcome. Either carries the refusal inside.
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite, "http")]
    [InlineData(Emulator.Azurite, "https")]
    [InlineData(Emulator.LocalStack, "http")]
    [InlineData(Emulator.LocalStack, "https")]
    [InlineData(Emulator.FakeGcsServer, "http")]
    [InlineData(Emulator.FakeGcsServer, "https")]
    public async Task TheServicesChecksumRefusalIsChangedContentOnlyOverTls(Emulator emulator, string scheme)
    {
        using var content = ArcBuffer.Wrap(Content);
        var endpoint = new Uri(scheme + "://127.0.0.1:9");
        await using var store = Stub(
            emulator,
            request => emulator switch
            {
                Emulator.Azurite => AzureError(HttpStatusCode.BadRequest, "Md5Mismatch"),
                Emulator.LocalStack => S3Error(HttpStatusCode.BadRequest, "BadDigest"),
                Emulator.FakeGcsServer when request.Method == HttpMethod.Post => GcsSession(endpoint),
                Emulator.FakeGcsServer => GcsError(HttpStatusCode.BadRequest, "Provided CRC32C does not match the object's data"),
                Emulator.GcsTestbench or _ => throw new ArgumentOutOfRangeException(nameof(emulator)),
            },
            endpoint);

        var refused = await Assert.ThrowsAnyAsync<Exception>(async () => await store.Store.CreateAsync(Name, content, null, Token));

        Assert.All(store.Transport.Requests, request => Assert.Equal(scheme, request.Uri.Scheme));
        Assert.IsType(scheme == Uri.UriSchemeHttps ? typeof(ObjectContentChangedException) : typeof(IOException), refused);
        Assert.IsType(
            emulator switch
            {
                Emulator.Azurite => typeof(RequestFailedException),
                Emulator.LocalStack => typeof(AmazonS3Exception),
                Emulator.FakeGcsServer => typeof(GoogleApiException),
                Emulator.GcsTestbench or _ => throw new ArgumentOutOfRangeException(nameof(emulator)),
            },
            refused.InnerException);
    }

    /// <summary>
    /// A failed condition answers only once a read of the object shows its state against the call's expectation, and a
    /// 404 is an object's absence only once a read shows the bucket exists.
    /// </summary>
    [Fact]
    public async Task TheCloudStorageStoresAnswersMapToOutcomesOnlyOnceAReadConfirmsThem()
    {
        using var content = ArcBuffer.Wrap(Content);
        static HttpResponseMessage NotMet() => GcsError(HttpStatusCode.PreconditionFailed, "conditionNotMet");

        Assert.IsType<ObjectCreateOutcome.Exists>((await GcsAsync(NotMet, current: 4, bucket: true, store => store.CreateAsync(Name, content, null, Token))).Value);
        _ = await Assert.ThrowsAsync<IOException>(() => GcsAsync(NotMet, current: null, bucket: true, store => store.CreateAsync(Name, content, null, Token)));
        Assert.IsType<ObjectReplaceOutcome.Mismatch>((await GcsAsync(NotMet, current: 6, bucket: true, store => store.ReplaceAsync(Name, new ObjectVersion("5"), content, null, Token))).Value);
        Assert.IsType<ObjectReplaceOutcome.Mismatch>((await GcsAsync(NotMet, current: null, bucket: true, store => store.ReplaceAsync(Name, new ObjectVersion("5"), content, null, Token))).Value);
        Assert.IsType<ObjectReplaceOutcome.Mismatch>((await GcsAsync(NotMet, current: 4, bucket: true, store => store.ReplaceAsync(Name, null, content, null, Token))).Value);
        _ = await Assert.ThrowsAsync<IOException>(() => GcsAsync(NotMet, current: 5, bucket: true, store => store.ReplaceAsync(Name, new ObjectVersion("5"), content, null, Token)));
        _ = await Assert.ThrowsAsync<IOException>(() => GcsAsync(NotMet, current: null, bucket: true, store => store.ReplaceAsync(Name, null, content, null, Token)));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => GcsAsync(NotMet, current: null, bucket: false, store => store.CreateAsync(Name, content, null, Token)));

        // A 412 which names another reason is no failed condition, and the testbench's 412 without a reason is read back.
        _ = await Assert.ThrowsAsync<GoogleApiException>(() => GcsAsync(() => GcsError(HttpStatusCode.PreconditionFailed, "invalid"), current: 4, bucket: true, store => store.CreateAsync(Name, content, null, Token)));
        Assert.IsType<ObjectCreateOutcome.Exists>((await GcsAsync(() => GcsError(HttpStatusCode.PreconditionFailed, null), current: 4, bucket: true, store => store.CreateAsync(Name, content, null, Token))).Value);

        // A 400 which names no checksum stays the service's error (TheServicesChecksumRefusalIsChangedContentOnlyOverTls).
        _ = await Assert.ThrowsAsync<GoogleApiException>(() => GcsAsync(() => GcsError(HttpStatusCode.BadRequest, "invalid"), current: null, bucket: true, store => store.CreateAsync(Name, content, null, Token)));

        // A 404 from an upload names a missing bucket or session, never a failed condition.
        _ = await Assert.ThrowsAsync<GoogleApiException>(() => GcsAsync(() => GcsError(HttpStatusCode.NotFound, "notFound"), current: null, bucket: true, store => store.ReplaceAsync(Name, new ObjectVersion("5"), content, null, Token)));

        Assert.Null(await GcsReadAsync(bucket: true, store => store.ReadAsync(Name, 0, 1, Token)));
        Assert.Null(await GcsReadAsync(bucket: true, store => store.ReadVersionedAsync(Name, Token)));
        await GcsReadAsync(bucket: true, async store =>
        {
            await store.DeleteAsync(Name, Token);
            return (ArcBuffer?)null;
        });
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => GcsReadAsync(bucket: false, store => store.ReadAsync(Name, 0, 1, Token)));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => GcsReadAsync(bucket: false, store => store.ReadVersionedAsync(Name, Token)));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => GcsReadAsync(bucket: false, async store =>
        {
            await store.DeleteAsync(Name, Token);
            return (ArcBuffer?)null;
        }));
    }

    /// <summary>
    /// An upload answers only once its object holds the content: an object of another length, crc32c, or MD5 at the
    /// generation the upload named, or none, leaves the outcome unknown.
    /// </summary>
    [Theory]
    [InlineData("size")]
    [InlineData("crc32c")]
    [InlineData("md5")]
    [InlineData("absent")]
    public async Task TheCloudStorageStoreThrowsWhenTheUploadsObjectHoldsOtherContent(string differs)
    {
        await using var store = Stub(Emulator.FakeGcsServer, request => request.Method == HttpMethod.Post
            ? GcsSession()
            : request.Method == HttpMethod.Put
                ? GcsObject("stored", 7, Content.Length, Crc32CBase64(Content), Md5Base64(Content))
                : differs switch
                {
                    "size" => GcsObject("stored", 7, Content.Length + 1, Crc32CBase64(Content), Md5Base64(Content)),
                    "crc32c" => GcsObject("stored", 7, Content.Length, Crc32CBase64([1]), Md5Base64(Content)),
                    "md5" => GcsObject("stored", 7, Content.Length, Crc32CBase64(Content), Md5Base64([1])),
                    _ => GcsError(HttpStatusCode.NotFound, "notFound"),
                });
        using var content = ArcBuffer.Wrap(Content);

        _ = await Assert.ThrowsAsync<IOException>(async () => await store.Store.CreateAsync(Name, content, null, Token));
        _ = await Assert.ThrowsAsync<IOException>(async () => await store.Store.ReplaceAsync(ObjectName.Manifest, new ObjectVersion("5"), content, null, Token));
    }

    /// <summary>
    /// A ranged read answers exactly the requested bytes: a range which passes the object's end is
    /// <see cref="EndOfStreamException"/>, and an answer from another offset, or longer or shorter than the object
    /// allows, is a service fault.
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.FakeGcsServer)]
    public async Task ARangedReadAnswersExactlyItsRange(Emulator provider)
    {
        Func<byte[], long, long, HttpResponseMessage> range = provider switch
        {
            Emulator.Azurite => AzureRange,
            Emulator.LocalStack => S3Range,
            Emulator.FakeGcsServer or Emulator.GcsTestbench => GcsRange,
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        using (var exact = Assert.IsType<ArcBuffer>(await RangeAsync(provider, () => range(Content[4..9], 4, Content.Length), 4, 5)))
        {
            Assert.Equal(Content[4..9], exact.ToArray());
        }

        _ = await Assert.ThrowsAsync<EndOfStreamException>(() => RangeAsync(provider, () => range(Content[10..], 10, Content.Length), 10, 5));
        _ = await Assert.ThrowsAsync<IOException>(() => RangeAsync(provider, () => range(Content[3..8], 3, Content.Length), 4, 5));
        _ = await Assert.ThrowsAsync<IOException>(() => RangeAsync(provider, () => range(Content[4..12], 4, Content.Length), 4, 5));
        _ = await Assert.ThrowsAsync<IOException>(() => RangeAsync(provider, () => range(Content[4..6], 4, Content.Length), 4, 5));
    }

    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.FakeGcsServer)]
    public async Task AListingFetchesEveryPage(Emulator provider)
    {
        string? prefix = null;
        var pages = 0;
        await using var store = Stub(provider, request =>
        {
            var first = Interlocked.Increment(ref pages) == 1;
            return provider switch
            {
                Emulator.Azurite => AzureListing(prefix!, first ? ["c/k/c3", "d/1/0/a1"] : ["manifest"], first ? "next" : null),
                Emulator.LocalStack => S3Listing(prefix!, first ? ["c/k/c3", "d/1/0/a1"] : ["manifest"], first ? "next" : null),
                Emulator.FakeGcsServer or Emulator.GcsTestbench => GcsListing(prefix!, first ? ["c/k/c3", "d/1/0/a1"] : ["manifest"], first ? "next" : null),
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };
        });
        prefix = store.Prefix;

        var listed = await store.Store.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token);

        Assert.Equal(["c/k/c3", "d/1/0/a1", "manifest"], listed.Select(static entry => entry.Name.Value));
        Assert.All(listed, static entry => Assert.Equal(3, entry.Length));
        var requests = store.Transport.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Contains("next", requests[1].Uri.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Cloud Storage store repeats a ranged read whose answer the transport lost, or which the service answered 503 or
    /// 429, none of which its client repeats for a download, and answers the range the repetition reads (archive LSM §9).
    /// </summary>
    /// <param name="status">The status of the first answer, or 0 when the transport loses it.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(503)]
    [InlineData(429)]
    public async Task TheCloudStorageStoreRepeatsAReadWhoseFirstAnswerFailedTransiently(int status)
    {
        var requests = 0;
        await using var store = Stub(Emulator.FakeGcsServer, _ => Interlocked.Increment(ref requests) switch
        {
            1 when status == 0 => throw new TransportDoubleException(),
            1 => GcsError((HttpStatusCode)status, null),
            _ => GcsRange([2, 3], 1, 4),
        });

        using var read = await store.Store.ReadAsync(Name, 1, 2, Token);

        Assert.Equal([2, 3], Assert.NotNull(read).ToArray());
        Assert.Equal(2, store.Transport.Requests.Count);
    }

    /// <summary>
    /// The Cloud Storage store repeats a delete whose answer the transport lost; the repetition finds the object absent, a
    /// 404, which completes the delete once a listing shows the bucket exists, as the lost answer would have, since no
    /// caller uses a deleted name again (LSM-I7).
    /// </summary>
    [Fact]
    public async Task TheCloudStorageStoreRepeatsADeleteWhoseAnswerWasLostAndTakesTheAbsenceItFinds()
    {
        var deletes = 0;
        await using var store = Stub(Emulator.FakeGcsServer, request =>
            IsGcsListing(request) ? GcsListing(string.Empty, [], null)
            : request.Method == HttpMethod.Delete && Interlocked.Increment(ref deletes) == 1 ? throw new TransportDoubleException()
            : GcsError(HttpStatusCode.NotFound, "notFound"));

        await store.Store.DeleteAsync(Name, Token);

        Assert.Equal(2, deletes);
        _ = Assert.Single(store.Transport.Requests, IsGcsListing);
    }

    /// <summary>
    /// The Cloud Storage store's repetitions are bounded: a read whose every answer is lost fails with the transport's
    /// failure after its first attempt and three repetitions, an unknown outcome its caller decides on.
    /// </summary>
    [Fact]
    public async Task TheCloudStorageStoreGivesUpAReadAfterThreeRepetitions()
    {
        await using var store = Stub(Emulator.FakeGcsServer, static _ => throw new TransportDoubleException());

        _ = await Assert.ThrowsAsync<TransportDoubleException>(async () => await store.Store.ReadAsync(Name, 0, 2, Token));

        Assert.Equal(4, store.Transport.Requests.Count);
    }

    private static CloudStore Stub(Emulator provider, Func<StubbedRequest, HttpResponseMessage> respond, Uri? endpoint = null)
    {
        var store = new CloudStore(provider, endpoint ?? Unserved);
        store.Transport.Respond = respond;
        return store;
    }

    private static async Task<T> WithAnswerAsync<T>(Emulator provider, Func<HttpResponseMessage> answer, Func<IObjectStore, ValueTask<T>> call)
    {
        await using var store = Stub(provider, _ => answer());
        return await call(store.Store);
    }

    private static async Task<ArcBuffer?> RangeAsync(Emulator provider, Func<HttpResponseMessage> answer, long offset, int length)
    {
        await using var store = Stub(provider, _ => answer());
        return await store.Store.ReadAsync(Name, offset, length, Token);
    }

    private static async ValueTask<bool> DeleteAsync(IObjectStore store)
    {
        await store.DeleteAsync(Name, Token);
        return true;
    }

    /// <summary>
    /// Runs <paramref name="call"/> against a Cloud Storage store whose upload answers <paramref name="upload"/>, whose
    /// object is at <paramref name="current"/> or absent, and whose bucket exists or not.
    /// </summary>
    private static async Task<T> GcsAsync<T>(Func<HttpResponseMessage> upload, long? current, bool bucket, Func<IObjectStore, ValueTask<T>> call)
    {
        await using var store = Stub(Emulator.FakeGcsServer, request => request switch
        {
            { Method.Method: "POST" } => GcsSession(),
            { Method.Method: "PUT" } => upload(),
            _ when IsGcsListing(request) => bucket ? GcsListing(string.Empty, [], null) : GcsError(HttpStatusCode.NotFound, "notFound"),
            _ => current is { } generation ? GcsObject("stored", generation, 3) : GcsError(HttpStatusCode.NotFound, "notFound"),
        });
        return await call(store.Store);
    }

    private static async Task<T> GcsReadAsync<T>(bool bucket, Func<IObjectStore, ValueTask<T>> call)
    {
        await using var store = Stub(Emulator.FakeGcsServer, request =>
            IsGcsListing(request) && bucket
                ? GcsListing(string.Empty, [], null)
                : GcsError(HttpStatusCode.NotFound, "notFound"));
        return await call(store.Store);
    }

    /// <summary>
    /// Returns whether <paramref name="request"/> lists the bucket's objects, as the store's bucket check does.
    /// </summary>
    private static bool IsGcsListing(StubbedRequest request) =>
        request.Uri.AbsolutePath.EndsWith("/b/" + CloudEmulators.ContainerName + "/o", StringComparison.Ordinal);
}
