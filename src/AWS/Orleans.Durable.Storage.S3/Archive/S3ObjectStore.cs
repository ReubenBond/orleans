using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.S3.Archive;

/// <summary>
/// Keeps the archive's objects in one Amazon S3 bucket (object-store.md §9).
/// </summary>
/// <remarks>
/// <para>
/// In a general-purpose bucket the object <c>name</c> is the key <c>{prefix}{name}</c>, and a listing uses
/// <c>ListObjectsV2</c> with <c>StartAfter</c>. In a directory bucket (<c>--x-s3</c>), whose <c>ListObjectsV2</c>
/// returns entries unordered and refuses <c>StartAfter</c>, the store maps each object name onto the hierarchical
/// directory layout of <see cref="S3DirectoryBucketLayout"/> (<c>research/papers/logdrive/paper.md</c> §3.2) and lists
/// lazily in ordinal order by enumerating and sorting one <c>Delimiter = "/"</c> directory at a time while pruning
/// subtrees outside <c>prefix</c> or at or below <c>startAfter</c>.
/// </para>
/// <para>
/// An object's ETag is its version (object-store.md §2.1): in a general-purpose bucket a single-part upload's ETag is a
/// digest of the content, so it names content rather than a write, while in a directory bucket an ETag is an opaque
/// random string rather than a content digest. A create or replace is one <c>PutObject</c>, with <c>If-None-Match: *</c>
/// or <c>If-Match</c> and the <c>x-amz-checksum-crc32c</c> of its content: its writer's CRC-32C, or else one the store
/// computes before the request leaves. The endpoint honours those conditions on <c>PutObject</c>, as Amazon S3 does. Over
/// HTTPS the payload is not signed: the checksum covers it, and signing would hash every byte again. Durability, retries,
/// and idempotency are as object-store.md §9 states for every cloud store; a conditional write which S3 refuses for a
/// concurrent one of the same key (409 <c>ConditionalRequestConflict</c>) throws, leaving its outcome unknown.
/// </para>
/// <para>
/// An upload reads its content through an <see cref="ArcBufferReadStream"/>, disposed when the call returns, so a read
/// the SDK makes after that fails rather than reading a released page. A read fills pooled pages straight from the
/// response.
/// </para>
/// </remarks>
public sealed class S3ObjectStore : IObjectStore
{
    private readonly IAmazonS3 _client;
    private readonly string _bucketName;
    private readonly string _prefix;
    private readonly bool _directoryBucket;
    private readonly bool _overTls;

