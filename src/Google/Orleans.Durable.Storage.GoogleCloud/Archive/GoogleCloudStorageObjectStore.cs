using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Google;
using Google.Apis.Download;
using Google.Apis.Services;
using Google.Cloud.Storage.V1;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Polly;
using Polly.Retry;
using ObjectsPage = Google.Apis.Storage.v1.Data.Objects;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Orleans.Durable.Storage.GoogleCloud.Archive;

/// <summary>
/// Keeps the archive's objects in one Google Cloud Storage bucket (archive LSM §9).
/// </summary>
/// <remarks>
/// <para>
/// The object <c>name</c> is the object <c>{prefix}{name}</c>, and its generation is the object's version, so a version
/// names a write. A create or replace uploads through a resumable session with <c>ifGenerationMatch</c>, 0 for none,
/// which Cloud Storage checks when the session finalizes the object, so a write lands whole or not at all. Each upload's
/// metadata carries the <c>crc32c</c> of its content, its writer's or else one the store computes before the upload
/// leaves, and the client adds the <c>crc32c</c> of the bytes it read to the session's final request
/// (<c>X-Goog-Hash</c>). Durability and idempotency are as archive LSM §9 states for every cloud store. The client
/// retries only a request its caller marks idempotent, on an unsuccessful status alone, and marks no download, so the
/// store turns its retries off and repeats each idempotent operation itself on a 5xx, a 429, a 408, or a transport
/// failure, a lost answer among them (<see cref="IsTransient"/>); a conditional upload is never repeated.
/// </para>
/// <para>
/// No answer rests on a session's word alone. An upload answers <see cref="ObjectCreateOutcome.Created"/> or
/// <see cref="ObjectReplaceOutcome.Applied"/> only once a read of the generation it names shows the content's length,
/// crc32c, and MD5; and a failed condition answers <see cref="ObjectCreateOutcome.Exists"/> or
/// <see cref="ObjectReplaceOutcome.Mismatch"/> only once a read shows the object's state against the call's
/// expectation; otherwise the call throws and its outcome is unknown. A 404 answers that an object is absent only once
/// a listing of at most one object shows the bucket exists, which needs only the objects permissions the store needs.
/// </para>
/// <para>
/// An upload reads its content through an <see cref="ArcBufferReadStream"/>, disposed when the call returns, so a read
/// the client makes after that fails rather than reading a released page. A read writes the download into pooled pages.
/// </para>
/// </remarks>
public sealed class GoogleCloudStorageObjectStore : IObjectStore
{
    /// <summary>
    /// The length of each request of a resumable upload: a multiple of the client's 256 KiB unit.
    /// </summary>
    private const int UploadChunkLength = 16 * 1024 * 1024;

    /// <summary>
    /// The length of the buffer through which the client passes a download to its destination, which it allocates per
    /// download: one TLS record.
    /// </summary>
    private const int DownloadChunkLength = 16 * 1024;

    private const string OctetStream = "application/octet-stream";

    /// <summary>
    /// The repetitions of an idempotent operation which failed transiently, after its first attempt: Azure.Core's default
    /// count, so an outage surfaces as the operation's failure within seconds, as it does on the other stores.
    /// </summary>
    private const int Retries = 3;

    /// <summary>The wait before an idempotent operation's first repetition, which doubles for each after it.</summary>
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest wait before a repetition.</summary>
    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The options of every delete, which nothing changes: the store repeats a delete itself (<see cref="DeleteAsync"/>).
    /// </summary>
    private static readonly DeleteObjectOptions DeleteOptions = new() { RetryOptions = RetryOptions.Never };

    /// <summary>
    /// The options of every read of an object's current metadata, which nothing changes: the store repeats the read itself.
    /// </summary>
    private static readonly GetObjectOptions MetadataOptions = new() { Projection = Projection.NoAcl, RetryOptions = RetryOptions.Never };

