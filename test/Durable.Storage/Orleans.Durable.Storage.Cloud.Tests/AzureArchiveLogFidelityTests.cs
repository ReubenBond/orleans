using System.Buffers;
using System.Globalization;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// An account the Azure fidelity tests run on.
/// </summary>
public enum AzureBackend
{
    /// <summary>Azurite, from the AppHost, under <c>DURABLE_STORAGE_EMULATORS=1</c>.</summary>
    Azurite,

    /// <summary>A live storage account, whose blob service endpoint <c>DURABLE_STORAGE_AZURE_LIVE</c> holds.</summary>
    Live,
}

/// <summary>
/// The preconditions of archive logs §9.1 on which <see cref="AzureArchiveLogStore"/> rests, one test each, on Azurite and
/// on a live account at milestones; each backend's result is its own test case in the run's report (archive-logs.md §10).
/// Each failure's status and error code are asserted, as the store tells condition failures from refusals by them; and
/// the checksums and names the store relies on: a body whose MD5 differs is refused, and a dot segment, which a URI
/// collapses, names another blob.
/// </summary>
/// <remarks>
/// Each test runs in a container of its own, which it deletes. Without <c>DURABLE_STORAGE_EMULATORS=1</c> the Azurite cases skip,
/// and the live cases run on <see cref="AzureLiveAccount"/>, skipping with its reason while its variable is unset.
/// Where Azurite does not model a precondition, its case checks what Azurite does model, pins what it does instead,
/// and says which: preconditions 7 and 7b hold on Azurite only as far as its case says, and only a live run establishes
/// them.
/// </remarks>
public sealed class AzureArchiveLogFidelityTests(CloudEmulators emulators) : IDisposable
{
    private const int Racers = 8;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>Gets the backends: Azurite and the live account.</summary>
    public static TheoryData<AzureBackend> Backends => [AzureBackend.Azurite, AzureBackend.Live];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Precondition 1: <c>x-ms-if-tags</c> is evaluated atomically with <c>Append Block</c> and with <c>Put Blob</c>.
    /// Appends racing a re-creation which moves the tag either land before it, which erases them, or fail; and of two
    /// re-creations conditioned on one tag, exactly one lands. Live, the re-creations carry the store's conditions,
    /// <c>If-Match: *</c> with the tag; on Azurite, which ignores the tag beside <c>If-Match: *</c> (3b), the tag alone.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task TagConditionsAreEvaluatedAtomicallyWithAppendBlockAndPutBlob(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var exists = backend == AzureBackend.Live;
        var wal = account.Container.GetAppendBlobClient("wal/atomic");
        _ = await wal.CreateAsync(new AppendBlobCreateOptions { Tags = Tags(1) }, Token);

        var appends = Enumerable.Range(0, Racers).Select(index => Outcome(() => wal.AppendBlockAsync(Bytes(index + 1), Append(appendPosition: null, generation: 1), Token)));
        var recreate = Outcome(() => wal.CreateAsync(Recreate(1, 2, exists: exists), Token));
        var answers = await Task.WhenAll([.. appends, recreate]);

        Assert.Equal((201, null), await recreate);
        Assert.All(answers, static answer => Assert.Contains(answer, new (int, string?)[] { (201, null), (412, "ConditionNotMet") }));
        Assert.Equal(0, (await wal.GetPropertiesAsync(cancellationToken: Token)).Value.ContentLength);
        Assert.Equal(Tags(2), await TagsOf(wal));

