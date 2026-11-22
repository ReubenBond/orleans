using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.AzureStorage.Archive;

/// <summary>
/// Keeps objects as block blobs, and appendable objects as append blobs, of one Azure Blob Storage container (archive LSM
/// §9, object-store.md §9.1).
/// </summary>
/// <remarks>
/// <para>
/// The object <c>name</c> is the blob <c>{prefix}{name}</c>, and its ETag is the object's version. A create or replace
/// is one <c>Put Blob</c>, whatever the content's length, with <c>If-None-Match: *</c> or <c>If-Match</c> and the
/// <c>Content-MD5</c> the SDK computes before the request leaves. <c>Put Blob</c> takes no CRC-32C, so the store checks
/// its writer's over the bytes the SDK reads for that MD5 (<see cref="Crc32CCheckingStream"/>), and a mismatch throws
/// <see cref="ObjectContentChangedException"/> before any request; the service's refusal of the MD5 is raised by
/// <see cref="ObjectContentChangedException.ForServiceRefusal"/>, by the transport. One request takes up to 5,000 MiB,
/// more than any <see cref="ArcBuffer"/> holds, so the client's per-try network timeout
/// (<c>RetryOptions.NetworkTimeout</c>) covers an upload of the largest object. Durability, retries, and idempotency are as archive LSM §9 states for every cloud
/// store.
/// </para>
/// <para>
/// An appendable object is an append blob created with <c>If-None-Match: *</c> and the index tag
/// <c>durable-open = 1</c>. An append is one <c>Append Block</c> conditioned on <c>appendpos</c> at its expected length
/// and on <c>x-ms-if-tags: "durable-open" = '1'</c>, which the service evaluates atomically with the append, so a seal,
/// <c>Set Blob Tags</c> with <c>durable-open = 0</c> under the same tag condition, stops every append which lands after
/// it. A refused append is classified by <c>Get Blob Properties</c>, whose blob type tells an append blob, and
/// <c>Get Blob Tags</c>: absent; not appendable, so sealed; sealed, unless the blob holds the append's bytes at its
/// expected length, which an earlier attempt whose answer the client lost wrote before the seal; open at another
/// length; or open at the expected length, which other appends reached since, so the append is tried again, at most
/// three times. A tombstone is an unconditional <c>Put Blob</c> of a block blob tagged <c>durable-open = 0</c>, which
/// replaces the append blob whole, so an append after it meets a block blob and answers sealed. The container's client
/// uses service version 2022-11-02 or later, which <see cref="AzureArchiveLogStore.ConfigureClientOptions"/> requires
/// and the composition pins above: its tag conditions and 100 MiB blocks these rely on.
/// </para>
/// <para>
/// An upload reads its content through an <see cref="ArcBufferReadStream"/>, disposed when the call returns, so a read
/// the SDK makes after that fails rather than reading a released page. A read fills pooled pages straight from the
/// response.
/// </para>
/// </remarks>
public sealed class AzureBlobObjectStore : IObjectStore, IAppendableObjectStore
{
    /// <summary>
    /// The largest content one <c>Put Blob</c> takes: 5,000 MiB, more than any <see cref="ArcBuffer"/> holds.
    /// </summary>
    private const long MaxPutBlobBytes = 5_000L * 1024 * 1024;

    /// <summary>
    /// The metadata which records a non-empty object's CRC-32C, 8 lowercase hexadecimal digits, since a blob keeps only
    /// its MD5 (object-store.md §7).
    /// </summary>
    internal const string Crc32CMetadata = "durablecrc32c";

    /// <summary>
    /// The index tag of an appendable object, <c>1</c> while it is open and <c>0</c> once sealed.
    /// </summary>
    internal const string OpenTag = "durable-open";

    /// <summary>
    /// The tag condition every append carries: the object is open.
    /// </summary>
    private const string OpenCondition = "\"" + OpenTag + "\" = '1'";