    private readonly StorageClient _client;
    private readonly bool _overTls;
    private readonly string _bucketName;
    private readonly string _prefix;
    private readonly ResiliencePipeline _retry;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleCloudStorageObjectStore"/> class.
    /// </summary>
    /// <param name="client">The storage client.</param>
    /// <param name="bucketName">The bucket which holds the objects, which exists.</param>
    /// <param name="prefix">The object-name prefix of every object, empty or ending in <c>/</c>.</param>
    /// <param name="timeProvider">The clock of the waits before repetitions, or the system's.</param>
    public GoogleCloudStorageObjectStore(StorageClient client, string bucketName, string? prefix = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        prefix ??= string.Empty;
        if (prefix.Length > 0 && prefix[^1] != '/')
        {
            throw new ArgumentException("A non-empty Cloud Storage object store prefix ends in '/'.", nameof(prefix));
        }

        _client = client;
        _overTls = client.Service.BaseUri.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
        _bucketName = bucketName;
        _prefix = prefix;
        _retry = new ResiliencePipelineBuilder { TimeProvider = timeProvider ?? TimeProvider.System }
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = static arguments => ValueTask.FromResult(arguments.Outcome.Exception is { } failure && IsTransient(failure, arguments.Context.CancellationToken)),
                MaxRetryAttempts = Retries,
                DelayGenerator = static arguments => ValueTask.FromResult<TimeSpan?>(Backoff.Delay(arguments.AttemptNumber, FirstRetryDelay, LongestRetryDelay, Random.Shared.NextDouble())),
            })
            .Build();
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var crc32cBase64 = ObjectStoreChecksums.Crc32CBase64(ObjectStoreChecksums.Crc32CToSend(content, crc32c));
        if (await UploadAsync(name, expectedGeneration: 0, content, crc32cBase64, cancellationToken).ConfigureAwait(false) is { } generation)
        {
            await VerifyStoredAsync(name, generation, content, crc32cBase64, cancellationToken).ConfigureAwait(false);
            return ObjectCreateOutcome.Created.Instance;
        }

        return await CurrentGenerationAsync(name, cancellationToken).ConfigureAwait(false) is not null
            ? ObjectCreateOutcome.Exists.Instance
            : throw UnconfirmedCondition(name);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Idempotent: a read changes nothing, so a repetition downloads the range again, into pages of its own.
    /// </remarks>
    public async ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfNotRange(offset, length);
        var (downloaded, range) = await _retry
            .ExecuteAsync(static (state, cancellationToken) => state.Store.DownloadRangeAsync(state.Name, state.Offset, state.Length, cancellationToken), (Store: this, Name: name, Offset: offset, Length: length), cancellationToken)
            .ConfigureAwait(false);
        if (downloaded is null)
        {
            await ThrowIfBucketMissingAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        using var writer = downloaded;
        var served = range
            ?? throw new IOException(string.Create(CultureInfo.InvariantCulture, $"Cloud Storage answered a ranged read of '{name}' without a Content-Range."));
        ObjectStoreResponses.ThrowIfNotServed(name, offset, length, served.From ?? -1, writer.Length, served.Length ?? -1);
        return writer.ConsumeSlice(length);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One metadata read: the object's size and the <c>crc32c</c> Cloud Storage keeps for every object.
    /// </remarks>
    public async ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        StorageObject stored;
        try
        {
            stored = await GetObjectAsync(name, MetadataOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            await ThrowIfBucketMissingAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var length = SizeOf(stored);
        return length == 0
            ? new ObjectProperties(0, null)
            : new ObjectProperties(length, ObjectStoreChecksums.Crc32CFromBase64(stored.Crc32c)
                ?? throw new IOException($"Cloud Storage answered the properties of '{name}' without its crc32c."));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
    {
        ObjectStoreArguments.ThrowIfNotPrefix(prefix);
        ObjectStoreArguments.ThrowIfNotStart(startAfter);
        return ListObjectsAsync(prefix, startAfter, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Idempotent: a name created again holds the content it held (the callers' demand), so a repetition after a delete
    /// whose answer was lost deletes the same content or finds the object absent, and its 404 completes the delete as the
    /// first's answer would have.
    /// </remarks>
    public async ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            await _retry
                .ExecuteAsync(static (state, cancellationToken) => new ValueTask(state.Store._client.DeleteObjectAsync(state.Store._bucketName, state.Name, DeleteOptions, cancellationToken)), (Store: this, Name: StorageNameOf(name)), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // The object is absent, as the delete leaves it, once the bucket is seen to exist.
            await ThrowIfBucketMissingAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Idempotent: a read changes nothing, so a repetition downloads the object again, into pages of its own.
    /// </remarks>
    public async ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (await _retry
            .ExecuteAsync(static (state, cancellationToken) => state.Store.DownloadAsync(state.Name, cancellationToken), (Store: this, Name: name), cancellationToken)
            .ConfigureAwait(false) is not var (downloaded, stored))
        {
            await ThrowIfBucketMissingAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        using var writer = downloaded;
        if (stored.Generation is not { } generation)
        {
            throw new IOException(string.Create(CultureInfo.InvariantCulture, $"Cloud Storage answered a read of '{name}' without the object's generation."));
        }

        return new VersionedObject(writer.ConsumeSlice(writer.Length), VersionOf(generation));
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectReplaceOutcome> ReplaceAsync(
        ObjectName name,
        ObjectVersion? expectedVersion,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfEmpty(content);
        long expectedGeneration = 0;
        if (expectedVersion is not null)
        {
            if (GenerationOf(expectedVersion) is not { } expected)
            {
                // Every version this store gives is a positive generation, so no object holds this one.
                return ObjectReplaceOutcome.Mismatch.Instance;
            }

            expectedGeneration = expected;
        }

        var crc32cBase64 = ObjectStoreChecksums.Crc32CBase64(ObjectStoreChecksums.Crc32CToSend(content, crc32c));
        if (await UploadAsync(name, expectedGeneration, content, crc32cBase64, cancellationToken).ConfigureAwait(false) is { } generation)
        {
            await VerifyStoredAsync(name, generation, content, crc32cBase64, cancellationToken).ConfigureAwait(false);
            return new ObjectReplaceOutcome.Applied(VersionOf(generation));
        }

        // The condition failed: a mismatch holds only where the object's durable state contradicts the expectation.
        var current = await CurrentGenerationAsync(name, cancellationToken).ConfigureAwait(false);
        var contradicts = expectedVersion is null ? current is not null : current != expectedGeneration;
        return contradicts ? ObjectReplaceOutcome.Mismatch.Instance : throw UnconfirmedCondition(name);
    }

    /// <summary>
    /// Returns whether <paramref name="exception"/> may report that an upload's condition failed: a 412 which names no
    /// reason but <c>conditionNotMet</c>, which a read of the object then confirms or refutes.
    /// </summary>
    internal static bool IsConditionFailure(GoogleApiException exception) =>
        exception.HttpStatusCode == HttpStatusCode.PreconditionFailed
        && (exception.Error?.Errors is not { Count: > 0 } errors
            || errors.Any(static error => string.Equals(error.Reason, "conditionNotMet", StringComparison.Ordinal)));

    /// <summary>
    /// Returns whether <paramref name="exception"/> is the service's refusal of an upload's bytes for their checksum: a 400
    /// which names the crc32c or MD5 they failed, which <see cref="ObjectContentChangedException.ForServiceRefusal"/> raises
    /// by the transport.
    /// </summary>
    internal static bool IsChecksumRefusal(GoogleApiException exception) =>
        exception.HttpStatusCode == HttpStatusCode.BadRequest
        && (exception.Message.Contains("crc32c", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("md5", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns whether <paramref name="failure"/> may pass when its idempotent operation is repeated: a 5xx, a 429, or a 408
    /// the service answered, or a failure of the transport, such as a lost answer, a connection which broke mid-body, or
    /// the client's own timeout; never a cancellation of <paramref name="cancellationToken"/>, nor a range past the
    /// object's end, which is the read's answer.
    /// </summary>
    internal static bool IsTransient(Exception failure, CancellationToken cancellationToken) => failure switch
    {
        GoogleApiException { HttpStatusCode: var status } => (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout,
        EndOfStreamException => false,
        HttpRequestException or IOException => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    /// <summary>
    /// Returns the positive generation <paramref name="version"/> names, or <see langword="null"/> when it names none.
    /// </summary>
    private static long? GenerationOf(ObjectVersion version) =>
        long.TryParse(version.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) && generation > 0 ? generation : null;

    private static ObjectVersion VersionOf(long generation) => new(generation.ToString(CultureInfo.InvariantCulture));

    private static IOException UnconfirmedCondition(ObjectName name) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Cloud Storage refused a write of '{name}' for its condition, which a read of the object does not confirm, so the write's outcome is unknown."));

    private async IAsyncEnumerable<ListedObject> ListObjectsAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Cloud Storage's start offset is inclusive, so the name itself, when it exists, is skipped below.
        var startOffset = startAfter is null ? null : _prefix + startAfter;
        string? pageToken = null;
        do
        {
            // Idempotent: a listing changes nothing, and a repetition asks for the same page by its token.
            var page = await ListPageAsync(_prefix + prefix, pageToken, pageSize: null, "items(name,size),nextPageToken", cancellationToken, startOffset).ConfigureAwait(false);
            foreach (var listed in page.Items ?? [])
            {
                // An object whose name maps to no object name is not the store's, so the listing skips it.
                var name = listed.Name[_prefix.Length..];
                if (ObjectName.IsValid(name) && ObjectStoreArguments.IsAfter(name, startAfter))
                {
                    yield return new ListedObject(new ObjectName(name), SizeOf(listed));
                }
            }

            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);
    }

    /// <summary>
    /// Returns one page of the listing of the objects under <paramref name="prefix"/>, from <paramref name="pageToken"/>,
    /// repeating its request while it fails transiently.
    /// </summary>
    private ValueTask<ObjectsPage> ListPageAsync(string prefix, string? pageToken, int? pageSize, string fields, CancellationToken cancellationToken, string? startOffset = null) =>
        _retry.ExecuteAsync(
            static async (state, cancellationToken) =>
            {
                var options = new ListObjectsOptions { PageToken = state.PageToken, PageSize = state.PageSize, Fields = state.Fields, StartOffset = state.StartOffset, RetryOptions = RetryOptions.Never };
                var pages = state.Store._client.ListObjectsAsync(state.Store._bucketName, state.Prefix, options).AsRawResponses().GetAsyncEnumerator(cancellationToken);
                try
                {
                    return await pages.MoveNextAsync().ConfigureAwait(false)
                        ? pages.Current
                        : throw new IOException("Cloud Storage answered a listing with no page.");
                }
                finally
                {
                    await pages.DisposeAsync().ConfigureAwait(false);
                }
            },
            (Store: this, Prefix: prefix, PageToken: pageToken, PageSize: pageSize, Fields: fields, StartOffset: startOffset),
            cancellationToken);

    /// <summary>
    /// Reads an object's metadata, repeating the read while it fails transiently: idempotent, since a read changes nothing.
    /// </summary>
    private ValueTask<StorageObject> GetObjectAsync(ObjectName name, GetObjectOptions options, CancellationToken cancellationToken) =>
        _retry.ExecuteAsync(
            static (state, cancellationToken) => new ValueTask<StorageObject>(state.Store._client.GetObjectAsync(state.Store._bucketName, state.Name, state.Options, cancellationToken)),
            (Store: this, Name: StorageNameOf(name), Options: options),
            cancellationToken);

    /// <summary>
    /// Downloads one range of the object into pages of its own, which it releases when the download fails, and returns them
    /// with the range the service answered, or no pages when the object is absent.
    /// </summary>
    /// <exception cref="EndOfStreamException">The range starts at or past the object's end.</exception>
    private async ValueTask<(ArcBufferWriter? Writer, ContentRangeHeaderValue? Range)> DownloadRangeAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken)
    {
        ArcBufferWriter? writer = new(length);
        try
        {
            var downloader = new RangeDownloader(_client.Service)
            {
                Range = new RangeHeaderValue(offset, offset + length - 1),
                ChunkSize = Math.Min(length, DownloadChunkLength),
            };
            IDownloadProgress progress;
            using (var sink = new ArcBufferWriteStream(writer))
            {
                progress = await downloader.DownloadAsync(MediaUriOf(name), sink, cancellationToken).ConfigureAwait(false);
            }

            if (progress.Status == DownloadStatus.Completed)
            {
                var downloaded = writer;
                writer = null;
                return (downloaded, downloader.ContentRange);
            }

            switch (progress.Exception)
            {
                case GoogleApiException { HttpStatusCode: HttpStatusCode.NotFound }:
                    return (null, null);
                case GoogleApiException { HttpStatusCode: HttpStatusCode.RequestedRangeNotSatisfiable } exception:
                    // The range starts at or past the object's end; an object holds at least one byte.
                    throw new EndOfStreamException(string.Create(CultureInfo.InvariantCulture, $"The object '{name}' ends at or before {offset}."), exception);
                case { } exception:
                    ExceptionDispatchInfo.Throw(exception);
                    throw new UnreachableException();
                default:
                    throw new IOException(string.Create(CultureInfo.InvariantCulture, $"Cloud Storage ended a read of '{name}' in state {progress.Status}."));
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    /// <summary>
    /// Downloads the whole object into pages of its own, which it releases when the download fails, and returns them with
    /// the object's metadata, or <see langword="null"/> when the object is absent.
    /// </summary>
    private async ValueTask<(ArcBufferWriter Writer, StorageObject Stored)?> DownloadAsync(ObjectName name, CancellationToken cancellationToken)
    {
        // Its length is unknown before the download, so the writer takes the largest pages.
        ArcBufferWriter? writer = new(int.MaxValue);
        try
        {
            // One download serves the content and its generation, and the client checks the content's crc32c.
            StorageObject stored;
            using (var sink = new ArcBufferWriteStream(writer))
            {
                stored = await _client
                    .DownloadObjectAsync(_bucketName, StorageNameOf(name), sink, new DownloadObjectOptions { ChunkSize = DownloadChunkLength }, cancellationToken)
                    .ConfigureAwait(false);
            }

            var downloaded = writer;
            writer = null;
            return (downloaded, stored);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        finally
        {
            writer?.Dispose();
        }
    }

    /// <summary>
    /// Uploads <paramref name="content"/>, whose base64 CRC-32C is <paramref name="crc32cBase64"/>, on the condition that the object's
    /// generation is <paramref name="expectedGeneration"/>, 0 for none, and returns the generation the client reports, or
    /// <see langword="null"/> when the service refused the condition.
    /// </summary>
    private async ValueTask<long?> UploadAsync(ObjectName name, long expectedGeneration, ArcBuffer content, string crc32cBase64, CancellationToken cancellationToken)
    {
        StorageObject uploaded;
        using (var stream = new ArcBufferReadStream(content))
        {
            try
            {
                uploaded = await _client
                    .UploadObjectAsync(
                        new StorageObject { Bucket = _bucketName, Name = StorageNameOf(name), ContentType = OctetStream, Crc32c = crc32cBase64 },
                        stream,
                        new UploadObjectOptions { IfGenerationMatch = expectedGeneration, ChunkSize = UploadChunkLength },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GoogleApiException exception) when (IsConditionFailure(exception))
            {
                return null;
            }
            catch (GoogleApiException exception) when (IsChecksumRefusal(exception))
            {
                throw ObjectContentChangedException.ForServiceRefusal(name, exception, _overTls);
            }
        }

        return uploaded?.Generation
            ?? throw new IOException("Cloud Storage answered an applied upload without the object's generation, so the write's outcome is unknown. Re-read the object to establish the operation's goal.");
    }

    /// <summary>
    /// Returns once a read shows the object holds <paramref name="content"/> at <paramref name="generation"/>, which an
    /// upload reported: its length, its MD5, and <paramref name="crc32cBase64"/>, the CRC-32C the upload carried.
    /// </summary>
    /// <remarks>
    /// A client which loses a resumable session's final answer asks the session for its state and reports the object the
    /// answer names. Google's testbench answers a session whose final request failed its condition with an object no
    /// write stored, and the service documents no answer for that case, so the upload's answer stands only once this
    /// read confirms it.
    /// </remarks>
    /// <exception cref="IOException">The object does not hold the content at the generation, so the write's outcome is unknown.</exception>
    private async ValueTask VerifyStoredAsync(ObjectName name, long generation, ArcBuffer content, string crc32cBase64, CancellationToken cancellationToken)
    {
        StorageObject stored;
        try
        {
            stored = await GetObjectAsync(name, new GetObjectOptions { Generation = generation, Projection = Projection.NoAcl, RetryOptions = RetryOptions.Never }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw new IOException(string.Create(CultureInfo.InvariantCulture, $"Cloud Storage answered an upload of '{name}' with generation {generation}, which the object does not hold, so the write's outcome is unknown."), exception);
        }

        if (SizeOf(stored) != content.Length
            || !string.Equals(stored.Crc32c, crc32cBase64, StringComparison.Ordinal)
            || !string.Equals(stored.Md5Hash, Md5Base64(content), StringComparison.Ordinal))
        {
            throw new IOException(string.Create(CultureInfo.InvariantCulture, $"Cloud Storage answered an upload of '{name}' with generation {generation}, which holds other content, so the write's outcome is unknown."));
        }
    }

    /// <summary>
    /// Returns an object's size, which an answer in the proto3 JSON mapping, as Google's testbench gives, omits when it is
    /// 0, the field's default.
    /// </summary>
    private static long SizeOf(Google.Apis.Storage.v1.Data.Object stored) => checked((long)(stored.Size ?? 0));

    /// <summary>
    /// Returns the object's current generation, or <see langword="null"/> when it is absent and the bucket exists.
    /// </summary>
    private async ValueTask<long?> CurrentGenerationAsync(ObjectName name, CancellationToken cancellationToken)
    {
        try
        {
            var current = await GetObjectAsync(name, MetadataOptions, cancellationToken).ConfigureAwait(false);
            return current.Generation
                ?? throw new IOException(string.Create(CultureInfo.InvariantCulture, $"Cloud Storage answered a read of '{name}' without the object's generation."));
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            await ThrowIfBucketMissingAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Throws when the store's bucket does not exist, so a 404 is an object's absence only in a bucket which does.
    /// </summary>
    /// <remarks>
    /// One request of a listing of at most one object checks the bucket: it needs only <c>storage.objects.list</c>, which
    /// the store's listings already need, where reading the bucket needs <c>storage.buckets.get</c>, which the roles
    /// that grant object access (<c>roles/storage.objectUser</c>, <c>roles/storage.objectAdmin</c>) lack.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The bucket does not exist.</exception>
    private async ValueTask ThrowIfBucketMissingAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await ListPageAsync(_prefix, pageToken: null, pageSize: 1, "nextPageToken", cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The Cloud Storage bucket '{_bucketName}' does not exist."), exception);
        }
    }

    private static string Md5Base64(ArcBuffer content)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (var segment in content)
        {
            md5.AppendData(segment);
        }

        return Convert.ToBase64String(md5.GetHashAndReset());
    }

    private string MediaUriOf(ObjectName name) =>
        string.Concat(_client.Service.BaseUri, "b/", Uri.EscapeDataString(_bucketName), "/o/", Uri.EscapeDataString(StorageNameOf(name)));

    private string StorageNameOf(ObjectName name) => _prefix + name.Value;

    /// <summary>
    /// A download of one range which records the range the service answered with, so the read can check it.
    /// </summary>
    private sealed class RangeDownloader(IClientService service) : MediaDownloader(service)
    {
        public ContentRangeHeaderValue? ContentRange { get; private set; }

        protected override void OnResponseReceived(HttpResponseMessage response)
        {
            base.OnResponseReceived(response);
            ContentRange = response.Content.Headers.ContentRange;
        }
    }
}
