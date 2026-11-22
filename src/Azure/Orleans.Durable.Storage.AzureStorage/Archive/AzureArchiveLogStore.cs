using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.AzureStorage.Archive;

/// <summary>
/// Archive logs on Azure append blobs, in Orleans Journaling's own layout (archive-logs.md §7.3, §9.1): the log
/// <c>{id}</c> is the append blob <c>wal/{id}</c>, holding its current generation's appends, and the block blob
/// <c>checkpoints/{id}/{g:D20}</c> of each generation an opener created, holding its base, so plain Orleans reads Durable Storage's
/// logs, read-only, with its own Azure provider. All knowledge of Orleans' layout is in this type and
/// <see cref="AzureArchiveLogMetadata"/>.
/// </summary>
/// <remarks>
/// <para>
/// It calls the Azure Blob SDK directly; the container's client is pinned to service version 2022-11-02 or later, whose
/// <c>x-ms-if-tags</c> conditions it relies on, and is configured by <see cref="ConfigureClientOptions"/>. An append run is
/// one <c>Append Block</c> per packed block, conditioned on <c>appendpos</c> at the block's start and on the generation's
/// index tag <c>durable-gen</c>; a key's first run first creates the append blob with <c>If-None-Match: *</c>, a 409
/// <c>BlobAlreadyExists</c> going on to the append, whose own condition decides. An opener is one <c>Put Blob</c> of its
/// checkpoint with <c>If-None-Match: *</c>, where a 409 <c>BlobAlreadyExists</c> is checked against the stored base's
/// length and MD5, then a <c>Put Blob</c> re-creating the empty write-ahead blob with the new generation's tag and
/// metadata, its base's MD5 among them, conditioned on <c>If-Match: *</c> and the tag of the generation it starts from,
/// since the tag alone does not stop a <c>Put Blob</c> from creating an absent blob, or <c>If-None-Match: *</c> from
/// <c>Origin</c>. Only 412 <c>ConditionNotMet</c> or <c>AppendPositionConditionNotMet</c>,
/// 409 <c>BlobAlreadyExists</c> on a write which expected no blob, and 404 <c>BlobNotFound</c> on one which expected a
/// blob are <see cref="ArchiveLogWriteOutcome.ConditionFailed"/> (§9.1); a code no retry can change is
/// <see cref="ArchiveLogRefusedException"/>.
/// </para>
/// <para>
/// Every write carries <c>Content-MD5</c>, which the store computes from the content it is given, the caller's verified
/// batch pages; a block's answer must give the offset and committed block count the run packed. A read is one
/// <c>Get Blob</c> of the write-ahead blob, which returns its content, length, committed block count, metadata, and ETag
/// from one version, read through: the SDK resumes a broken response under <c>If-Match</c> on its ETag, and a 412
/// answers <c>Stale</c>. When the generation's base is not empty, the read then reads its checkpoint, whose 404 answers
/// <c>Stale</c>, since a newer generation replaced it and the sweeper deleted it, and whose MD5 must be the one the
/// write-ahead blob records (§8.4).
/// </para>
/// </remarks>
public sealed class AzureArchiveLogStore : IArchiveLogStore
{
    /// <summary>The layout's name in the store's record.</summary>
    public const string LayoutName = "azure-append-blob";

    /// <summary>The largest <c>Append</c>, and block: 100 MiB, the largest <c>Append Block</c> (precondition 5).</summary>
    public const int MaxBlockBytes = 100 << 20;

    /// <summary>The most blocks an append run may bring a generation to: 49,900, below Azure's 50,000.</summary>
    public const ulong MaxBlocks = 49_900;

    /// <summary>The blocks at which the store asks for a <c>Replace</c>: 40,000, so a grain snapshots long before the refusal.</summary>
    public const ulong ReplaceRequestBlocks = 40_000;

    /// <summary>
    /// The content above which a write first asks the service to accept it (<c>Expect: 100-continue</c>), and an opener
    /// first reads whether its checkpoint exists: 1 MiB, so a straggler's refused write sends no large body.
    /// </summary>
    public const int ExpectContinueBytes = 1 << 20;

