using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>Ordering applied by <see cref="FakeS3DirectoryBucketService"/> to each <c>ListObjectsV2</c> response.</summary>
public enum FakeS3ListingOrder
{
    /// <summary>Reverses every directory's entries before pagination.</summary>
    Reversed,

    /// <summary>Deterministically shuffles every directory's entries before pagination.</summary>
    Shuffled,
}

/// <summary>Parameters captured from a <c>ListObjectsV2</c> request sent to <see cref="FakeS3DirectoryBucketService"/>.</summary>
internal readonly record struct RecordedListRequest(string Prefix, string Delimiter, int? MaxKeys, bool HasPrefixParameter);

/// <summary>
/// A strict HTTP-layer fake of an Amazon S3 Express One Zone directory bucket (<c>--x-s3</c>) beneath the real
/// <see cref="Amazon.S3.AmazonS3Client"/>.
/// </summary>
/// <remarks>
/// <para>
/// Enforces S3 Express One Zone directory-bucket semantics:
/// <c>ListObjectsV2</c> rejects any presence of <c>start-after</c>, rejects any <c>delimiter</c> other than <c>/</c>,
/// returns <c>400 InvalidRequest</c> for any non-empty <c>prefix</c> that does not end in <c>/</c>, binds continuation
/// tokens to <c>(prefix, delimiter)</c>, forbids creating a key that collides with an existing directory (including
/// retained empty directories) or ancestor object, validates <c>x-amz-checksum-crc32c</c> supplied via request header
/// or <c>aws-chunked</c> trailer on <c>PutObject</c> and returns it on <c>HeadObject</c> only when stored, assigns
/// unique per-upload opaque ETags, honours <c>If-None-Match</c> and <c>If-Match</c> atomically, and returns
/// <c>Contents</c> and <c>CommonPrefixes</c> intentionally unordered (reversed or deterministically shuffled across
/// pages).
/// </para>
/// <para>
/// <c>retainEmptyDirectories</c> models S3 Express directories that remain visible in <c>CommonPrefixes</c> while
/// in-progress multipart uploads or concurrent operations reference them. Tests configure <c>ForcePathStyle = true</c>
/// and <c>DisableS3ExpressSessionAuth = true</c> only as fake-client seams to route requests into this handler;
/// production S3 Express clients use virtual-hosted endpoints and SigV4 Express session authentication.
/// </para>
/// </remarks>
internal sealed class FakeS3DirectoryBucketService : HttpMessageHandler
{
    internal const string DefaultBucketName = "ledgers--usw2-az1--x-s3";
    private const int MaxKeyBytes = 1_024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, StoredObject> _objects = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _emptyDirectories = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, PendingPage> _pages = [with(StringComparer.Ordinal)];
    private readonly List<RecordedListRequest> _listRequests = [];
    private readonly string _bucketName;
    private readonly FakeS3ListingOrder _listingOrder;
    private readonly int _maxPageSize;
    private readonly bool _retainEmptyDirectories;
    private readonly bool _duplicateAcrossPages;
    private readonly int _seed;
    private long _etagSequence;
    private int _listSequence;
    private int _tokenSequence;

    public FakeS3DirectoryBucketService(
        string bucketName = DefaultBucketName,
        FakeS3ListingOrder listingOrder = FakeS3ListingOrder.Shuffled,
        int seed = 1,
        int maxPageSize = 1_000,
        bool retainEmptyDirectories = false,
        bool duplicateAcrossPages = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPageSize, 1);