    /// <summary>
    /// The most <c>Append Block</c> requests one append sends while the blob stays open at its expected length, after
    /// which its outcome is unknown.
    /// </summary>
    private const int MaxAppendAttempts = 3;

    private readonly BlobContainerClient _container;

    /// <summary>
    /// Gets a value indicating whether a refused append to a sealed blob answers sealed without reading whether an earlier
    /// attempt landed before the seal: a mutant, for tests.
    /// </summary>
    internal bool AnswersSealedForALandedAppendMutant { get; init; }
    private readonly string _prefix;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBlobObjectStore"/> class.
    /// </summary>
    /// <param name="container">The container which holds the objects, which exists.</param>
    /// <param name="prefix">The blob-name prefix of every object, empty or ending in <c>/</c>.</param>
    public AzureBlobObjectStore(BlobContainerClient container, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        prefix ??= string.Empty;
        if (prefix.Length > 0 && prefix[^1] != '/')
        {
            throw new ArgumentException("A non-empty Azure Blob object store prefix ends in '/'.", nameof(prefix));
        }

        _container = container;
        _prefix = prefix;
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            _ = await UploadAsync(name, AzureBlobRequests.ConditionsFor(null), content, crc32c, cancellationToken).ConfigureAwait(false);
            return ObjectCreateOutcome.Created.Instance;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsConditionFailure(exception, expectedAbsent: true))
        {
            return ObjectCreateOutcome.Exists.Instance;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsDefiniteRefusal(exception))
        {
            throw Refused("a create", name, exception);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfNotRange(offset, length);
        BlobDownloadStreamingResult download;
        try
        {
            download = (await BlobOf(name)
                .DownloadStreamingAsync(new BlobDownloadOptions { Range = new HttpRange(offset, length) }, cancellationToken)
                .ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException exception) when (IsAbsent(exception))
        {
            return null;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 416, BlobErrorCode.InvalidRange))
        {
            // The range starts at or past the object's end, as any range of an empty object does.
            throw new EndOfStreamException(string.Create(CultureInfo.InvariantCulture, $"The object '{name}' ends at or before {offset}."), exception);
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsDefiniteRefusal(exception))
        {
            throw Refused("a read", name, exception);
        }

        using (download)
        {
            using var writer = new ArcBufferWriter(length);
            await ArcBufferWriteStream.CopyFromAsync(download.Content, writer, cancellationToken).ConfigureAwait(false);
            var (from, total) = ServedRangeOf(name, offset, length, writer.Length, download.Details.ContentRange);
            ObjectStoreResponses.ThrowIfNotServed(name, offset, length, from, writer.Length, total);
            return writer.ConsumeSlice(length);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One <c>Get Blob Properties</c>: the blob's length and the CRC-32C its upload recorded as metadata; an append blob,
    /// whose length grows, records none.
    /// </remarks>
    public async ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (await BlobPropertiesAsync(name, cancellationToken).ConfigureAwait(false) is not { } properties)
        {
            return null;
        }

        return properties.ContentLength == 0 || properties.BlobType == BlobType.Append
            ? new ObjectProperties(properties.ContentLength, null)
            : new ObjectProperties(properties.ContentLength, Crc32COf(name, properties.Metadata));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
    {
        ObjectStoreArguments.ThrowIfNotPrefix(prefix);
        ObjectStoreArguments.ThrowIfNotStart(startAfter);
        return ListBlobsAsync(prefix, startAfter, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            _ = await BlobOf(name).DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.BlobNotFound))
        {
            // The blob is absent, as the delete leaves it; a missing container is a 404 of another code, which throws.
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsDefiniteRefusal(exception))
        {
            throw Refused("a delete", name, exception);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        BlobDownloadStreamingResult download;
        try
        {
            download = (await BlobOf(name).DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException exception) when (IsAbsent(exception))
        {
            return null;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsDefiniteRefusal(exception))
        {
            throw Refused("a read", name, exception);
        }

        using (download)
        {
            // One download serves the content and its ETag; the SDK resumes a broken download only at that ETag.
            var length = download.Details.ContentLength;
            if (length is < 0 or > int.MaxValue)
            {
                throw new IOException(string.Create(CultureInfo.InvariantCulture, $"Azure Blob answered a read of '{name}' with a length of {length}, which no object has."));
            }

            using var writer = new ArcBufferWriter((int)length);
            await ArcBufferWriteStream.CopyFromAsync(download.Content, writer, cancellationToken).ConfigureAwait(false);
            if (writer.Length != length)
            {
                throw new IOException(string.Create(CultureInfo.InvariantCulture, $"Azure Blob answered {writer.Length} bytes of '{name}' where its response promised {length}."));
            }

            return new VersionedObject(writer.ConsumeSlice(writer.Length), new ObjectVersion(download.Details.ETag.ToString()));
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
            var etag = await UploadAsync(name, AzureBlobRequests.ConditionsFor(expectedVersion?.Value), content, crc32c, cancellationToken).ConfigureAwait(false);
            return new ObjectReplaceOutcome.Applied(new ObjectVersion(etag));
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsConditionFailure(exception, expectedAbsent: expectedVersion is null))
        {
            return ObjectReplaceOutcome.Mismatch.Instance;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsDefiniteRefusal(exception))
        {
            throw Refused("a replace", name, exception);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            _ = await AppendBlobOf(name)
                .CreateAsync(
                    new AppendBlobCreateOptions
                    {
                        Tags = Tagged(open: true),
                        Conditions = new AppendBlobRequestConditions { IfNoneMatch = ETag.All },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return ObjectCreateOutcome.Created.Instance;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsConditionFailure(exception, expectedAbsent: true))
        {
            return ObjectCreateOutcome.Exists.Instance;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsDefiniteRefusal(exception))
        {
            throw Refused("an appendable create", name, exception);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfNotAppend(expectedLength, content);
        for (var attempt = 0; attempt < MaxAppendAttempts; attempt++)
        {
            try
            {
                return await AppendBlockAsync(name, expectedLength, content, crc32c, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (IsAppendRefusal(exception))
            {
                // A failed condition, a missing blob, or a block blob: this attempt did not land, and the blob's state
                // answers, an earlier attempt's landing among it.
            }
            catch (RequestFailedException exception) when (AzureBlobRequests.IsDefiniteRefusal(exception))
            {
                throw Refused("an append", name, exception);
            }

            var (decided, outcome) = await StateAfterRefusalAsync(name, expectedLength, content, cancellationToken).ConfigureAwait(false);
            if (decided)
            {
                return outcome;
            }
        }

        throw new IOException(string.Create(
            CultureInfo.InvariantCulture,
            $"Azure Blob refused {MaxAppendAttempts} appends to '{name}' at {expectedLength} while the blob stayed open at that length, so the append's outcome is unknown."));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>Get Blob Properties</c>, whose blob type tells an append blob, then <c>Set Blob Tags</c> with
    /// <c>durable-open = 0</c>, conditioned on <c>"durable-open" = '1'</c>, so it changes no sealed or non-appendable blob;
    /// a failed condition reads the blob's type again, since a tombstone may have replaced it.
    /// </remarks>
    public async ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (await AppendabilityAsync(name, cancellationToken).ConfigureAwait(false) is { Value: not ObjectSealOutcome.Sealed } decided)
        {
            return decided;
        }

        try
        {
            _ = await BlobOf(name)
                .SetTagsAsync(Tagged(open: false), new BlobRequestConditions { TagConditions = OpenCondition }, cancellationToken)
                .ConfigureAwait(false);
            return ObjectSealOutcome.Sealed.Instance;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.BlobNotFound))
        {
            return ObjectSealOutcome.Absent.Instance;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 412, BlobErrorCode.ConditionNotMet))
        {
            // Not open: sealed already, or replaced by a tombstone since the first read.
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsDefiniteRefusal(exception))
        {
            throw Refused("a seal", name, exception);
        }

        return await AppendabilityAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns what a seal of the blob as it stands answers: absent, not appendable, or, for an append blob, sealed.
    /// </summary>
    private async ValueTask<ObjectSealOutcome> AppendabilityAsync(ObjectName name, CancellationToken cancellationToken) =>
        await BlobPropertiesAsync(name, cancellationToken).ConfigureAwait(false) switch
        {
            null => ObjectSealOutcome.Absent.Instance,
            { BlobType: BlobType.Append } => ObjectSealOutcome.Sealed.Instance,
            _ => ObjectSealOutcome.NotAppendable.Instance,
        };

    /// <inheritdoc/>
    /// <remarks>
    /// One unconditional <c>Put Blob</c> of a block blob tagged <c>durable-open = 0</c>, which replaces an append blob
    /// whole.
    /// </remarks>
    public async ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            _ = await UploadAsync(name, conditions: null, content, crc32c, cancellationToken, Tagged(open: false)).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsDefiniteRefusal(exception))
        {
            throw Refused("a tombstone", name, exception);
        }
    }

    /// <summary>
    /// Classifies a refused append by the blob's state: absent; not an append blob, so sealed; an append blob which is not
    /// open, sealed unless it holds the append's bytes at its expected length, as when an earlier attempt landed before the
    /// seal; open at another length; or, open at the expected length, which other appends reached since, undecided, so the
    /// append is tried again. A blob deleted between the reads is absent.
    /// </summary>
    private async ValueTask<(bool Decided, ObjectAppendOutcome Outcome)> StateAfterRefusalAsync(ObjectName name, long expectedLength, ArcBuffer content, CancellationToken cancellationToken)
    {
        if (await BlobPropertiesAsync(name, cancellationToken).ConfigureAwait(false) is not { } properties)
        {
            return (true, ObjectAppendOutcome.Absent.Instance);
        }

        if (properties.BlobType != BlobType.Append)
        {
            return (true, ObjectAppendOutcome.Sealed.Instance);
        }

        if (await IsOpenAsync(name, cancellationToken).ConfigureAwait(false) is not { } open)
        {
            return (true, ObjectAppendOutcome.Absent.Instance);
        }

        if (!open)
        {
            return (true, await HoldsAsync(name, expectedLength, content, properties.ContentLength, cancellationToken).ConfigureAwait(false) switch
            {
                true => new ObjectAppendOutcome.Appended(expectedLength + content.Length),
                false => ObjectAppendOutcome.Sealed.Instance,
                null => ObjectAppendOutcome.Absent.Instance,
            });
        }

        return properties.ContentLength == expectedLength
            ? (false, ObjectAppendOutcome.Absent.Instance)
            : (true, new ObjectAppendOutcome.LengthMismatch(properties.ContentLength));
    }

    /// <summary>
    /// Returns whether a sealed blob of <paramref name="length"/> bytes holds <paramref name="content"/> at
    /// <paramref name="expectedLength"/>, or <see langword="null"/> when it is gone.
    /// </summary>
    private async ValueTask<bool?> HoldsAsync(ObjectName name, long expectedLength, ArcBuffer content, long length, CancellationToken cancellationToken)
    {
        if (AnswersSealedForALandedAppendMutant || length - content.Length < expectedLength)
        {
            return false;
        }

        if (await ReadAsync(name, expectedLength, content.Length, cancellationToken).ConfigureAwait(false) is not { } stored)
        {
            return null;
        }

        using (stored)
        {
            return stored.ToArray().AsSpan().SequenceEqual(content.ToArray());
        }
    }

    /// <summary>
    /// Appends one block at <paramref name="expectedLength"/> while the blob is open, its content checked against its
    /// writer's CRC-32C over the bytes the SDK reads for the block's <c>Content-MD5</c>.
    /// </summary>
    private async ValueTask<ObjectAppendOutcome> AppendBlockAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken)
    {
        using var read = new ArcBufferReadStream(content);
        using var checking = crc32c is { } expected ? new Crc32CCheckingStream(read, name, expected) : null;
        Response<BlobAppendInfo> appended;
        try
        {
            appended = await AppendBlobOf(name)
                .AppendBlockAsync(
                    checking ?? (Stream)read,
                    new AppendBlobAppendBlockOptions
                    {
                        Conditions = new AppendBlobRequestConditions { IfAppendPositionEqual = expectedLength, TagConditions = OpenCondition },
                        TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = StorageChecksumAlgorithm.MD5 },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 400, BlobErrorCode.Md5Mismatch))
        {
            throw ObjectContentChangedException.ForServiceRefusal(name, exception, _container.Uri.Scheme == Uri.UriSchemeHttps);
        }

        if (!long.TryParse(appended.Value.BlobAppendOffset, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset != expectedLength)
        {
            throw new IOException(string.Create(
                CultureInfo.InvariantCulture,
                $"Azure Blob answered an append to '{name}' at {expectedLength} with the offset '{appended.Value.BlobAppendOffset}', so its outcome is unknown."));
        }

        return new ObjectAppendOutcome.Appended(expectedLength + content.Length);
    }

    /// <summary>
    /// Returns whether an append's refusal leaves its answer to the blob's state: a failed position or tag condition, a
    /// missing blob, or a blob which is not an append blob, as a tombstone is.
    /// </summary>
    private static bool IsAppendRefusal(RequestFailedException exception) =>
        AzureBlobRequests.IsConditionFailure(exception, expectedAbsent: false)
        || AzureBlobRequests.HasErrorCode(exception, 409, BlobErrorCode.InvalidBlobType);

    /// <summary>
    /// Returns whether an append blob is open, tagged <c>durable-open = 1</c>, or <see langword="null"/> when it is gone.
    /// </summary>
    private async ValueTask<bool?> IsOpenAsync(ObjectName name, CancellationToken cancellationToken)
    {
        try
        {
            var tags = (await BlobOf(name).GetTagsAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value.Tags;
            return tags.TryGetValue(OpenTag, out var open) && open == "1";
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.BlobNotFound))
        {
            return null;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsDefiniteRefusal(exception))
        {
            throw Refused("a tags read", name, exception);
        }
    }

    /// <summary>
    /// Returns a blob's properties from one <c>Get Blob Properties</c>, or <see langword="null"/> when it is absent.
    /// </summary>
    private async ValueTask<BlobProperties?> BlobPropertiesAsync(ObjectName name, CancellationToken cancellationToken)
    {
        try
        {
            return (await BlobOf(name).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException exception) when (IsAbsent(exception))
        {
            return null;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsDefiniteRefusal(exception))
        {
            throw Refused("a properties read", name, exception);
        }
    }

    private static Dictionary<string, string> Tagged(bool open) => new(StringComparer.Ordinal) { [OpenTag] = open ? "1" : "0" };

    /// <summary>
    /// Returns whether a read's 404 shows its object absent: <c>BlobNotFound</c>, or <c>ContainerNotFound</c>, since no
    /// object lies in a container which does not exist; a write to a missing container is refused.
    /// </summary>
    private static bool IsAbsent(RequestFailedException exception) =>
        AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.BlobNotFound)
        || AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.ContainerNotFound);

    private ObjectStoreRefusedException Refused(string request, ObjectName name, RequestFailedException exception) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Azure refused {request} of '{name}' in the container '{_container.Uri}' with {exception.Status} {exception.ErrorCode}, which no retry changes."), exception, isAuthorization: exception.Status is 401 or 403);

    /// <summary>
    /// Returns where a ranged read's answer starts and the object's length, which its <c>Content-Range</c> gives.
    /// </summary>
    /// <remarks>
    /// Azurite answers a range which starts at the object's end with no bytes and the empty range
    /// <c>bytes {length}-{length - 1}/{length}</c>, where the service answers 416; that answer, too, shows the object
    /// ends before the range.
    /// </remarks>
    private static (long From, long Total) ServedRangeOf(ObjectName name, long offset, int length, long served, string? contentRange)
    {
        if (ContentRangeHeaderValue.TryParse(contentRange, out var range) && range is { From: { } from, Length: { } total })
        {
            return (from, total);
        }

        var slash = contentRange?.LastIndexOf('/') ?? -1;
        if (served == 0 && slash >= 0 && long.TryParse(contentRange.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var objectLength))
        {
            ObjectStoreArguments.ThrowIfPastEnd(name, offset, length, objectLength);
        }

        throw new IOException(string.Create(CultureInfo.InvariantCulture, $"Azure Blob answered a ranged read of '{name}' with the Content-Range '{contentRange}'."));
    }

    /// <summary>
    /// Returns the CRC-32C a non-empty block blob's upload recorded as metadata.
    /// </summary>
    /// <exception cref="IOException">The blob records none, as no upload of this store leaves it.</exception>
    private static uint Crc32COf(ObjectName name, IDictionary<string, string> metadata) =>
        metadata.TryGetValue(Crc32CMetadata, out var text)
            && text.Length == 8
            && uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var crc32c)
            ? crc32c
            : throw new IOException($"Azure Blob answered the properties of '{name}' without the {Crc32CMetadata} metadata its upload records.");

    /// <summary>
    /// Lists the blobs under the prefix from the first name at or after the start, which <c>startFrom</c> includes, so the
    /// start's own name is skipped; a service which ignores <c>startFrom</c> lists from the prefix's start, which the same
    /// check filters.
    /// </summary>
    private async IAsyncEnumerable<ListedObject> ListBlobsAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = new GetBlobsOptions { Prefix = _prefix + prefix, StartFrom = startAfter is null ? null : _prefix + startAfter };
        await foreach (var blob in _container.GetBlobsAsync(options, cancellationToken).ConfigureAwait(false))
        {
            // A blob whose name maps to no object name is not the store's, so the listing skips it.
            var name = blob.Name[_prefix.Length..];
            if (ObjectName.IsValid(name) && ObjectStoreArguments.IsAfter(name, startAfter) && blob.Properties.ContentLength is { } length)
            {
                yield return new ListedObject(new ObjectName(name), length);
            }
        }
    }

    private async ValueTask<string> UploadAsync(
        ObjectName name,
        BlobRequestConditions? conditions,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken,
        Dictionary<string, string>? tags = null)
    {
        // Put Blob takes no CRC-32C, so the store checks its writer's over the bytes the SDK reads for its Content-MD5.
        using var read = new ArcBufferReadStream(content);
        using var checking = crc32c is { } expected ? new Crc32CCheckingStream(read, name, expected) : null;
        var stream = checking ?? (Stream)read;
        Response<BlobContentInfo> uploaded;
        try
        {
            uploaded = await BlobOf(name)
                .UploadAsync(
                    stream,
                    new BlobUploadOptions
                    {
                        Conditions = conditions,
                        Tags = tags,
                        Metadata = content.Length == 0
                            ? null
                            : new Dictionary<string, string> { [Crc32CMetadata] = ObjectStoreChecksums.Crc32CToSend(content, crc32c).ToString("x8", CultureInfo.InvariantCulture) },

                        // One Put Blob for any content, so a write lands whole under its condition and the SDK buffers nothing.
                        TransferOptions = new StorageTransferOptions { InitialTransferSize = MaxPutBlobBytes },
                        TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = StorageChecksumAlgorithm.MD5 },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 400, BlobErrorCode.Md5Mismatch))
        {
            throw ObjectContentChangedException.ForServiceRefusal(name, exception, _container.Uri.Scheme == Uri.UriSchemeHttps);
        }

        return AzureBlobRequests.AppliedETag(uploaded.Value.ETag);
    }

    private BlobClient BlobOf(ObjectName name) => _container.GetBlobClient(_prefix + name.Value);

    private AppendBlobClient AppendBlobOf(ObjectName name) => _container.GetAppendBlobClient(_prefix + name.Value);
}