    /// <summary>The largest content one <c>Put Blob</c> takes: 5,000 MiB.</summary>
    private const long MaxPutBlobBytes = 5_000L * 1024 * 1024;

    private readonly BlobContainerClient _container;
    private readonly string _format;
    private readonly string _prefix;

    /// <summary>Initializes a new instance of the <see cref="AzureArchiveLogStore"/> class.</summary>
    /// <param name="container">
    /// The container dedicated to the cluster, which exists, and whose client uses service version 2022-11-02 or later and
    /// options which <see cref="ConfigureClientOptions"/> configured.
    /// </param>
    /// <param name="format">The <c>format</c> caller parameter of the store's record, which every blob's <c>format</c> metadata records.</param>
    /// <param name="prefix">
    /// The blob-name prefix of every blob the store names, empty or ending in <c>/</c>: the cluster's store prefix, under
    /// which plain Orleans reads the logs when its blob names carry it too (orleans.md §5.13).
    /// </param>
    public AzureArchiveLogStore(BlobContainerClient container, string format, string? prefix = null)
        : this(container, format, AzureLayout, prefix)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AzureArchiveLogStore"/> class with a layout of its own; for tests.</summary>
    internal AzureArchiveLogStore(BlobContainerClient container, string format, ArchiveLogLayout layout, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentException.ThrowIfNullOrEmpty(format);
        ArgumentNullException.ThrowIfNull(layout);
        prefix ??= string.Empty;
        if (prefix.Length > 0 && (prefix[^1] != '/' || !AzureArchiveLogMetadata.IsAdmissibleLogName(prefix[..^1])))
        {
            throw new ArgumentException("A non-empty archive-log store prefix is a log name's segments, ending in '/'.", nameof(prefix));
        }

        if (layout.Packing != ArchiveLogPacking.Blocks || layout.MaxAppendBytes > MaxBlockBytes || layout.NameRule != AzureArchiveLogMetadata.NameRule)
        {
            throw new ArgumentException("An Azure layout packs blocks of at most 100 MiB under Azure's name rule.", nameof(layout));
        }

        _container = container;
        _format = format;
        _prefix = prefix;
        Layout = layout;
    }

    /// <summary>
    /// Gets Azure's layout: blocks of up to 100 MiB, <c>Append</c>s refused past <see cref="MaxBlocks"/> and a
    /// <c>Replace</c> asked for at <see cref="ReplaceRequestBlocks"/>; and log names by
    /// <see cref="AzureArchiveLogMetadata.IsAdmissibleLogName"/>.
    /// </summary>
    public static ArchiveLogLayout AzureLayout { get; } = new(LayoutName, ArchiveLogPacking.Blocks, MaxBlockBytes, MaxBlocks, ReplaceRequestBlocks, AzureArchiveLogMetadata.NameRule);

    /// <inheritdoc/>
    public ArchiveLogLayout Layout { get; }

    /// <summary>
    /// Gets a value indicating whether the store is the <c>drops-existence-condition</c> mutant, whose opener re-creates
    /// the write-ahead blob under the starting generation's tag alone, without <c>If-Match: *</c>; for tests.
    /// </summary>
    internal bool DropsExistenceConditionMutant { get; init; }

    /// <summary>Gets the bases a page of <see cref="ListGenerationsAsync"/> asks the service for, at most; the service's 5,000 by default, fewer in tests.</summary>
    internal int ListPageSize { get; init; } = 5_000;

    /// <summary>
    /// Configures the options of the archive container's client, which the composition creates: a write above
    /// <see cref="ExpectContinueBytes"/> asks the service to accept it first, so a straggler's write, which fails its
    /// condition, sends no large body (archive-logs.md §9.1). The client's own retries, which honour <c>Retry-After</c>,
    /// are the one retry layer of each request. The options' service version is 2022-11-02 or later, whose tag conditions
    /// and blocks the stores rely on; the composition pins one.
    /// </summary>
    /// <returns><paramref name="options"/>.</returns>
    /// <exception cref="ArgumentException">The options' service version is older than 2022-11-02.</exception>
    public static BlobClientOptions ConfigureClientOptions(BlobClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Version < AzureBlobRequests.MinimumServiceVersion)
        {
            throw new ArgumentException($"Durable Storage's Azure stores need service version {AzureBlobRequests.MinimumServiceVersion} or later, not {options.Version}.", nameof(options));
        }

