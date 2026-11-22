using System.IO.Hashing;
using System.Net;
using Amazon.S3;
using Azure;
using Google;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// Each cloud store on its emulator, with faults its transport injects at chosen requests and responses: an upload whose
/// bytes change after the client computed their checksum, which the service refuses (archive LSM §10, integrity);
/// requests which fail before they land or lose their answers after, which the SDK's built-in retries meet (archive §3);
/// a send which outlives its cancelled call; and a download which breaks while another writer replaces the object.
/// </summary>
public sealed class CloudObjectStoreFaultTests(CloudEmulators emulators) : IDisposable
{
    private static readonly ObjectName Name = new("d/1/0/fault");

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// The transport flips a byte of the upload after the client computed its checksum: the service refuses the bytes for
    /// their checksum, which the store, reaching its emulator over plain HTTP, raises as an <see cref="IOException"/>, an
    /// unknown outcome, with the service's refusal inside; the create fails, and no object exists; the create then succeeds.
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.FakeGcsServer)]
    public async Task AnUploadDamagedAfterItsChecksumIsRefused(Emulator emulator)
    {
        await using var store = new CloudStore(emulator, emulators.Require(emulator));
        using var content = Content(ArcBufferPagePool.LargestPageSize + 17, seed: 1);
        store.Transport.FailNext(TransportFault.Corrupt, TransportDouble.IsUpload);

        // Over plain HTTP the bytes may have changed in transit, as here, so the refusal leaves the outcome unknown.
        var refused = (await Assert.ThrowsAsync<IOException>(async () => await store.Store.CreateAsync(Name, content, null, Token))).InnerException;

        switch (emulator)
        {
            case Emulator.Azurite:
                var azure = Assert.IsType<RequestFailedException>(refused);
                Assert.Equal((400, "Md5Mismatch"), (azure.Status, azure.ErrorCode));
                break;
            case Emulator.LocalStack:
                var s3 = Assert.IsType<AmazonS3Exception>(refused);
                Assert.Equal((HttpStatusCode.BadRequest, "BadDigest"), (s3.StatusCode, s3.ErrorCode));
                break;
            case Emulator.FakeGcsServer:
                Assert.Equal(HttpStatusCode.BadRequest, Assert.IsType<GoogleApiException>(refused).HttpStatusCode);
                break;
            case Emulator.GcsTestbench:
            default:
                throw new ArgumentOutOfRangeException(nameof(emulator));
        }

        Assert.Equal(1, store.Transport.Injected);
        Assert.Null(await store.Store.ReadVersionedAsync(Name, Token));
        Assert.IsType<ObjectCreateOutcome.Created>((await store.Store.CreateAsync(Name, content, null, Token)).Value);
        await AssertHoldsAsync(store.Store, content);
    }

    /// <summary>
    /// The testbench answers an upload whose bytes fail their checksum with 412, as for a failed condition: the store
    /// reads the object, finds none, and throws rather than answer <see cref="ObjectCreateOutcome.Exists"/>.
    /// </summary>
    [Fact]
    public async Task ADamagedUploadNeverAnswersExists()
    {
        await using var store = new CloudStore(Emulator.GcsTestbench, emulators.Require(Emulator.GcsTestbench));
        using var content = Content(4096, seed: 6);
        store.Transport.FailNext(TransportFault.Corrupt, TransportDouble.IsUpload);

        _ = await Assert.ThrowsAsync<IOException>(async () => await store.Store.CreateAsync(Name, content, null, Token));

        Assert.Null(await store.Store.ReadVersionedAsync(Name, Token));
    }

    /// <summary>
    /// The service never sees the first upload: the client's retry lands the create, which answers
    /// <see cref="ObjectCreateOutcome.Created"/>.
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.GcsTestbench)]
    public async Task AnUploadRefusedBeforeItLandsIsRetried(Emulator emulator)
    {
        await using var store = new CloudStore(emulator, emulators.Require(emulator));
        using var content = Content(2 * ArcBufferPagePool.LargestPageSize + 5, seed: 2);
        store.Transport.FailNext(TransportFault.FailBefore, TransportDouble.IsUpload);

        Assert.IsType<ObjectCreateOutcome.Created>((await store.Store.CreateAsync(Name, content, null, Token)).Value);

        Assert.Equal(1, store.Transport.Injected);
        await AssertHoldsAsync(store.Store, content);
    }

    /// <summary>
    /// The first upload lands and its answer is lost: the client's retry meets its own landing, which fails its
    /// condition, so the create answers <see cref="ObjectCreateOutcome.Exists"/>; or, where the client asks the resumable
    /// session it uploaded through for its state, the session reports the object it finalized, which the store reads
    /// back, and the create answers <see cref="ObjectCreateOutcome.Created"/>. The object holds the content.
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite, false)]
    [InlineData(Emulator.LocalStack, false)]
    [InlineData(Emulator.GcsTestbench, true)]
    public async Task ACreateWhoseAnswerIsLostAnswersFromItsOwnLanding(Emulator emulator, bool resumable)
    {
        await using var store = new CloudStore(emulator, emulators.Require(emulator));
        using var content = Content(ArcBufferPagePool.LargestPageSize + 3, seed: 3);
        store.Transport.FailNext(TransportFault.LoseAnswer, TransportDouble.IsUpload);

        var outcome = await store.Store.CreateAsync(Name, content, null, Token);

        Assert.Equal(1, store.Transport.Injected);
        ObjectCreateOutcome expected = resumable ? ObjectCreateOutcome.Created.Instance : ObjectCreateOutcome.Exists.Instance;
        Assert.Equal(expected, outcome);
        await AssertHoldsAsync(store.Store, content);
        Assert.IsType<ObjectCreateOutcome.Exists>((await store.Store.CreateAsync(Name, content, null, Token)).Value);
    }

    /// <summary>
    /// The S3 client's retry of a lost answer does not depend on retries other clients spent (#377). Another client of
    /// the same endpoint, which keeps the SDK's process-wide retry quota, meets a 503 at every request until the quota is
    /// empty and its call makes one request and no retry; the store's create, whose answer is then lost, still retries,
    /// meets its own landing, and answers <see cref="ObjectCreateOutcome.Exists"/>.
    /// </summary>
    [Fact]
    public async Task ALostAnswerIsRetriedWhenOtherClientsEmptiedTheRetryQuota()
    {
        var endpoint = emulators.Require(Emulator.LocalStack);
        using (var unavailable = new TransportDouble { Respond = static _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) } })
        using (var draining = CloudClients.S3Client(endpoint, unavailable, throttleRetries: true))
        {
            var emptied = false;
            for (var call = 0; call < 200 && !emptied; call++)
            {
                var before = unavailable.Requests.Count;
                _ = await Assert.ThrowsAsync<AmazonS3Exception>(() => draining.GetObjectMetadataAsync(CloudEmulators.ContainerName, "quota", Token));
                emptied = unavailable.Requests.Count - before == 1;
            }

            Assert.True(emptied, "The endpoint's retry quota never emptied.");
        }

        await using var store = new CloudStore(Emulator.LocalStack, endpoint);
        using var content = Content(1000, seed: 9);
        store.Transport.FailNext(TransportFault.LoseAnswer, TransportDouble.IsUpload);

        Assert.IsType<ObjectCreateOutcome.Exists>((await store.Store.CreateAsync(Name, content, null, Token)).Value);
        Assert.Equal(1, store.Transport.Injected);
        await AssertHoldsAsync(store.Store, content);
    }

    /// <summary>
    /// A replace whose answer is lost: the client's retry meets its own landing, which fails its condition, so the
    /// replace answers <see cref="ObjectReplaceOutcome.Mismatch"/>; or, where the client asks its resumable session for
    /// its state, <see cref="ObjectReplaceOutcome.Applied"/> with the version the object holds. The object holds the new
    /// content.
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite, false)]
    [InlineData(Emulator.LocalStack, false)]
    [InlineData(Emulator.GcsTestbench, true)]
    public async Task AReplaceWhoseAnswerIsLostAnswersFromItsOwnLanding(Emulator emulator, bool resumable)
    {
        await using var store = new CloudStore(emulator, emulators.Require(emulator));
        using var first = Content(100, seed: 4);
        using var second = Content(200, seed: 5);
        var applied = Assert.IsType<ObjectReplaceOutcome.Applied>((await store.Store.ReplaceAsync(ObjectName.Manifest, null, first, null, Token)).Value);
        store.Transport.FailNext(TransportFault.LoseAnswer, TransportDouble.IsUpload);

        var outcome = await store.Store.ReplaceAsync(ObjectName.Manifest, applied.Version, second, null, Token);

        Assert.Equal(1, store.Transport.Injected);
        var read = Assert.IsType<VersionedObject>(await store.Store.ReadVersionedAsync(ObjectName.Manifest, Token));
        using (read.Content)
        {
            Assert.Equal(second.ToArray(), read.Content.ToArray());
            Assert.NotEqual(applied.Version, read.Version);
        }

        ObjectReplaceOutcome expected = resumable ? new ObjectReplaceOutcome.Applied(read.Version) : ObjectReplaceOutcome.Mismatch.Instance;
        Assert.Equal(expected, outcome);
    }

    /// <summary>
    /// A create of an existing object, and a replace at a stale version, whose final answers are lost, so that the client
    /// never sees the service refuse the condition: each never answers <see cref="ObjectCreateOutcome.Created"/> or
    /// <see cref="ObjectReplaceOutcome.Applied"/>, and the other writer's content survives. On the testbench the client's
    /// session reports an object no write stored, which the store's read-back refutes.
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.GcsTestbench)]
    public async Task AWriteWhoseFailedConditionsAnswerIsLostNeverAnswersApplied(Emulator emulator)
    {
        await using var store = new CloudStore(emulator, emulators.Require(emulator));
        using var existing = Content(300, seed: 7);
        using var other = Content(300, seed: 8);
        Assert.IsType<ObjectCreateOutcome.Created>((await store.Store.CreateAsync(Name, existing, null, Token)).Value);
        var stale = Assert.IsType<ObjectReplaceOutcome.Applied>((await store.Store.ReplaceAsync(ObjectName.Manifest, null, existing, null, Token)).Value).Version;
        using var newer = Content(301, seed: 9);
        _ = Assert.IsType<ObjectReplaceOutcome.Applied>((await store.Store.ReplaceAsync(ObjectName.Manifest, stale, newer, null, Token)).Value);

        store.Transport.FailNext(TransportFault.LoseAnswer, TransportDouble.IsUpload);
        try
        {
            Assert.IsType<ObjectCreateOutcome.Exists>((await store.Store.CreateAsync(Name, other, null, Token)).Value);
        }
        catch (IOException)
        {
            // The store could not confirm the condition, so the outcome is unknown.
        }

        store.Transport.FailNext(TransportFault.LoseAnswer, TransportDouble.IsUpload);
        try
        {
            Assert.IsType<ObjectReplaceOutcome.Mismatch>((await store.Store.ReplaceAsync(ObjectName.Manifest, stale, other, null, Token)).Value);
        }
        catch (IOException)
        {
            // The store could not confirm the condition, so the outcome is unknown.
        }

        Assert.Equal(2, store.Transport.Injected);
        await AssertHoldsAsync(store.Store, existing);
        var manifest = Assert.IsType<VersionedObject>(await store.Store.ReadVersionedAsync(ObjectName.Manifest, Token));
        using (manifest.Content)
        {
            Assert.Equal(newer.ToArray(), manifest.Content.ToArray());
        }
    }

    /// <summary>
    /// An upload whose caller cancels while the transport still reads its body: the call ends cancelled, the caller
    /// releases its buffer and the pool reuses the pages, and the send then fails reading the disposed stream rather than
    /// reading other bytes; nothing lands.
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    public async Task ASendWhichOutlivesItsCancelledCallFailsRatherThanSendOtherBytes(Emulator emulator)
    {
        await using var store = new CloudStore(emulator, emulators.Require(emulator));
        store.Transport.FailNext(TransportFault.Stall, TransportDouble.IsUpload);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var content = Content(2 * ArcBufferPagePool.LargestPageSize, seed: 10);
        try
        {
            var create = store.Store.CreateAsync(Name, content, null, cancellation.Token).AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Token);
            await cancellation.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create);
        }
        finally
        {
            content.Dispose();
        }

        using (var reused = new ArcBufferWriter(2 * ArcBufferPagePool.LargestPageSize))
        {
            reused.Write(new byte[2 * ArcBufferPagePool.LargestPageSize]);
            store.Transport.ReleaseStall();
            var failure = await store.Transport.StalledBody;
            Assert.True(Chain(failure).OfType<ObjectDisposedException>().Any(), $"The stalled send ended with {failure}.");
        }

        Assert.Null(await store.Store.ReadVersionedAsync(Name, Token));
    }

    /// <summary>
    /// A download which breaks while another writer replaces the object: the read answers the content and version of one
    /// write, or throws, never a mix of the two.
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.FakeGcsServer)]
    public async Task ADownloadWhichBreaksWhileTheObjectIsReplacedAnswersOneWrite(Emulator emulator)
    {
        var endpoint = emulators.Require(emulator);
        await using var store = new CloudStore(emulator, endpoint);
        await using var writer = new CloudStore(emulator, endpoint, store.Prefix);
        using var first = Content(ArcBufferPagePool.LargestPageSize + 11, seed: 11);
        using var second = Content(ArcBufferPagePool.LargestPageSize + 11, seed: 12);
        var applied = Assert.IsType<ObjectReplaceOutcome.Applied>((await store.Store.ReplaceAsync(ObjectName.Manifest, null, first, null, Token)).Value);
        var replaced = false;
        store.Transport.BreakAfter = 64 * 1024;
        store.Transport.OnBreak = async () =>
        {
            if (!replaced)
            {
                replaced = true;
                _ = Assert.IsType<ObjectReplaceOutcome.Applied>((await writer.Store.ReplaceAsync(ObjectName.Manifest, applied.Version, second, null, Token)).Value);
            }
        };
        store.Transport.FailNext(TransportFault.BreakResponse, TransportDouble.IsDownload);

        VersionedObject? read;
        try
        {
            read = await store.Store.ReadVersionedAsync(ObjectName.Manifest, Token);
        }
        catch (Exception exception) when (exception is IOException or RequestFailedException or AmazonS3Exception or GoogleApiException)
        {
            read = null;
        }

        Assert.True(replaced);
        if (read is { } found)
        {
            using (found.Content)
            {
                var bytes = found.Content.ToArray();
                Assert.True(
                    bytes.AsSpan().SequenceEqual(first.ToArray()) && found.Version == applied.Version
                    || bytes.AsSpan().SequenceEqual(second.ToArray()) && found.Version != applied.Version,
                    "The read answered a mix of two writes, or content under another write's version.");
            }
        }
    }

    /// <summary>
    /// A versioned read of an object of several MiB comes back in pages of the largest size, not minimum pages.
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.FakeGcsServer)]
    public async Task AVersionedReadFillsTheLargestPages(Emulator emulator)
    {
        await using var store = new CloudStore(emulator, emulators.Require(emulator));
        const int Length = 3 * ArcBufferPagePool.LargestPageSize + 5;
        using var content = Content(Length, seed: 13);
        _ = await store.Store.ReplaceAsync(ObjectName.Manifest, null, content, null, Token);

        var read = Assert.IsType<VersionedObject>(await store.Store.ReadVersionedAsync(ObjectName.Manifest, Token));
        using (read.Content)
        {
            var pages = 0;
            foreach (var _ in read.Content.AsReadOnlySequence())
            {
                pages++;
            }

            Assert.InRange(pages, 1, (Length + ArcBufferPagePool.LargestPageSize - 1) / ArcBufferPagePool.LargestPageSize);
            Assert.Equal(content.ToArray(), read.Content.ToArray());
        }
    }

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>
    /// A create or replace whose writer's CRC-32C does not match its content is refused, and leaves the object as it was:
    /// S3 and Cloud Storage refuse the bytes for the checksum they carry, which over the emulators' plain HTTP is an
    /// unknown outcome, and Azure, whose <c>Put Blob</c> takes no CRC-32C, refuses them before any request with
    /// <see cref="ObjectContentChangedException"/>. The
    /// content's own CRC-32C then writes it. LocalStack refuses the replace but then serves the object it replaced with its
    /// length and no bytes, which the test pins, so an upgrade which fixes it fails here (test/Durable.Storage/docs/spec/verification.md §13).
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.FakeGcsServer)]
    [InlineData(Emulator.GcsTestbench)]
    public async Task AWriteWhoseCrc32CIsWrongIsRefusedAndLeavesTheObjectAsItWas(Emulator emulator)
    {
        await using var store = new CloudStore(emulator, emulators.Require(emulator));
        using var content = Content(ArcBufferPagePool.LargestPageSize + 17, seed: 7);
        using var replacement = Content(4096, seed: 8);
        var contentCrc32C = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, content.ToArray());
        var replacementCrc32C = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, replacement.ToArray());

        AssertRefused(emulator, await Assert.ThrowsAnyAsync<Exception>(async () => await store.Store.CreateAsync(Name, content, contentCrc32C ^ 1, Token)));
        Assert.Null(await store.Store.ReadVersionedAsync(Name, Token));

        Assert.IsType<ObjectCreateOutcome.Created>((await store.Store.CreateAsync(Name, content, contentCrc32C, Token)).Value);
        var read = Assert.IsType<VersionedObject>(await store.Store.ReadVersionedAsync(Name, Token));
        using (read.Content)
        {
            AssertRefused(emulator, await Assert.ThrowsAnyAsync<Exception>(async () => await store.Store.ReplaceAsync(Name, read.Version, replacement, replacementCrc32C ^ 1, Token)));
        }

        if (emulator == Emulator.LocalStack)
        {
            Assert.Equal(content.Length, Assert.Single(await store.Store.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token)).Length);
            _ = await Assert.ThrowsAnyAsync<IOException>(async () => await store.Store.ReadVersionedAsync(Name, Token));
        }
        else
        {
            await AssertHoldsAsync(store.Store, content);
        }

        Assert.IsType<ObjectReplaceOutcome.Applied>((await store.Store.ReplaceAsync(Name, read.Version, replacement, replacementCrc32C, Token)).Value);
        await AssertHoldsAsync(store.Store, replacement);
    }

    /// <summary>
    /// Requires <paramref name="refused"/> to be the refusal of content which does not match its writer's CRC-32C: the
    /// store's own, or the service's refusal over plain HTTP, an unknown outcome. The testbench answers a checksum failure with 412, which a read
    /// of the object then shows confirms no condition, so its outcome stays unknown.
    /// </summary>
    private static void AssertRefused(Emulator emulator, Exception refused)
    {
        switch (emulator)
        {
            case Emulator.Azurite:
                Assert.Null(Assert.IsType<ObjectContentChangedException>(refused).InnerException);
                break;
            case Emulator.LocalStack:
                var s3 = Assert.IsType<AmazonS3Exception>(Assert.IsType<IOException>(refused).InnerException);
                Assert.Equal((HttpStatusCode.BadRequest, "BadDigest"), (s3.StatusCode, s3.ErrorCode));
                break;
            case Emulator.FakeGcsServer:
                var gcs = Assert.IsType<GoogleApiException>(Assert.IsType<IOException>(refused).InnerException);
                Assert.Equal(HttpStatusCode.BadRequest, gcs.HttpStatusCode);
                break;
            case Emulator.GcsTestbench:
                _ = Assert.IsType<IOException>(refused);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(emulator));
        }
    }

    private static async Task AssertHoldsAsync(IObjectStore store, ArcBuffer content)
    {
        var read = Assert.IsType<VersionedObject>(await store.ReadVersionedAsync(Name, Token));
        using (read.Content)
        {
            Assert.Equal(content.ToArray(), read.Content.ToArray());
        }

        using var tail = Assert.IsType<ArcBuffer>(await store.ReadAsync(Name, content.Length - 9, 9, Token));
        Assert.Equal(content.ToArray()[^9..], tail.ToArray());
    }

    /// <summary>
    /// Returns <paramref name="length"/> seeded bytes held in pooled pages, so an upload spans several segments.
    /// </summary>
    private static ArcBuffer Content(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        using var writer = new ArcBufferWriter(length);
        writer.Write(bytes);
        return writer.ConsumeSlice(length);
    }
}