        _bucketName = bucketName;
        _listingOrder = listingOrder;
        _seed = seed;
        _maxPageSize = maxPageSize;
        _retainEmptyDirectories = retainEmptyDirectories;
        _duplicateAcrossPages = duplicateAcrossPages;
    }

    /// <summary>Gets every <c>ListObjectsV2</c> request served.</summary>
    public IReadOnlyList<RecordedListRequest> ListRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _listRequests];
            }
        }
    }

    /// <summary>Gets the S3 <c>Prefix</c> of every <c>ListObjectsV2</c> request served.</summary>
    public IReadOnlyList<string> ListedPrefixes
    {
        get
        {
            lock (_gate)
            {
                return [.. _listRequests.Select(static request => request.Prefix)];
            }
        }
    }

    /// <summary>Gets the stored keys currently in the bucket.</summary>
    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_gate)
            {
                return [.. _objects.Keys];
            }
        }
    }

    /// <summary>Clears the recorded <see cref="ListRequests"/> and <see cref="ListedPrefixes"/>.</summary>
    public void ResetListedPrefixes()
    {
        lock (_gate)
        {
            _listRequests.Clear();
        }
    }

    /// <summary>Adds a raw foreign object directly to the bucket, bypassing the store's key layout.</summary>
    public void AddForeignObject(string key, byte[] content)
    {
        lock (_gate)
        {
            _objects[key] = new StoredObject([.. content], NextETag(), Crc32CBase64: null);
        }
    }

    /// <summary>Adds an empty directory entry directly to the bucket.</summary>
    public void AddEmptyDirectory(string directoryPrefix)
    {
        if (string.IsNullOrEmpty(directoryPrefix) || directoryPrefix[^1] != '/')
        {
            throw new ArgumentException("A directory prefix is non-empty and ends in '/'.", nameof(directoryPrefix));
        }

        lock (_gate)
        {
            AddAncestorDirectories(directoryPrefix);
            _ = _emptyDirectories.Add(directoryPrefix);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is missing.");
        var query = ParseQuery(uri.Query);
        var (bucket, key) = ParseBucketAndKey(uri.AbsolutePath);

        if (!string.Equals(bucket, _bucketName, StringComparison.Ordinal))
        {
            return StubResponses.S3Error(HttpStatusCode.NotFound, "NoSuchBucket");
        }

        if (request.Method == HttpMethod.Get && query.TryGetValue("list-type", out var listType) && listType == "2")
        {
            return HandleListObjectsV2(query);
        }

        if (string.IsNullOrEmpty(key))
        {
            return StubResponses.S3Error(HttpStatusCode.BadRequest, "InvalidRequest");
        }

        if (request.Method == HttpMethod.Put)
        {
            return HandlePutObject(request, key, body);
        }

        if (request.Method == HttpMethod.Get)
        {
            return HandleGetObject(request, key);
        }

        if (request.Method == HttpMethod.Head)
        {
            return HandleHeadObject(request, key);
        }

        if (request.Method == HttpMethod.Delete)
        {
            return HandleDeleteObject(key);
        }

        return StubResponses.S3Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed");
    }

    private HttpResponseMessage HandlePutObject(HttpRequestMessage request, string key, byte[] body)
    {
        if (Encoding.UTF8.GetByteCount(key) > MaxKeyBytes)
        {
            return StubResponses.S3Error(HttpStatusCode.BadRequest, "KeyTooLongError");
        }

        Dictionary<string, string>? trailers = null;
        if (IsAwsChunked(request))
        {
            (body, trailers) = DecodeAwsChunked(body);
        }

        var expectedCrc = Header(request, "x-amz-checksum-crc32c");
        if (expectedCrc is null && trailers is not null && trailers.TryGetValue("x-amz-checksum-crc32c", out var trailerCrc))
        {
            expectedCrc = trailerCrc;
        }

        if (expectedCrc is not null)
        {
            var actualCrc = Crc32CBase64(body);
            if (!string.Equals(expectedCrc, actualCrc, StringComparison.Ordinal))
            {
                return StubResponses.S3Error(HttpStatusCode.BadRequest, "BadDigest");
            }
        }

        var ifNoneMatch = Header(request, "If-None-Match");
        var ifMatch = Header(request, "If-Match");

        lock (_gate)
        {
            if (HasPathCollision(key))
            {
                return StubResponses.S3Error(HttpStatusCode.BadRequest, "InvalidRequest");
            }

            var exists = _objects.TryGetValue(key, out var existing);
            if (string.Equals(ifNoneMatch, "*", StringComparison.Ordinal) && exists)
            {
                return StubResponses.S3Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
            }

            if (ifMatch is not null)
            {
                if (!exists)
                {
                    return StubResponses.S3Error(HttpStatusCode.NotFound, "NoSuchKey");
                }

                if (!string.Equals(existing!.ETag, ifMatch, StringComparison.Ordinal))
                {
                    return StubResponses.S3Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
                }
            }

            var etag = NextETag();
            _objects[key] = new StoredObject(body, etag, expectedCrc);
            return StubResponses.S3Written(etag);
        }
    }

    private HttpResponseMessage HandleGetObject(HttpRequestMessage request, string key)
    {
        StoredObject? found;
        lock (_gate)
        {
            _ = _objects.TryGetValue(key, out found);
        }

        if (found is null)
        {
            return StubResponses.S3Error(HttpStatusCode.NotFound, "NoSuchKey");
        }

        if (Header(request, "Range") is { } rangeHeader)
        {
            if (!RangeHeaderValue.TryParse(rangeHeader, out var parsed) || parsed.Ranges.Count != 1)
            {
                return StubResponses.S3Error(HttpStatusCode.BadRequest, "InvalidArgument");
            }

            var range = parsed.Ranges.First();
            var from = range.From ?? 0;
            var to = range.To ?? (found.Content.Length - 1);
            if (from < 0 || from >= found.Content.Length || to < from)
            {
                return StubResponses.S3Error(HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange");
            }

            var clampedTo = Math.Min(to, found.Content.Length - 1);
            var slice = found.Content.AsSpan((int)from, (int)(clampedTo - from + 1)).ToArray();
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice),
            };
            partial.Content.Headers.ContentLength = slice.Length;
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, clampedTo, found.Content.Length);
            partial.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            _ = partial.Headers.TryAddWithoutValidation("ETag", found.ETag);
            return partial;
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([.. found.Content]),
        };
        response.Content.Headers.ContentLength = found.Content.Length;
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        _ = response.Headers.TryAddWithoutValidation("ETag", found.ETag);
        return response;
    }

    private HttpResponseMessage HandleHeadObject(HttpRequestMessage request, string key)
    {
        StoredObject? found;
        lock (_gate)
        {
            _ = _objects.TryGetValue(key, out found);
        }

        if (found is null)
        {
            return StubResponses.Status(HttpStatusCode.NotFound);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]),
        };
        response.Content.Headers.ContentLength = found.Content.Length;
        _ = response.Headers.TryAddWithoutValidation("ETag", found.ETag);
        if (string.Equals(Header(request, "x-amz-checksum-mode"), "ENABLED", StringComparison.OrdinalIgnoreCase)
            && found.Crc32CBase64 is not null)
        {
            response.Headers.Add("x-amz-checksum-crc32c", found.Crc32CBase64);
        }

        return response;
    }

    private HttpResponseMessage HandleDeleteObject(string key)
    {
        lock (_gate)
        {
            if (_objects.Remove(key) && _retainEmptyDirectories)
            {
                AddAncestorDirectories(key);
            }
        }

        return StubResponses.Status(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage HandleListObjectsV2(Dictionary<string, string> query)
    {
        // S3 Express One Zone directory buckets reject any presence of StartAfter (including empty).
        if (query.ContainsKey("start-after"))
        {
            return StubResponses.S3Error(HttpStatusCode.BadRequest, "InvalidArgument");
        }

        var delimiter = query.TryGetValue("delimiter", out var rawDelimiter) ? rawDelimiter : string.Empty;
        if (delimiter.Length > 0 && !string.Equals(delimiter, "/", StringComparison.Ordinal))
        {
            return StubResponses.S3Error(HttpStatusCode.BadRequest, "InvalidArgument");
        }

        var hasPrefixParameter = query.TryGetValue("prefix", out var rawPrefix);
        var prefix = hasPrefixParameter ? rawPrefix! : string.Empty;
        // S3 Express One Zone requires a non-empty Prefix to end in '/' (whether or not Delimiter is set).
        if (prefix.Length > 0 && prefix[^1] != '/')
        {
            return StubResponses.S3Error(HttpStatusCode.BadRequest, "InvalidRequest");
        }

        int? explicitMaxKeys = null;
        if (query.TryGetValue("max-keys", out var rawMaxKeys))
        {
            if (!int.TryParse(rawMaxKeys, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMax) || parsedMax < 0)
            {
                return StubResponses.S3Error(HttpStatusCode.BadRequest, "InvalidArgument");
            }

            explicitMaxKeys = parsedMax;
        }

        var effectiveMaxKeys = explicitMaxKeys is { } requested
            ? Math.Min(requested, _maxPageSize)
            : _maxPageSize;

        List<ListEntry> pageEntries;
        string? nextToken = null;
        lock (_gate)
        {
            _listRequests.Add(new RecordedListRequest(prefix, delimiter, explicitMaxKeys, hasPrefixParameter));

            if (explicitMaxKeys == 0)
            {
                return BuildListXml(prefix, delimiter, maxKeys: 0, nextToken: null, entries: []);
            }

            List<ListEntry> remaining;
            var duplicatedPrevious = false;
            if (query.TryGetValue("continuation-token", out var token) && token.Length > 0)
            {
                // Continuation tokens are idempotent cursors in S3 bound to their (prefix, delimiter).
                if (!_pages.TryGetValue(token, out var pending)
                    || !string.Equals(pending.Prefix, prefix, StringComparison.Ordinal)
                    || !string.Equals(pending.Delimiter, delimiter, StringComparison.Ordinal))
                {
                    return StubResponses.S3Error(HttpStatusCode.BadRequest, "InvalidArgument");
                }

                remaining = pending.Remaining;
                duplicatedPrevious = pending.DuplicatedPrevious;
            }
            else
            {
                remaining = CollectDirectEntries(prefix, delimiter);
                ApplyUnorderedPermutation(remaining);
            }

            if (remaining.Count <= effectiveMaxKeys)
            {
                pageEntries = remaining;
            }
            else
            {
                pageEntries = remaining.GetRange(0, effectiveMaxKeys);
                var tail = remaining.GetRange(effectiveMaxKeys, remaining.Count - effectiveMaxKeys);
                var duplicatedNow = false;
                if (_duplicateAcrossPages && !duplicatedPrevious && pageEntries.Count > 0)
                {
                    // Repeat the last entry of this page on the next page to exercise cross-page deduplication
                    // without stalling progress when effectiveMaxKeys == 1.
                    tail.Insert(0, pageEntries[^1]);
                    duplicatedNow = true;
                }

                nextToken = string.Create(CultureInfo.InvariantCulture, $"tok-{++_tokenSequence}");
                _pages[nextToken] = new PendingPage(prefix, delimiter, tail, duplicatedNow);
            }
        }

        return BuildListXml(prefix, delimiter, effectiveMaxKeys, nextToken, pageEntries);
    }

    private List<ListEntry> CollectDirectEntries(string prefix, string delimiter)
    {
        var entries = new List<ListEntry>();
        var directories = new HashSet<string>(StringComparer.Ordinal);

        if (delimiter.Length == 0)
        {
            foreach (var (key, stored) in _objects)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    entries.Add(new ListEntry(key, IsDirectory: false, stored.Content.Length));
                }
            }

            return entries;
        }

        foreach (var (key, stored) in _objects)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var slash = key.IndexOf('/', prefix.Length);
            if (slash < 0)
            {
                entries.Add(new ListEntry(key, IsDirectory: false, stored.Content.Length));
            }
            else
            {
                var commonPrefix = key[..(slash + 1)];
                if (directories.Add(commonPrefix))
                {
                    entries.Add(new ListEntry(commonPrefix, IsDirectory: true, 0));
                }
            }
        }

        foreach (var dir in _emptyDirectories)
        {
            if (!dir.StartsWith(prefix, StringComparison.Ordinal) || dir.Length <= prefix.Length)
            {
                continue;
            }

            var slash = dir.IndexOf('/', prefix.Length);
            if (slash >= 0)
            {
                var commonPrefix = dir[..(slash + 1)];
                if (directories.Add(commonPrefix))
                {
                    entries.Add(new ListEntry(commonPrefix, IsDirectory: true, 0));
                }
            }
        }

        return entries;
    }

    private void ApplyUnorderedPermutation(List<ListEntry> entries)
    {
        // Sort deterministically first so the permutation is independent of Dictionary iteration order.
        entries.Sort(static (left, right) =>
        {
            var cmp = string.CompareOrdinal(left.Name, right.Name);
            return cmp != 0 ? cmp : left.IsDirectory.CompareTo(right.IsDirectory);
        });

        if (_listingOrder == FakeS3ListingOrder.Reversed)
        {
            entries.Reverse();
            return;
        }

        var random = new Random(HashCode.Combine(_seed, ++_listSequence));
        for (var index = entries.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (entries[index], entries[swap]) = (entries[swap], entries[index]);
        }
    }

    private bool HasPathCollision(string key)
    {
        // A key cannot equal an existing directory prefix (including retained empty directories) nor sit beneath an existing object key.
        var asDirectory = key + "/";
        if (_emptyDirectories.Contains(asDirectory))
        {
            return true;
        }

        foreach (var dir in _emptyDirectories)
        {
            if (dir.StartsWith(asDirectory, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var existing in _objects.Keys)
        {
            if (existing.StartsWith(asDirectory, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var slash = key.IndexOf('/');
        while (slash >= 0)
        {
            if (_objects.ContainsKey(key[..slash]))
            {
                return true;
            }

            slash = key.IndexOf('/', slash + 1);
        }

        return false;
    }

    private void AddAncestorDirectories(string path)
    {
        var slash = path.IndexOf('/');
        while (slash >= 0)
        {
            _ = _emptyDirectories.Add(path[..(slash + 1)]);
            slash = path.IndexOf('/', slash + 1);
        }
    }

    private HttpResponseMessage BuildListXml(string prefix, string delimiter, int maxKeys, string? nextToken, List<ListEntry> entries)
    {
        var xml = new StringBuilder(512);
        _ = xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>")
            .Append("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">")
            .Append("<Name>").Append(SecurityElement.Escape(_bucketName)).Append("</Name>")
            .Append("<Prefix>").Append(SecurityElement.Escape(prefix)).Append("</Prefix>");
        if (delimiter.Length > 0)
        {
            _ = xml.Append("<Delimiter>").Append(SecurityElement.Escape(delimiter)).Append("</Delimiter>");
        }

        _ = xml.Append(CultureInfo.InvariantCulture, $"<KeyCount>{entries.Count}</KeyCount>")
            .Append(CultureInfo.InvariantCulture, $"<MaxKeys>{maxKeys}</MaxKeys>")
            .Append("<IsTruncated>").Append(nextToken is null ? "false" : "true").Append("</IsTruncated>");
        if (nextToken is not null)
        {
            _ = xml.Append("<NextContinuationToken>").Append(SecurityElement.Escape(nextToken)).Append("</NextContinuationToken>");
        }

        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                _ = xml.Append("<CommonPrefixes><Prefix>")
                    .Append(SecurityElement.Escape(entry.Name))
                    .Append("</Prefix></CommonPrefixes>");
            }
            else
            {
                _ = xml.Append("<Contents><Key>")
                    .Append(SecurityElement.Escape(entry.Name))
                    .Append(CultureInfo.InvariantCulture, $"</Key><Size>{entry.Size}</Size></Contents>");
            }
        }

        _ = xml.Append("</ListBucketResult>");
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(xml.ToString()));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static (string Bucket, string Key) ParseBucketAndKey(string absolutePath)
    {
        var trimmed = absolutePath.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        if (slash < 0)
        {
            return (Uri.UnescapeDataString(trimmed), string.Empty);
        }

        return (Uri.UnescapeDataString(trimmed[..slash]), Uri.UnescapeDataString(trimmed[(slash + 1)..]));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
            }
            else
            {
                result[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return result;
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values)
            || request.Content is not null && request.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : null;

    private static bool IsAwsChunked(HttpRequestMessage request)
    {
        var encoding = Header(request, "Content-Encoding");
        if (encoding is not null && encoding.Contains("aws-chunked", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var sha = Header(request, "x-amz-content-sha256");
        return sha is not null && sha.StartsWith("STREAMING-", StringComparison.OrdinalIgnoreCase);
    }

    private static (byte[] Body, Dictionary<string, string> Trailers) DecodeAwsChunked(byte[] encoded)
    {
        var trailers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (encoded.Length == 0)
        {
            return (encoded, trailers);
        }

        using var output = new MemoryStream(encoded.Length);
        var offset = 0;
        while (offset < encoded.Length)
        {
            var lineEnd = encoded.AsSpan(offset).IndexOf("\r\n"u8);
            if (lineEnd < 0)
            {
                throw new InvalidDataException("Malformed aws-chunked body: missing CRLF after chunk header.");
            }

            var header = Encoding.ASCII.GetString(encoded, offset, lineEnd);
            var semicolon = header.IndexOf(';');
            var sizeHex = semicolon >= 0 ? header[..semicolon] : header;
            var chunkLength = int.Parse(sizeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            offset += lineEnd + 2;
            if (chunkLength == 0)
            {
                break;
            }

            output.Write(encoded, offset, chunkLength);
            offset += chunkLength + 2;
        }

        // Parse trailing headers (such as x-amz-checksum-crc32c) that follow the terminating 0-length chunk.
        while (offset < encoded.Length)
        {
            var lineEnd = encoded.AsSpan(offset).IndexOf("\r\n"u8);
            var length = lineEnd < 0 ? encoded.Length - offset : lineEnd;
            if (length == 0)
            {
                break;
            }

            var line = Encoding.ASCII.GetString(encoded, offset, length);
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                trailers[name] = value;
            }

            offset += lineEnd < 0 ? length : lineEnd + 2;
        }

        return (output.ToArray(), trailers);
    }

    private string NextETag() =>
        string.Create(CultureInfo.InvariantCulture, $"\"{++_etagSequence:x16}-{_seed:x8}\"");

    private static string Crc32CBase64(byte[] content)
    {
        var crc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, content);
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, crc);
        return Convert.ToBase64String(bytes);
    }

    private sealed record StoredObject(byte[] Content, string ETag, string? Crc32CBase64);

    private readonly record struct ListEntry(string Name, bool IsDirectory, long Size);

    private readonly record struct PendingPage(string Prefix, string Delimiter, List<ListEntry> Remaining, bool DuplicatedPrevious);
}