        options.Request100ContinueOptions = new Request100ContinueOptions { Mode = Request100ContinueMode.Always, ContentLengthThreshold = ExpectContinueBytes };
        return options;
    }

    /// <inheritdoc/>
    /// <remarks>By <see cref="AzureArchiveLogMetadata.IsAdmissibleLogName"/>, version <see cref="AzureArchiveLogMetadata.NameRule"/>.</remarks>
    public bool AdmitsName(string name) => AzureArchiveLogMetadata.IsAdmissibleLogName(name, _prefix);

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogRead> ReadAsync(ArchiveLogName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        BlobDownloadStreamingResult download;
        try
        {
            download = (await Wal(name).DownloadStreamingAsync(new BlobDownloadOptions(), cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.BlobNotFound))
        {
            return ArchiveLogRead.Absent.Instance;
        }

        using var appends = new ArcBufferWriter();
        AzureWalMetadata metadata;
        ArchiveLogPosition position;
        using (download)
        {
            metadata = WalMetadata(name, download.Details.Metadata);
            position = PositionOf(name, metadata, download.Details.ContentLength, download.Details.BlobCommittedBlockCount);
            ArchiveLogRead.Read.CheckContentLength((long)position.Length);
            if (!await CopyAsync(download.Content, (long)position.Length, appends, cancellationToken).ConfigureAwait(false))
            {
                return ArchiveLogRead.Stale.Instance;
            }
        }

        using var content = new ArcBufferWriter();
        var baseLength = 0;
        if (metadata.BaseMd5 is { } md5 && md5 != AzureArchiveLogMetadata.EmptyMd5)
        {
            if (await ReadCheckpointAsync(name, metadata.Checkpoint!, md5, content, cancellationToken).ConfigureAwait(false) is not { } length)
            {
                return ArchiveLogRead.Stale.Instance;
            }

            baseLength = length;
            ArchiveLogRead.Read.CheckContentLength(baseLength + (long)position.Length);
        }

        if (appends.Length > 0)
        {
            using var bytes = appends.ConsumeSlice(appends.Length);
            content.Write(bytes.AsReadOnlySequence());
        }

        using var buffer = content.Length == 0 ? default : content.ConsumeSlice(content.Length);
        return new ArchiveLogRead.Read(position, metadata.Opener, baseLength, buffer);
    }

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogProperties?> GetPropertiesAsync(ArchiveLogName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        BlobProperties properties;
        try
        {
            properties = (await Wal(name).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.BlobNotFound))
        {
            return null;
        }

        var metadata = WalMetadata(name, properties.Metadata);
        return new ArchiveLogProperties(PositionOf(name, metadata, properties.ContentLength, properties.BlobCommittedBlockCount), metadata.Opener);
    }

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogWriteOutcome> AppendAsync(ArchiveLogName name, ArchiveLogPosition from, IReadOnlyList<ReadOnlySequence<byte>> run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(run);
        if (run.Count == 0 || run.Any(unit => unit.IsEmpty || unit.Length > Layout.MaxAppendBytes))
        {
            throw new ArgumentException($"An append run holds at least one block, each of 1 to {Layout.MaxAppendBytes} bytes.", nameof(run));
        }

        var wal = Wal(name);
        var (generation, offset, blocks) = from.IsOrigin ? (1ul, 0ul, 0ul) : (from.Generation, from.Length, from.Units);
        if (from.IsOrigin)
        {
            try
            {
                _ = await wal.CreateAsync(
                    new AppendBlobCreateOptions
                    {
                        Metadata = AzureArchiveLogMetadata.ForWal(_format, name.Value, generation, null, null, name.KeyHash, _prefix),
                        Tags = Tags(generation),
                        Conditions = new AppendBlobRequestConditions { IfNoneMatch = ETag.All },
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (AzureBlobRequests.IsConditionFailure(exception, expectedAbsent: true))
            {
                // The log exists, perhaps by this call's own earlier attempt: the append's own condition decides.
            }
            catch (RequestFailedException exception) when (IsRefusal(exception))
            {
                throw Refused(name, exception);
            }
        }

        foreach (var unit in run)
        {
            Response<BlobAppendInfo> appended;
            using (var stream = new SequenceReadStream(unit))
            {
                try
                {
                    appended = await wal.AppendBlockAsync(
                        stream,
                        new AppendBlobAppendBlockOptions
                        {
                            Conditions = new AppendBlobRequestConditions
                            {
                                IfAppendPositionEqual = checked((long)offset),
                                TagConditions = AzureArchiveLogMetadata.TagCondition(generation),
                            },
                            TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = StorageChecksumAlgorithm.MD5, PrecalculatedChecksum = Md5Of(unit) },
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (RequestFailedException exception) when (AzureBlobRequests.IsConditionFailure(exception, expectedAbsent: false))
                {
                    return ArchiveLogWriteOutcome.ConditionFailed.Instance;
                }
                catch (RequestFailedException exception) when (IsRefusal(exception))
                {
                    throw Refused(name, exception);
                }
            }

            blocks++;
            CheckAppended(name, offset, blocks, appended.Value);
            offset += (ulong)unit.Length;
        }

        return ArchiveLogWriteOutcome.Applied.Instance;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A base above <see cref="ExpectContinueBytes"/> is first looked for, so a replay sends no large body; a checkpoint
    /// which exists, found so or by its <c>Put Blob</c>'s 409, must hold the opener's own base, by length and MD5.
    /// </remarks>
    public async ValueTask<ArchiveLogWriteOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, ReadOnlySequence<byte> content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var generation = checked(from.Generation + 1);
        var md5 = Md5Of(content);
        var checkpoint = Checkpoint(name.Value, generation);
        if (content.Length <= ExpectContinueBytes || !await CheckStoredBaseAsync(name, checkpoint, content.Length, md5, cancellationToken).ConfigureAwait(false))
        {
            await PutBaseAsync(name, checkpoint, content, md5, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            _ = await Wal(name).CreateAsync(
                new AppendBlobCreateOptions
                {
                    Metadata = AzureArchiveLogMetadata.ForWal(_format, name.Value, generation, opener, md5, name.KeyHash, _prefix),
                    Tags = Tags(generation),
                    Conditions = from.Generation == 0
                        ? new AppendBlobRequestConditions { IfNoneMatch = ETag.All }
                        : new AppendBlobRequestConditions
                        {
                            // x-ms-if-tags alone lets a Put Blob create an absent blob (§9.1, precondition 3).
                            IfMatch = DropsExistenceConditionMutant ? null : ETag.All,
                            TagConditions = AzureArchiveLogMetadata.TagCondition(from.Generation),
                        },
                },
                cancellationToken).ConfigureAwait(false);
            return ArchiveLogWriteOutcome.Applied.Instance;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.IsConditionFailure(exception, expectedAbsent: from.Generation == 0))
        {
            return ArchiveLogWriteOutcome.ConditionFailed.Instance;
        }
        catch (RequestFailedException exception) when (IsRefusal(exception))
        {
            throw Refused(name, exception);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Lists the blobs under <c>checkpoints/</c> with their metadata, a page per service response, resuming from the
    /// service's continuation, <c>List Blobs</c>' marker, which names the position before the next blob's name, and stays
    /// valid whatever is created or deleted; and parses each name and its <c>durable_key</c> strictly (§7.1, §7.3), so a
    /// page may list fewer bases than it asked for. Other blobs are not listed.
    /// </remarks>
    public async IAsyncEnumerable<ArchiveLogGenerationPage> ListGenerationsAsync(string? continuation, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pages = _container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, AzureArchiveLogMetadata.CheckpointsPrefix(_prefix), cancellationToken).AsPages(continuation, ListPageSize);
        await foreach (var page in pages.ConfigureAwait(false))
        {
            var generations = new List<ArchiveLogGeneration>(page.Values.Count);
            foreach (var blob in page.Values)
            {
                if (AzureArchiveLogMetadata.ParseCheckpointName(blob.Name, _prefix) is { } parsed)
                {
                    generations.Add(new ArchiveLogGeneration(parsed.Log, parsed.Generation, AzureArchiveLogMetadata.CheckpointKeyHash(blob.Metadata)));
                }
            }

            yield return new ArchiveLogGenerationPage(generations, string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="log"/> is not a name the store forms.</exception>
    public async ValueTask DeleteGenerationAsync(string log, ulong generation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(log);
        try
        {
            _ = await Checkpoint(log, generation).DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.BlobNotFound))
        {
            // The checkpoint is absent, as the deletion leaves it.
        }
    }

    /// <summary>
    /// Returns the MD5 of <paramref name="content"/>, computed here from the caller's own bytes, the verified batch pages,
    /// so the service refuses bytes damaged on the way (§9.1).
    /// </summary>
    /// <remarks>
    /// The SDK sends a CRC-64 only as a structured message (XSM/1.0), which only newer service versions read, and requires
    /// the message's acknowledgement of every response it does not classify as an error, a conditional create's 409
    /// <c>BlobAlreadyExists</c> among them; <c>Content-MD5</c> carries the same guarantee on every service version.
    /// </remarks>
    private static byte[] Md5Of(ReadOnlySequence<byte> content)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (var segment in content)
        {
            md5.AppendData(segment.Span);
        }

        return md5.GetHashAndReset();
    }

    private static Dictionary<string, string> Tags(ulong generation) =>
        new(StringComparer.Ordinal) { [AzureArchiveLogMetadata.GenerationTag] = AzureArchiveLogMetadata.TagOf(generation) };

    /// <summary>
    /// Returns whether a write failed for a reason no retry changes: a name, blob type, block count, or body size the
    /// service refuses, a container which does not exist, or an identity without the permission.
    /// </summary>
    private static bool IsRefusal(RequestFailedException exception) =>
        AzureBlobRequests.HasErrorCode(exception, 409, BlobErrorCode.BlockCountExceedsLimit)
        || AzureBlobRequests.HasErrorCode(exception, 409, BlobErrorCode.InvalidBlobType)
        || AzureBlobRequests.HasErrorCode(exception, 400, BlobErrorCode.InvalidResourceName)
        || AzureBlobRequests.HasErrorCode(exception, 400, BlobErrorCode.InvalidUri)
        || AzureBlobRequests.HasErrorCode(exception, 413, BlobErrorCode.RequestBodyTooLarge)
        || AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.ContainerNotFound)
        || AzureBlobRequests.HasErrorCode(exception, 403, BlobErrorCode.AuthorizationPermissionMismatch);

    private static ArchiveLogRefusedException Refused(ArchiveLogName name, RequestFailedException exception) =>
        new($"Azure refused a write of the archive log '{name}' with {exception.Status} {exception.ErrorCode}, which no retry changes.", exception);

    /// <summary>Returns whether a read's response failed because the blob changed or went away during it, which answers <c>Stale</c>.</summary>
    private static bool ChangedDuringRead(RequestFailedException exception) =>
        exception.Status == 412 || AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.BlobNotFound);

    /// <summary>Reads a write-ahead blob's metadata.</summary>
    /// <exception cref="ArchiveLogCorruptException">The metadata breaks its format or names another key.</exception>
    private AzureWalMetadata WalMetadata(ArchiveLogName name, IDictionary<string, string> metadata) =>
        AzureArchiveLogMetadata.ReadWal(metadata, name.Value, name.KeyHash, tag: null, _prefix) switch
        {
            AzureWalMetadata read => read,
            InvalidDataException rejection => throw new ArchiveLogCorruptException($"The archive log '{name}' breaks the metadata of its write-ahead blob: {rejection.Message}", rejection),
            null => throw new UnreachableException(),
        };

    /// <summary>
    /// Returns the log's position from its metadata's generation and opener's base rows, and its blob's length and committed
    /// blocks, which every token's rules bind.
    /// </summary>
    /// <exception cref="ArchiveLogCorruptException">The length and blocks are no position's.</exception>
    private static ArchiveLogPosition PositionOf(ArchiveLogName name, AzureWalMetadata metadata, long length, int blocks)
    {
        var empty = length == 0;
        if (length < 0 || blocks < 0 || empty != (blocks == 0) || blocks > length)
        {
            throw new ArchiveLogCorruptException(string.Create(
                CultureInfo.InvariantCulture,
                $"The archive log '{name}' holds {length} bytes in {blocks} committed blocks, which no drain's blocks make."));
        }

        return new ArchiveLogPosition(metadata.Generation, metadata.Opener?.Rows ?? 0, (ulong)length, (ulong)blocks);
    }

    /// <summary>Checks a block's answer against the run's packing: its offset and the log's committed block count after it.</summary>
    /// <exception cref="ArchiveLogCorruptException">The answer contradicts the run's packing.</exception>
    private static void CheckAppended(ArchiveLogName name, ulong offset, ulong blocks, BlobAppendInfo appended)
    {
        if (!ulong.TryParse(appended.BlobAppendOffset, NumberStyles.None, CultureInfo.InvariantCulture, out var at) || at != offset || (ulong)appended.BlobCommittedBlockCount != blocks)
        {
            throw new ArchiveLogCorruptException(string.Create(
                CultureInfo.InvariantCulture,
                $"Azure appended a block of the archive log '{name}' at {appended.BlobAppendOffset} as block {appended.BlobCommittedBlockCount}, where the run packed it at {offset} as block {blocks}."));
        }
    }

    /// <summary>Copies a response's <paramref name="length"/> bytes, read through as the SDK resumes it.</summary>
    /// <returns>Whether the response served its bytes from one version, rather than the blob changing or going away.</returns>
    /// <exception cref="IOException">The response served another length than it promised.</exception>
    private static async ValueTask<bool> CopyAsync(Stream source, long length, ArcBufferWriter writer, CancellationToken cancellationToken)
    {
        var before = writer.Length;
        try
        {
            await ArcBufferWriteStream.CopyFromAsync(source, writer, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (ChangedDuringRead(exception))
        {
            return false;
        }

        return writer.Length - before == length
            ? true
            : throw new IOException(string.Create(CultureInfo.InvariantCulture, $"Azure Blob served {writer.Length - before} bytes where its response promised {length}."));
    }

    /// <summary>Returns the write-ahead blob of a log, whose name the store forms.</summary>
    /// <exception cref="ArgumentException">The log's name is not one the store forms: its caller did not check <see cref="AdmitsName"/>.</exception>
    private AppendBlobClient Wal(ArchiveLogName name) =>
        AzureArchiveLogMetadata.IsAdmissibleLogName(name.Value, _prefix)
            ? _container.GetAppendBlobClient(AzureArchiveLogMetadata.WalName(name.Value, _prefix))
            : throw new ArgumentException($"The store forms no log name '{name}' (archive-logs.md §7.1).", nameof(name));

    /// <summary>Returns a generation's checkpoint of a log, whose name the store forms.</summary>
    /// <exception cref="ArgumentException">The log's name is not one the store forms.</exception>
    private BlockBlobClient Checkpoint(string log, ulong generation) =>
        AzureArchiveLogMetadata.IsAdmissibleLogName(log, _prefix)
            ? _container.GetBlockBlobClient(AzureArchiveLogMetadata.CheckpointName(log, generation, _prefix))
            : throw new ArgumentException($"The store forms no log name '{log}' (archive-logs.md §7.1).", nameof(log));

    /// <summary>Puts an opener's base, once; a checkpoint which exists must hold the same base.</summary>
    private async ValueTask PutBaseAsync(ArchiveLogName name, BlockBlobClient checkpoint, ReadOnlySequence<byte> content, byte[] md5, CancellationToken cancellationToken)
    {
        using var stream = new SequenceReadStream(content);
        try
        {
            _ = await checkpoint.UploadAsync(
                stream,
                new BlobUploadOptions
                {
                    Metadata = AzureArchiveLogMetadata.ForCheckpoint(_format, name.KeyHash),
                    HttpHeaders = new BlobHttpHeaders { ContentHash = md5 },
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },

                    // One Put Blob for any base: a key's content is below 2 GiB, and one request takes up to 5,000 MiB. The
                    // SDK computes the request's own Content-MD5 from the caller's bytes, which it reads for the upload.
                    TransferOptions = new StorageTransferOptions { InitialTransferSize = MaxPutBlobBytes, MaximumTransferSize = MaxPutBlobBytes },
                    TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = StorageChecksumAlgorithm.MD5 },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 409, BlobErrorCode.BlobAlreadyExists))
        {
            // The checkpoint exists: by the caller's demand it is this write's own, which the stored length and MD5 confirm.
            _ = await CheckStoredBaseAsync(name, checkpoint, content.Length, md5, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (IsRefusal(exception))
        {
            throw Refused(name, exception);
        }
    }

    /// <summary>Checks a stored checkpoint against an opener's base, by one properties read.</summary>
    /// <returns>Whether the checkpoint exists; a base the sweeper deleted belongs to a generation the log has passed.</returns>
    /// <exception cref="ArchiveLogCorruptException">The stored base differs: two writes came from one position.</exception>
    private static async ValueTask<bool> CheckStoredBaseAsync(ArchiveLogName name, BlockBlobClient checkpoint, long length, byte[] md5, CancellationToken cancellationToken)
    {
        BlobProperties stored;
        try
        {
            stored = (await checkpoint.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.BlobNotFound))
        {
            return false;
        }

        if (stored.ContentLength != length || stored.ContentHash is not { } hash || !hash.AsSpan().SequenceEqual(md5))
        {
            throw new ArchiveLogCorruptException(string.Create(
                CultureInfo.InvariantCulture,
                $"The archive log '{name}' holds a checkpoint '{checkpoint.Name}' of {stored.ContentLength} bytes whose MD5 is not the {length}-byte base the opener writes: two writes came from one position."));
        }

        return true;
    }

    /// <summary>Reads the generation's checkpoint, checking its MD5, into <paramref name="content"/>.</summary>
    /// <returns>The checkpoint's length, or <see langword="null"/> when it is gone or changed, which answers <c>Stale</c>.</returns>
    /// <exception cref="ArchiveLogCorruptException">The checkpoint's metadata breaks its format or names another key, or its bytes are not the MD5 the write-ahead blob records.</exception>
    private async ValueTask<int?> ReadCheckpointAsync(ArchiveLogName name, string checkpoint, string md5, ArcBufferWriter content, CancellationToken cancellationToken)
    {
        BlobDownloadStreamingResult download;
        try
        {
            download = (await _container.GetBlockBlobClient(checkpoint).DownloadStreamingAsync(new BlobDownloadOptions(), cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException exception) when (AzureBlobRequests.HasErrorCode(exception, 404, BlobErrorCode.BlobNotFound))
        {
            return null;
        }

        using var bytes = new ArcBufferWriter();
        using (download)
        {
            if (AzureArchiveLogMetadata.ReadCheckpoint(download.Details.Metadata, name.KeyHash) is { } rejection)
            {
                throw new ArchiveLogCorruptException($"The archive log '{name}' breaks the metadata of its checkpoint '{checkpoint}': {rejection.Message}", rejection);
            }

            ArchiveLogRead.Read.CheckContentLength(download.Details.ContentLength);
            if (!await CopyAsync(download.Content, download.Details.ContentLength, bytes, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        var length = bytes.Length;
        using var @base = length == 0 ? default : bytes.ConsumeSlice(length);
        if (Convert.ToHexStringLower(Md5Of(@base.AsReadOnlySequence())) != md5)
        {
            throw new ArchiveLogCorruptException($"The archive log '{name}''s checkpoint '{checkpoint}' does not hash to the MD5 its write-ahead blob records.");
        }

        content.Write(@base.AsReadOnlySequence());
        return length;
    }
}