    /// <summary>
    /// Initializes a new instance of the <see cref="S3ObjectStore"/> class.
    /// </summary>
    /// <param name="client">The S3 client.</param>
    /// <param name="bucketName">
    /// The bucket which holds the objects, which exists: a general-purpose bucket or an S3 Express One Zone directory
    /// bucket (ending in <c>--x-s3</c>).
    /// </param>
    /// <param name="prefix">The key prefix of every object, empty or ending in <c>/</c>.</param>
    public S3ObjectStore(IAmazonS3 client, string bucketName, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(bucketName);

        prefix ??= string.Empty;
        if (prefix.Length > 0 && prefix[^1] != '/')
        {
            throw new ArgumentException("A non-empty S3 object store prefix ends in '/'.", nameof(prefix));
        }

        _client = client;
        _bucketName = bucketName;
        _prefix = prefix;
        _directoryBucket = S3DirectoryBucketLayout.IsDirectoryBucket(bucketName);

        // The SDK sends an unsigned payload only over HTTPS, and a checksum refusal means changed content only over TLS.
        _overTls = !client.Config.UseHttp
            && !(client.Config.ServiceURL?.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            _ = await PutAsync(name, expectedETag: null, content, crc32c, cancellationToken).ConfigureAwait(false);
            return ObjectCreateOutcome.Created.Instance;
        }
        catch (AmazonS3Exception exception) when (IsConditionFailure(exception, expectedAbsent: true))
        {
            return ObjectCreateOutcome.Exists.Instance;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfNotRange(offset, length);
        GetObjectResponse response;
        try
        {
            response = await _client
                .GetObjectAsync(
                    new GetObjectRequest { BucketName = _bucketName, Key = KeyOf(name), ByteRange = new ByteRange(offset, offset + length - 1) },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsNoSuchKey(exception))
        {
            return null;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The range starts at or past the object's end, as any range of an empty object does.
            throw new EndOfStreamException(string.Create(CultureInfo.InvariantCulture, $"The object '{name}' ends at or before {offset}."), exception);
        }

        using (response)
        {
            using var writer = new ArcBufferWriter(length);
            await ArcBufferWriteStream.CopyFromAsync(response.ResponseStream, writer, cancellationToken).ConfigureAwait(false);
            var range = ContentRangeOf(name, response.ContentRange);
            ObjectStoreResponses.ThrowIfNotServed(name, offset, length, range.From ?? -1, writer.Length, range.Length ?? -1);
            return writer.ConsumeSlice(length);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One <c>HeadObject</c> with checksums enabled: the CRC-32C each upload carries, which S3 keeps as the object's
    /// checksum.
    /// </remarks>
    public async ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        GetObjectMetadataResponse response;
        try
        {
            response = await _client
                .GetObjectMetadataAsync(
                    new GetObjectMetadataRequest { BucketName = _bucketName, Key = KeyOf(name), ChecksumMode = ChecksumMode.ENABLED },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // A HEAD answers 404 without a body, so with no NoSuchKey code: the object is absent once the bucket is seen to
            // exist.
            await ThrowIfBucketMissingAsync(exception, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var length = response.ContentLength;
        return length == 0
            ? new ObjectProperties(0, null)
            : new ObjectProperties(length, ObjectStoreChecksums.Crc32CFromBase64(response.ChecksumCRC32C)
                ?? throw new IOException($"S3 answered the properties of '{name}' without the CRC-32C its upload carried."));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
    {
        ObjectStoreArguments.ThrowIfNotPrefix(prefix);
        ObjectStoreArguments.ThrowIfNotStart(startAfter);
        return _directoryBucket
            ? ListDirectoryBucketAsync(prefix, startAfter, cancellationToken)
            : ListObjectsAsync(prefix, startAfter, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        _ = await _client
            .DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucketName, Key = KeyOf(name) }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        GetObjectResponse response;
        try
        {
            response = await _client
                .GetObjectAsync(new GetObjectRequest { BucketName = _bucketName, Key = KeyOf(name) }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsNoSuchKey(exception))
        {
            return null;
        }

        using (response)
        {
            // One GetObject serves the content and its ETag.
            var length = response.ContentLength;
            if (length is < 0 or > int.MaxValue)
            {
                throw new IOException(string.Create(CultureInfo.InvariantCulture, $"S3 answered a read of '{name}' with a length of {length}, which no object has."));
            }

            if (string.IsNullOrWhiteSpace(response.ETag))
            {
                throw new IOException($"S3 answered a versioned read of '{name}' without an ETag.");
            }

            var version = new ObjectVersion(response.ETag);
            using var writer = new ArcBufferWriter((int)length);
            await ArcBufferWriteStream.CopyFromAsync(response.ResponseStream, writer, cancellationToken).ConfigureAwait(false);
            if (writer.Length != length)
            {
                throw new IOException(string.Create(CultureInfo.InvariantCulture, $"S3 answered {writer.Length} bytes of '{name}' where its response promised {length}."));
            }

            return new VersionedObject(writer.ConsumeSlice(writer.Length), version);
        }
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
        try
        {
            var etag = await PutAsync(name, expectedVersion?.Value, content, crc32c, cancellationToken).ConfigureAwait(false);
            return new ObjectReplaceOutcome.Applied(new ObjectVersion(etag));
        }
        catch (AmazonS3Exception exception) when (IsConditionFailure(exception, expectedAbsent: expectedVersion is null))
        {
            return ObjectReplaceOutcome.Mismatch.Instance;
        }
    }

    /// <summary>
    /// Returns whether <paramref name="exception"/> reports that a write's condition failed against the object's durable
    /// state: 412 <c>PreconditionFailed</c>, or, for a write which expected an object, 404 <c>NoSuchKey</c>.
    /// </summary>
    private static bool IsConditionFailure(AmazonS3Exception exception, bool expectedAbsent) =>
        exception.StatusCode == HttpStatusCode.PreconditionFailed || !expectedAbsent && IsNoSuchKey(exception);

    /// <summary>
    /// Returns whether <paramref name="exception"/> is the service's refusal of a write's bytes for their checksum, 400
    /// <c>BadDigest</c>, which <see cref="ObjectContentChangedException.ForServiceRefusal"/> raises by the transport.
    /// </summary>
    private static bool IsChecksumRefusal(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.BadRequest && string.Equals(exception.ErrorCode, "BadDigest", StringComparison.Ordinal);

    /// <summary>
    /// Throws when the store's bucket does not exist, so a HEAD's 404 is an object's absence only in a bucket which does.
    /// One listing of at most one key checks it, which needs only the <c>s3:ListBucket</c> permission the store's listings
    /// already need.
    /// </summary>
    /// <exception cref="ObjectStoreRefusedException">The bucket does not exist.</exception>
    private async ValueTask ThrowIfBucketMissingAsync(AmazonS3Exception notFound, CancellationToken cancellationToken)
    {
        try
        {
            var request = new ListObjectsV2Request
            {
                BucketName = _bucketName,
                Prefix = _directoryBucket && _prefix.Length == 0 ? null : _prefix,
                MaxKeys = 1,
            };
            if (_directoryBucket)
            {
                request.Delimiter = "/";
            }

            _ = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound && string.Equals(exception.ErrorCode, "NoSuchBucket", StringComparison.Ordinal))
        {
            throw new ObjectStoreRefusedException($"S3 has no bucket '{_bucketName}', which the object store's objects lie in.", notFound, isAuthorization: false);
        }
    }

    private static bool IsNoSuchKey(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound && string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal);

    private static ContentRangeHeaderValue ContentRangeOf(ObjectName name, string? contentRange) =>
        ContentRangeHeaderValue.TryParse(contentRange, out var range)
            ? range
            : throw new IOException(string.Create(CultureInfo.InvariantCulture, $"S3 answered a ranged read of '{name}' with the Content-Range '{contentRange}'."));

    private async IAsyncEnumerable<ListedObject> ListObjectsAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? continuation = null;
        do
        {
            var request = new ListObjectsV2Request { BucketName = _bucketName, Prefix = _prefix + prefix, ContinuationToken = continuation };
            if (startAfter is not null && continuation is null)
            {
                request.StartAfter = _prefix + startAfter;
            }

            var page = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            if (page.S3Objects is { } objects)
            {
                foreach (var listed in objects)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // An object whose key maps to no object name is not the store's, so the listing skips it.
                    var name = listed.Key[_prefix.Length..];
                    if (ObjectName.IsValid(name) && ObjectStoreArguments.IsAfter(name, startAfter) && listed.Size is { } length)
                    {
                        yield return new ListedObject(new ObjectName(name), length);
                    }
                }
            }

            continuation = NextContinuationTokenOf(page);
        }
        while (continuation is not null);
    }

    private async IAsyncEnumerable<ListedObject> ListDirectoryBucketAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!S3DirectoryBucketLayout.TryGetStartingDirectory(_prefix, prefix, startAfter, out var startLogicalPrefix, out var startS3Prefix))
        {
            yield break;
        }

        var rootEntries = await ListDirectoryEntriesAsync(startLogicalPrefix, startS3Prefix, prefix, startAfter, cancellationToken).ConfigureAwait(false);
        if (rootEntries.Count == 0)
        {
            yield break;
        }

        var stack = new Stack<DirectoryCursor>();
        stack.Push(new DirectoryCursor(rootEntries, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cursor = stack.Pop();
            var entry = cursor.Entries[cursor.Index];
            if (cursor.Index + 1 < cursor.Entries.Count)
            {
                stack.Push(cursor with { Index = cursor.Index + 1 });
            }

            if (entry.S3Prefix is not { } childS3Prefix)
            {
                yield return new ListedObject(new ObjectName(entry.LogicalName), entry.Size);
                continue;
            }

            var children = await ListDirectoryEntriesAsync(entry.LogicalName, childS3Prefix, prefix, startAfter, cancellationToken).ConfigureAwait(false);
            if (children.Count > 0)
            {
                stack.Push(new DirectoryCursor(children, 0));
            }
        }
    }

    private async ValueTask<List<DirectoryEntry>> ListDirectoryEntriesAsync(
        string currentLogicalPrefix,
        string s3DirectoryPrefix,
        string prefix,
        string? startAfter,
        CancellationToken cancellationToken)
    {
        var entries = new List<DirectoryEntry>();
        string? continuation = null;
        do
        {
            var request = new ListObjectsV2Request
            {
                BucketName = _bucketName,
                Prefix = s3DirectoryPrefix.Length == 0 ? null : s3DirectoryPrefix,
                Delimiter = "/",
                ContinuationToken = continuation,
            };

            var page = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            if (page.CommonPrefixes is { } commonPrefixes)
            {
                foreach (var commonPrefix in commonPrefixes)
                {
                    if (commonPrefix is null)
                    {
                        throw new IOException("S3 answered a listing page with a null common prefix.");
                    }

                    if (S3DirectoryBucketLayout.TryDecodeChildDirectory(s3DirectoryPrefix, currentLogicalPrefix, commonPrefix, out var childLogicalPrefix)
                        && S3DirectoryBucketLayout.ShouldDescend(childLogicalPrefix, prefix, startAfter))
                    {
                        entries.Add(new DirectoryEntry(childLogicalPrefix, commonPrefix, Size: 0));
                    }
                }
            }

            if (page.S3Objects is { } objects)
            {
                foreach (var listed in objects)
                {
                    if (listed.Key is null)
                    {
                        throw new IOException("S3 answered a listing page with a null object key.");
                    }

                    if (S3DirectoryBucketLayout.TryDecodeLeaf(s3DirectoryPrefix, currentLogicalPrefix, listed.Key, out var logicalName)
                        && logicalName.StartsWith(prefix, StringComparison.Ordinal)
                        && ObjectStoreArguments.IsAfter(logicalName, startAfter))
                    {
                        if (listed.Size is not { } length || length < 0)
                        {
                            throw new IOException(string.Create(CultureInfo.InvariantCulture, $"S3 answered listed object '{logicalName}' with invalid size '{listed.Size}'."));
                        }

                        entries.Add(new DirectoryEntry(logicalName, S3Prefix: null, Size: length));
                    }
                }
            }

            continuation = NextContinuationTokenOf(page);
        }
        while (continuation is not null);

        SortAndDeduplicate(entries);
        return entries;
    }

    private static string? NextContinuationTokenOf(ListObjectsV2Response page)
    {
        if (page.IsTruncated != true)
        {
            return null;
        }

        return string.IsNullOrEmpty(page.NextContinuationToken)
            ? throw new IOException("S3 answered a truncated listing page without a continuation token.")
            : page.NextContinuationToken;
    }

    private static void SortAndDeduplicate(List<DirectoryEntry> entries)
    {
        entries.Sort(static (left, right) =>
        {
            var order = string.CompareOrdinal(left.LogicalName, right.LogicalName);
            return order != 0 ? order : left.IsDirectory.CompareTo(right.IsDirectory);
        });

        // Deduplicate entries that an unordered paginated listing may repeat across pages.
        var write = 0;
        for (var read = 0; read < entries.Count; read++)
        {
            var candidate = entries[read];
            if (write > 0
                && entries[write - 1].IsDirectory == candidate.IsDirectory
                && string.Equals(entries[write - 1].LogicalName, candidate.LogicalName, StringComparison.Ordinal))
            {
                continue;
            }

            if (write != read)
            {
                entries[write] = candidate;
            }

            write++;
        }

        if (write < entries.Count)
        {
            entries.RemoveRange(write, entries.Count - write);
        }
    }

    private async ValueTask<string> PutAsync(ObjectName name, string? expectedETag, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken)
    {
        using var stream = new ArcBufferReadStream(content);
        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = KeyOf(name),
            InputStream = stream,
            AutoCloseStream = false,
            // The writer's CRC-32C, when it gave one, so the service checks the bytes the writer verified.
            ChecksumCRC32C = ObjectStoreChecksums.Crc32CBase64(ObjectStoreChecksums.Crc32CToSend(content, crc32c)),
            DisablePayloadSigning = _overTls,
        };
        if (expectedETag is null)
        {
            request.IfNoneMatch = "*";
        }
        else
        {
            request.IfMatch = expectedETag;
        }

        PutObjectResponse response;
        try
        {
            response = await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsChecksumRefusal(exception))
        {
            throw ObjectContentChangedException.ForServiceRefusal(name, exception, _overTls);
        }

        return string.IsNullOrWhiteSpace(response.ETag)
            ? throw new IOException("S3 answered an applied upload without the object's ETag, so the write's outcome is unknown. Re-read the object to establish the operation's goal.")
            : response.ETag;
    }

    private string KeyOf(ObjectName name) =>
        _directoryBucket ? S3DirectoryBucketLayout.Encode(_prefix, name) : _prefix + name.Value;

    private readonly record struct DirectoryEntry(string LogicalName, string? S3Prefix, long Size)
    {
        public bool IsDirectory => S3Prefix is not null;
    }

    private readonly record struct DirectoryCursor(List<DirectoryEntry> Entries, int Index);
}