        var racing = await Task.WhenAll(Enumerable.Range(0, Racers).Select(index => Outcome(() => wal.CreateAsync(Recreate(2, 3, index, exists), Token))));
        Assert.Equal(1, racing.Count(static answer => answer.Status == 201));
        Assert.All(racing.Where(static answer => answer.Status != 201), static answer => Assert.Equal((412, "ConditionNotMet"), answer));
        Assert.Equal(Tags(3), await TagsOf(wal));
    }

    /// <summary>
    /// Precondition 2: a <c>Put Blob</c> which sets <c>x-ms-tags</c> never exposes new content under old tags. A reader's
    /// one <c>Get Blob Properties</c> under <c>x-ms-if-tags</c> naming a generation, which re-creations race, answers only
    /// metadata of that generation whenever its condition holds.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task APutBlobWhichSetsTagsNeverExposesNewContentUnderOldTags(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var wal = account.Container.GetAppendBlobClient("wal/exposure");
        _ = await wal.CreateAsync(Recreate(0, 1), Token);
        const ulong generations = 12;
        var writer = Task.Run(
            async () =>
            {
                for (var generation = 1ul; generation < generations; generation++)
                {
                    _ = await wal.CreateAsync(Recreate(generation, generation + 1), Token);
                }
            },
            Token);

        var (reads, held, guess) = (0, 0, 1ul);
        while (!writer.IsCompleted || held == 0)
        {
            try
            {
                var conditioned = new BlobRequestConditions { TagConditions = AzureArchiveLogMetadata.TagCondition(guess) };
                var metadata = (await wal.GetPropertiesAsync(conditioned, Token)).Value.Metadata;
                Assert.Equal(guess.ToString(CultureInfo.InvariantCulture), metadata[AzureArchiveLogMetadata.WalGeneration]);
                held++;
            }
            catch (RequestFailedException exception) when (exception.Status == 412)
            {
                Assert.Equal("ConditionNotMet", exception.ErrorCode);
                guess = guess % generations + 1;
            }

            reads++;
        }

        await writer;
        Assert.True(held > 0, $"None of {reads} reads met its tag condition.");
        Assert.Equal(Tags(generations), await TagsOf(wal));
    }

    /// <summary>
    /// Precondition 3: on an absent blob, <c>x-ms-if-tags</c> fails an <c>Append Block</c> and a <c>Get Blob Properties</c>
    /// with 404 <c>BlobNotFound</c>, while a <c>Put Blob</c> under it alone creates the blob; the live service and Azurite
    /// answer alike.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ATagConditionOnAnAbsentBlobFailsAnAppendAndAReadButNotAPutBlob(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var wal = account.Container.GetAppendBlobClient("wal/absent");

        var append = await Outcome(() => wal.AppendBlockAsync(Bytes(1), Append(appendPosition: 0, generation: 1), Token));
        var read = await Outcome(() => wal.GetPropertiesAsync(new BlobRequestConditions { TagConditions = AzureArchiveLogMetadata.TagCondition(1) }, Token));
        var create = await Outcome(() => wal.CreateAsync(Recreate(1, 2, exists: false), Token));

        Assert.Equal((404, "BlobNotFound"), append);
        Assert.Equal((404, "BlobNotFound"), read);
        Assert.Equal((201, null), create);
        Assert.Equal(Tags(2), await TagsOf(wal));
    }

    /// <summary>
    /// Precondition 3b: <c>If-Match: *</c> with <c>x-ms-if-tags</c> fails a <c>Put Blob</c> on an absent blob with 412
    /// <c>ConditionNotMet</c> and creates nothing, and on an existing blob is the tag condition alone: it fails under
    /// another generation's tag and lands under its own. The opener's re-creation of a write-ahead blob carries both.
    /// Azurite models it only in part: beside <c>If-Match: *</c> it ignores <c>x-ms-if-tags</c>, so there a re-creation
    /// under another generation's tag lands, as its case pins, and only the live run and the fake transport establish
    /// the opener's tag condition.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AnExistenceConditionWithTheTagConditionFailsAPutBlobOnAnAbsentBlob(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var live = backend == AzureBackend.Live;
        var absent = account.Container.GetAppendBlobClient("wal/absent");
        var wal = account.Container.GetAppendBlobClient("wal/existence");
        _ = await wal.CreateAsync(Recreate(0, 1), Token);

        Assert.Equal((412, "ConditionNotMet"), await Outcome(() => absent.CreateAsync(Recreate(1, 2), Token)));
        Assert.False((await absent.ExistsAsync(Token)).Value);
        Assert.Equal(live ? (412, "ConditionNotMet") : (201, null), await Outcome(() => wal.CreateAsync(Recreate(2, 3), Token)));
        Assert.Equal((201, null), await Outcome(() => wal.CreateAsync(Recreate(1, 2), Token)));
        Assert.Equal(Tags(2), await TagsOf(wal));
    }

    /// <summary>
    /// Precondition 4: <c>Append Block</c> honours <c>appendpos</c> together with <c>x-ms-if-tags</c>: it lands only when
    /// both hold.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AppendBlockHonoursAppendPositionTogetherWithTheTagCondition(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var wal = account.Container.GetAppendBlobClient("wal/conditions");
        _ = await wal.CreateAsync(new AppendBlobCreateOptions { Tags = Tags(1) }, Token);
        _ = await wal.AppendBlockAsync(Bytes(3), Append(appendPosition: 0, generation: 1), Token);

        Assert.Equal((412, "AppendPositionConditionNotMet"), await Outcome(() => wal.AppendBlockAsync(Bytes(2), Append(appendPosition: 0, generation: 1), Token)));
        Assert.Equal((412, "ConditionNotMet"), await Outcome(() => wal.AppendBlockAsync(Bytes(2), Append(appendPosition: 3, generation: 2), Token)));
        Assert.Equal(412, (await Outcome(() => wal.AppendBlockAsync(Bytes(2), Append(appendPosition: 0, generation: 2), Token))).Status);
        var landed = await wal.AppendBlockAsync(Bytes(2), Append(appendPosition: 3, generation: 1), Token);

        Assert.Equal("3", landed.Value.BlobAppendOffset);
        Assert.Equal(2, landed.Value.BlobCommittedBlockCount);
        Assert.Equal(5, (await wal.GetPropertiesAsync(cancellationToken: Token)).Value.ContentLength);
    }

    /// <summary>
    /// Precondition 5: <c>Append Block</c> accepts 100 MiB, the <c>MaxAppendBytes</c> recorded for Azure, through the
    /// store's own request.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AppendBlockAcceptsTheRecordedMaxAppendBytes(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var name = new ArchiveLogName("block", 1);
        var block = new byte[AzureArchiveLogStore.AzureLayout.MaxAppendBytes!.Value];
        block[^1] = 1;

        var outcome = await account.Store.AppendAsync(name, ArchiveLogPosition.Origin, [new ReadOnlySequence<byte>(block)], Token);

        Assert.IsType<ArchiveLogWriteOutcome.Applied>(outcome.Value);
        Assert.Equal(new ArchiveLogPosition(1, 0, (ulong)block.Length, 1), (await account.Store.GetPropertiesAsync(name, Token))?.Position);
    }

    /// <summary>
    /// Precondition 6: the identity may write blob index tags, and the account has no hierarchical namespace.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task TheIdentityWritesIndexTagsAndTheAccountHasNoHierarchicalNamespace(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var wal = account.Container.GetAppendBlobClient("wal/tags");
        _ = await wal.CreateAsync(new AppendBlobCreateOptions(), Token);

        _ = await wal.SetTagsAsync(Tags(7), cancellationToken: Token);

        Assert.Equal(Tags(7), await TagsOf(wal));
        Assert.False((await account.Service.GetAccountInfoAsync(Token)).Value.IsHierarchicalNamespaceEnabled);
    }

    /// <summary>
    /// Precondition 7: <c>Get Blob</c> returns content, length, committed block count, and metadata from one version.
    /// Blocks of lengths 1, 2, 3, … append while readers read, and each read's length, block count, and content agree.
    /// Azurite's <c>Get Blob</c>, racing an append, may report a committed block count other than its content's, so its
    /// case reads between the appends.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task GetBlobAnswersFromOneVersion(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var wal = account.Container.GetAppendBlobClient("wal/version");
        _ = await wal.CreateAsync(Recreate(0, 1), Token);
        const int blocks = 24;
        if (backend == AzureBackend.Azurite)
        {
            for (var block = 1; block <= blocks; block++)
            {
                _ = await wal.AppendBlockAsync(Bytes(block, fill: (byte)block), cancellationToken: Token);
                Assert.Equal(block, await ReadOneVersionAsync(wal));
            }

            return;
        }

        var writer = Task.Run(
            async () =>
            {
                for (var block = 1; block <= blocks; block++)
                {
                    _ = await wal.AppendBlockAsync(Bytes(block, fill: (byte)block), cancellationToken: Token);
                }
            },
            Token);

        do
        {
            _ = await ReadOneVersionAsync(wal);
        }
        while (!writer.IsCompleted);

        await writer;
    }

    /// <summary>
    /// Precondition 7b: <c>Get Blob Properties</c>, the classifier's one read, returns length, committed block count, and
    /// metadata from one version. Blocks of lengths 1, 2, 3, … append while readers read their properties, and each read's
    /// length is its block count's. As with precondition 7, Azurite's case reads between the appends.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task GetBlobPropertiesAnswersFromOneVersion(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var wal = account.Container.GetAppendBlobClient("wal/properties");
        _ = await wal.CreateAsync(Recreate(0, 1), Token);
        const int blocks = 24;
        if (backend == AzureBackend.Azurite)
        {
            for (var block = 1; block <= blocks; block++)
            {
                _ = await wal.AppendBlockAsync(Bytes(block, fill: (byte)block), cancellationToken: Token);
                Assert.Equal(block, await PropertiesOfOneVersionAsync(wal));
            }

            return;
        }

        var writer = Task.Run(
            async () =>
            {
                for (var block = 1; block <= blocks; block++)
                {
                    _ = await wal.AppendBlockAsync(Bytes(block, fill: (byte)block), cancellationToken: Token);
                }
            },
            Token);

        do
        {
            _ = await PropertiesOfOneVersionAsync(wal);
        }
        while (!writer.IsCompleted);

        await writer;
    }

    /// <summary>
    /// The store's transit checksum: an <c>Append Block</c> whose body differs from the MD5 the request carries is refused
    /// with 400 <c>Md5Mismatch</c> and lands nothing (a <c>Put Blob</c>'s, which the SDK computes from the body it sends,
    /// the fake transport damages in transit); and a checkpoint's <c>Put Blob</c> under <c>If-None-Match: *</c> on an
    /// existing checkpoint answers 409 <c>BlobAlreadyExists</c>, the one checkpoint failure the store reads as a condition
    /// failure.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AMismatchedMd5IsRefusedAndAnExistingCheckpointAnswersBlobAlreadyExists(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var wal = account.Container.GetAppendBlobClient("wal/md5");
        _ = await wal.CreateAsync(new AppendBlobCreateOptions(), Token);
        var wrong = new byte[16];

        var append = await Outcome(() => wal.AppendBlockAsync(Bytes(3), new AppendBlobAppendBlockOptions { TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = StorageChecksumAlgorithm.MD5, PrecalculatedChecksum = wrong } }, Token));

        Assert.Equal((400, "Md5Mismatch"), append);
        Assert.Equal(0, (await wal.GetPropertiesAsync(cancellationToken: Token)).Value.ContentLength);

        var checkpoint = account.Container.GetBlockBlobClient("checkpoints/md5/00000000000000000001");
        _ = await checkpoint.UploadAsync(Bytes(3), cancellationToken: Token);
        var again = await Outcome(() => checkpoint.UploadAsync(Bytes(3), new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, Token));
        Assert.Equal((409, "BlobAlreadyExists"), again);
    }

    /// <summary>
    /// The names the store forms (DR 7, <see cref="AzureArchiveLogMetadata.IsAdmissibleLogName"/>): a <c>..</c> segment,
    /// which the URI collapses, names another blob, so the store admits no such name; a name of 255 segments is a blob's,
    /// and the 256th is past Azure's bound, where the live service refuses it with 400 and no error code and Azurite, which
    /// does not model the bound, stores it, as its case pins. The store forms at most 254, a deliberate margin of one.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ADotSegmentNamesAnotherBlobAndNamesHaveAtMost255Segments(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);

        _ = await account.Container.GetAppendBlobClient("wal/k/../y").CreateAsync(new AppendBlobCreateOptions(), Token);
        var names = await account.Container.GetBlobsAsync(cancellationToken: Token).Select(static blob => blob.Name).ToListAsync(Token);

        Assert.Equal(["wal/y"], names);
        Assert.False(AzureArchiveLogMetadata.IsAdmissibleLogName("k/../y"));

        var longest = string.Join('/', Enumerable.Repeat("s", 255));
        Assert.Equal((201, null), await Outcome(() => account.Container.GetAppendBlobClient(longest).CreateAsync(new AppendBlobCreateOptions(), Token)));
        var past = await Outcome(() => account.Container.GetAppendBlobClient(longest + "/s").CreateAsync(new AppendBlobCreateOptions(), Token));
        Assert.Equal(backend == AzureBackend.Live ? 400 : 201, past.Status);
        Assert.True(string.IsNullOrEmpty(past.Code), $"The answer carries the error code {past.Code}.");
    }

    /// <summary>Reads a write-ahead blob's properties once, checks that its length is its block count's, and returns its block count.</summary>
    private static async Task<int> PropertiesOfOneVersionAsync(AppendBlobClient wal)
    {
        var properties = (await wal.GetPropertiesAsync(cancellationToken: Token)).Value;
        var count = properties.BlobCommittedBlockCount;
        Assert.Equal((long)count * (count + 1) / 2, properties.ContentLength);
        Assert.Equal("1", properties.Metadata[AzureArchiveLogMetadata.WalGeneration]);
        return count;
    }

    /// <summary>
    /// Reads a write-ahead blob of blocks of lengths 1, 2, 3, … once, checks that its length, committed block count,
    /// content, and metadata agree, and returns its block count.
    /// </summary>
    private static async Task<int> ReadOneVersionAsync(AppendBlobClient wal)
    {
        using var download = (await wal.DownloadStreamingAsync(cancellationToken: Token)).Value;
        using var content = new MemoryStream();
        await download.Content.CopyToAsync(content, Token);
        var count = download.Details.BlobCommittedBlockCount;
        Assert.Equal((long)count * (count + 1) / 2, content.Length);
        Assert.Equal(content.Length, download.Details.ContentLength);
        Assert.Equal("1", download.Details.Metadata[AzureArchiveLogMetadata.WalGeneration]);
        Assert.Equal([.. Enumerable.Range(1, count).SelectMany(static block => Enumerable.Repeat((byte)block, block))], content.ToArray());
        return count;
    }

    /// <summary>
    /// Precondition 8, on the storage: a checkpoint holding a <c>Replace</c> payload followed by <c>Append</c> payloads,
    /// and the appends after it, read back as their concatenation, which Orleans' concatenable journal formats replay as
    /// one state (Orleans' own replay of it is row 4.9's interop test).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ACheckpointAndTheAppendsAfterItReadAsTheirConcatenation(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        var name = new ArchiveLogName("concatenation", 1);
        byte[] first = [1, 1];
        byte[] checkpoint = [2, 2, 2, 3, 3];
        byte[] after = [4];

        Assert.IsType<ArchiveLogWriteOutcome.Applied>((await account.Store.AppendAsync(name, ArchiveLogPosition.Origin, [new ReadOnlySequence<byte>(first)], Token)).Value);
        Assert.IsType<ArchiveLogWriteOutcome.Applied>((await account.Store.OpenAsync(name, new ArchiveLogPosition(1, 0, 2, 1), new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, 1), new ReadOnlySequence<byte>(checkpoint), Token)).Value);
        Assert.IsType<ArchiveLogWriteOutcome.Applied>((await account.Store.AppendAsync(name, new ArchiveLogPosition(2, 1, 0, 0), [new ReadOnlySequence<byte>(after)], Token)).Value);

        var read = await account.Store.ReadAsync(name, Token);
        var log = Assert.IsType<ArchiveLogRead.Read>(read.Value);
        using (log)
        {
            Assert.True(log.HasBase);
            Assert.Equal(new ArchiveLogPosition(2, 1, 1, 1), log.Position);
            Assert.Equal([.. checkpoint, .. after], log.Content.ToArray());
        }
    }

    /// <summary>
    /// The sweeper's listing (archive-logs.md §8.5) on the service: <c>include=metadata</c> returns each checkpoint's
    /// <c>durable_key</c>, so every listed generation names its log's key hash, in the order of its blob's name, a nested
    /// log's after its parent's.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ListGenerationsReturnsEachCheckpointsKeyHash(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        ArchiveLogName[] names = [new("listed", 0x1234_5678_9abc_def0), new("listed/nested", 42)];
        foreach (var name in names)
        {
            Assert.IsType<ArchiveLogWriteOutcome.Applied>((await account.Store.AppendAsync(name, ArchiveLogPosition.Origin, [new ReadOnlySequence<byte>([1])], Token)).Value);
            Assert.IsType<ArchiveLogWriteOutcome.Applied>((await account.Store.OpenAsync(name, new ArchiveLogPosition(1, 0, 1, 1), new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, 1), new ReadOnlySequence<byte>([2]), Token)).Value);
        }

        var listed = new List<ArchiveLogGeneration>();
        await foreach (var generation in account.Store.ListGenerationsAsync(continuation: null, Token).SelectMany(static page => page.Generations))
        {
            listed.Add(generation);
        }

        Assert.Equal([.. names.Select(static name => new ArchiveLogGeneration(name.Value, 2, name.KeyHash))], listed);
    }

    /// <summary>
    /// archive-logs.md §3, §8.5: the bases' listing meets its page contract on the service, with pages of two bases: the
    /// pages concatenate to the whole listing in order, each continuation, <c>List Blobs</c>' marker, resumes at exactly
    /// the next page, only the last is null, an empty container lists one empty page, and a marker outlives the deletion of
    /// the bases about it and the creation of others.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task TheBasesListingMeetsItsPageContract(AzureBackend backend)
    {
        await using var account = await AccountAsync(backend);
        await ArchiveLogPageContract.CheckAsync(new AzureArchiveLogStore(account.Container, "json") { ListPageSize = 2 }, Token);
    }

    private static Dictionary<string, string> Tags(ulong generation) =>
        new(StringComparer.Ordinal) { [AzureArchiveLogMetadata.GenerationTag] = AzureArchiveLogMetadata.TagOf(generation) };

    private static async Task<Dictionary<string, string>> TagsOf(AppendBlobClient blob) =>
        new((await blob.GetTagsAsync(cancellationToken: Token)).Value.Tags, StringComparer.Ordinal);

    private static MemoryStream Bytes(int length, byte fill = 7) => new([.. Enumerable.Repeat(fill, length)]);

    private static AppendBlobAppendBlockOptions Append(long? appendPosition, ulong generation) =>
        new()
        {
            Conditions = new AppendBlobRequestConditions
            {
                IfAppendPositionEqual = appendPosition,
                TagConditions = AzureArchiveLogMetadata.TagCondition(generation),
            },
        };

    /// <summary>
    /// The options of the store's re-creation of a write-ahead blob at <paramref name="generation"/>, conditioned on
    /// <c>If-Match: *</c> and the tag of <paramref name="from"/>, or on the blob's absence when it is 0, with metadata told
    /// apart by <paramref name="writer"/>; without <paramref name="exists"/>, on the tag alone.
    /// </summary>
    private static AppendBlobCreateOptions Recreate(ulong from, ulong generation, int writer = 0, bool exists = true)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AzureArchiveLogMetadata.WalGeneration] = generation.ToString(CultureInfo.InvariantCulture),
            ["writer"] = writer.ToString(CultureInfo.InvariantCulture),
        };
        return new AppendBlobCreateOptions
        {
            Metadata = metadata,
            Tags = Tags(generation),
            Conditions = from == 0
                ? new AppendBlobRequestConditions { IfNoneMatch = ETag.All }
                : new AppendBlobRequestConditions { IfMatch = exists ? ETag.All : null, TagConditions = AzureArchiveLogMetadata.TagCondition(from) },
        };
    }

    /// <summary>Returns the status and error code a request answers, a refusal's among them; a success has no code.</summary>
    private static async Task<(int Status, string? Code)> Outcome<T>(Func<Task<Response<T>>> request)
    {
        try
        {
            return ((await request()).GetRawResponse().Status, null);
        }
        catch (RequestFailedException exception) when (exception.Status is 400 or 404 or 409 or 412)
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
        var service = backend switch
        {
            AzureBackend.Azurite => CloudEmulators.AzureService(endpoint!, transport),
            AzureBackend.Live => AzureLiveAccount.Service(CloudEmulators.AzureOptions(transport)),
            _ => throw new ArgumentOutOfRangeException(nameof(backend)),
        };

        // The account owns the container from before its creation, so a creation which throws leaves none behind.
        var account = new Account(service, service.GetBlobContainerClient(FormattableString.Invariant($"{AzureLiveAccount.FidelityContainerPrefix}{Guid.NewGuid():N}")), transport);
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

    /// <summary>A container of its own on a backend, and the store over it, which the test deletes.</summary>
    private sealed class Account(BlobServiceClient service, BlobContainerClient container, TransportDouble transport) : IAsyncDisposable
    {
        public BlobServiceClient Service { get; } = service;

        public BlobContainerClient Container { get; } = container;

        public AzureArchiveLogStore Store { get; } = new(container, "json");

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(AzureLiveAccount.DeleteTimeout, TimeProvider.System);
                _ = await Container.DeleteIfExistsAsync(cancellationToken: timeout.Token);
            }
            finally
            {
                transport.Dispose();
            }
        }
    }
}
