using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs.Models;
using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Fuzz.Archive.Logs;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// <see cref="AzureArchiveLogStore"/> through the real SDK over the fake transport (archive-logs.md §7.3, §8.4, §9.1): the
/// metadata of <c>metadata.json</c> and <c>metadata-rejections.json</c> in both directions, and its round trip over
/// generated fields; snapshots of each write kind's requests, their conditions, their service version, and the metadata
/// it leaves; a read which the SDK resumes under its ETag, <c>Stale</c> when the blob changes or the checkpoint vanishes;
/// corruption of the metadata, the checkpoint, its MD5, and a block's answer; a second opener whose base differs;
/// refusals; and the strict parse of a listing.
/// </summary>
public sealed class AzureArchiveLogStoreTests : IDisposable
{
    private static readonly ArchiveLogLayout Layout = new(AzureArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 4, 100, 50, AzureArchiveLogMetadata.NameRule);

    private static readonly ArchiveLogName Name = new("journals/account-17", ArchiveLogNames.KeyHash("journal-1"u8));

    private static readonly LedgerSegmentId Segment = new(new LedgerId(9), Slot.Zero);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly FakeAzureBlobService _service = new();
    private readonly AzureArchiveLogStore _store;

    public AzureArchiveLogStoreTests() => _store = new AzureArchiveLogStore(_service.Client(), "json", Layout);

    public static TheoryData<string> MetadataCases => [.. SharedVectors.Cases("archive-logs", "metadata.json").Select(SharedVectors.Name)];

    public static TheoryData<string> MetadataRejections => [.. SharedVectors.Cases("archive-logs", "metadata-rejections.json").Select(SharedVectors.Name)];

    public void Dispose()
    {
        _service.Dispose();
        _leaks.Dispose();
    }

    /// <summary>
    /// A metadata vector reads as its fields, by the reader and by the fuzz target's independent reading of the rules, and
    /// each write kind's vector is the metadata the writer produces.
    /// </summary>
    [Theory]
    [MemberData(nameof(MetadataCases))]
    public void AMetadataVectorReadsAsItsFieldsAndIsWhatItsWriteProduces(string name)
    {
        var vector = SharedVectors.Cases("archive-logs", "metadata.json").Single(element => SharedVectors.Name(element) == name);
        var input = ArchiveLogMetadataTarget.InputOf(vector);
        if (!input.Wal)
        {
            Assert.Null(AzureArchiveLogMetadata.ReadCheckpoint(input.Metadata, input.KeyHash));
            Assert.Equal(Pairs(vector), Sorted(AzureArchiveLogMetadata.ForCheckpoint(SharedVectors.Text(vector, "format"), input.KeyHash)));
            return;
        }

        var opener = OpenerOf(vector);
        var md5 = vector.GetProperty("baseMd5").GetString();
        var expected = new AzureWalMetadata(SharedVectors.Text(vector, "format"), SharedVectors.UInt64(vector, "generation"), vector.GetProperty("checkpoint").GetString(), opener, md5, input.KeyHash);
        Assert.Equal(expected, Assert.IsType<AzureWalMetadata>(AzureArchiveLogMetadata.ReadWal(input.Metadata, input.Log, input.KeyHash, input.Tag).Value));
        Assert.Equal((null, new ArchiveLogMetadataFields(expected.Format, expected.Generation, expected.Checkpoint, opener, md5, input.KeyHash)), ArchiveLogMetadataRules.Read(input));
        if (vector.GetProperty("write").GetString() is not null)
        {
            Assert.Equal(Pairs(vector), Sorted(AzureArchiveLogMetadata.ForWal(expected.Format, input.Log, expected.Generation, opener, md5 is null ? null : Convert.FromHexString(md5), input.KeyHash)));
            Assert.Equal(ArchiveLogNames.KeyHash(Convert.FromHexString(SharedVectors.Text(vector, "key"))), input.KeyHash);
        }
    }

    /// <summary>The metadata a write produces reads back as its own fields, over generated logs, generations, openers, bases, and keys.</summary>
    [Fact]
    public void AWritesMetadataReadsBackAsItsFields() =>
        Gen.Select(Gen.Select(Gen.Int[1, 3], Gen.ULong[1, ulong.MaxValue], Gen.Int[0, 3]), Gen.ULong, Gen.Byte.Array[16], Gen.ULong).Sample(static @case =>
        {
            var ((segments, generation, kind), tombstone, md5, keyHash) = @case;
            var log = string.Join('/', Enumerable.Range(0, segments).Select(static segment => "s" + segment.ToString(CultureInfo.InvariantCulture)));
            var opener = generation == 1 && kind == 0 ? (ArchiveLogOpener?)null
                : kind == 1 ? new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, 1)
                : new ArchiveLogOpener(ArchiveLogOpenerKind.Delete, kind == 2 ? null : tombstone, 1);
            var baseMd5 = opener is null ? null : md5;
            var metadata = AzureArchiveLogMetadata.ForWal("json", log, generation, opener, baseMd5, keyHash);

            var read = Assert.IsType<AzureWalMetadata>(AzureArchiveLogMetadata.ReadWal(metadata, log, keyHash, AzureArchiveLogMetadata.TagOf(generation)).Value);

            var checkpoint = opener is null ? null : AzureArchiveLogMetadata.CheckpointName(log, generation);
            Assert.Equal(new AzureWalMetadata("json", generation, checkpoint, opener, baseMd5 is null ? null : Convert.ToHexStringLower(baseMd5), keyHash), read);
        });

    [Theory]
    [MemberData(nameof(MetadataRejections))]
    public void AMetadataRejectionIsRejectedByItsReason(string name)
    {
        var vector = SharedVectors.Cases("archive-logs", "metadata-rejections.json").Single(element => SharedVectors.Name(element) == name);
        var input = ArchiveLogMetadataTarget.InputOf(vector);
        var rejection = input.Wal
            ? Assert.IsType<InvalidDataException>(AzureArchiveLogMetadata.ReadWal(input.Metadata, input.Log, input.KeyHash, input.Tag).Value)
            : AzureArchiveLogMetadata.ReadCheckpoint(input.Metadata, input.KeyHash);

        Assert.Equal(SharedVectors.Text(vector, "reason"), DecodeRejection.ReasonOf(rejection!));
        Assert.Equal(SharedVectors.Text(vector, "reason"), ArchiveLogMetadataRules.Read(input).Reason);
    }

    /// <summary>A key's first append run: the append blob created once with its tag and metadata, then a block per unit.</summary>
    [Fact]
    public async Task AFirstAppendRunsRequests()
    {
        await WriteAsync(default, ArchiveCommand.Append(3), ArchiveCommand.Append(2));
        await Verify(Snapshot());
    }

    /// <summary>An opener: its checkpoint created once, then the write-ahead blob re-created under the starting generation's tag.</summary>
    [Fact]
    public async Task AnOpenersRequests()
    {
        var first = await WriteAsync(default, ArchiveCommand.Append(2));
        var replace = await WriteAsync(first, ArchiveCommand.Replace(3), ArchiveCommand.Append(1));
        await WriteAsync(replace, ArchiveCommand.Delete(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        await Verify(Snapshot());
    }

    /// <summary>An append run on a generation an opener created: each block at its offset under the generation's tag.</summary>
    [Fact]
    public async Task AnAppendRunsRequests()
    {
        var opener = await WriteAsync(default, ArchiveCommand.Delete(default));
        await WriteAsync(opener, ArchiveCommand.Append(4), ArchiveCommand.Append(1), ArchiveCommand.Append(3));
        await Verify(Snapshot());
    }

    /// <summary>A straggler's requests meet a newer generation and fail; one properties read decides them held.</summary>
    [Fact]
    public async Task AStragglersRequests()
    {
        var first = await WriteAsync(default, ArchiveCommand.Append(2));
        _ = await WriteAsync(first, ArchiveCommand.Replace(1));
        await WriteAsync(default, ArchiveCommand.Append(2));
        await WriteAsync(first, ArchiveCommand.Replace(1));
        await Verify(Snapshot());
    }

    /// <summary>
    /// orleans.md §5.13: stores under two prefixes of one container keep to their own: each names its blobs under its
    /// prefix, an opener's write-ahead blob names its checkpoint with it, each reads its own log of one name as its own
    /// writes, and each lists only its own generations; the prefix shortens the names it admits by its length.
    /// </summary>
    [Fact]
    public async Task StoresUnderTwoPrefixesOfOneContainerKeepToTheirOwn()
    {
        using var service = new FakeAzureBlobService();
        var ours = new AzureArchiveLogStore(service.Client(), "json", Layout, "a/");
        var theirs = new AzureArchiveLogStore(service.Client(), "json", Layout, "bb/");
        var write = new ArchiveWrite(Segment, default, [ArchiveCommand.Replace(3)]);
        Assert.IsType<ArchiveLogConditionOutcome.Held>((await new ArchiveLogWriter(ours, new FakeTimeProvider()).WriteAsync(Name, write, new ReadOnlySequence<byte>([1, 2, 3]), Token)).Value);
        Assert.IsType<ArchiveLogConditionOutcome.Held>((await new ArchiveLogWriter(theirs, new FakeTimeProvider()).WriteAsync(Name, new ArchiveWrite(Segment, default, [ArchiveCommand.Replace(2)]), new ReadOnlySequence<byte>([7, 8]), Token)).Value);

        Assert.All(service.Records, static record => Assert.True(record.Blob.StartsWith("a/", StringComparison.Ordinal) || record.Blob.StartsWith("bb/", StringComparison.Ordinal), record.Blob));
        Assert.Equal(AzureArchiveLogMetadata.CheckpointName(Name.Value, 1, "a/"), (await service.Client().GetBlobClient(AzureArchiveLogMetadata.WalName(Name.Value, "a/")).GetPropertiesAsync(cancellationToken: Token)).Value.Metadata[AzureArchiveLogMetadata.Checkpoint]);
        using (var read = Assert.IsType<ArchiveLogRead.Read>((await ours.ReadAsync(Name, Token)).Value))
        {
            Assert.Equal(3, read.Content.Length);
        }

        using (var read = Assert.IsType<ArchiveLogRead.Read>((await theirs.ReadAsync(Name, Token)).Value))
        {
            Assert.Equal(2, read.Content.Length);
        }

        Assert.Equal([(Name.Value, 1UL)], await ours.ListGenerationsAsync(continuation: null, Token).SelectMany(static page => page.Generations).Select(static generation => (generation.Log, generation.Generation)).ToListAsync(Token));
        Assert.Equal([(Name.Value, 1UL)], await theirs.ListGenerationsAsync(continuation: null, Token).SelectMany(static page => page.Generations).Select(static generation => (generation.Log, generation.Generation)).ToListAsync(Token));
        var longest = new string('k', AzureArchiveLogMetadata.MaxLogNameLength);
        Assert.True(new AzureArchiveLogStore(service.Client(), "json").AdmitsName(longest));
        Assert.False(ours.AdmitsName(longest));
        Assert.True(ours.AdmitsName(longest[2..]));
    }

    /// <summary>
    /// A read gets the base its write-ahead blob names and checks it against the MD5 the blob records; a checkpoint deleted
    /// under the read answers <c>Stale</c>; and a generation whose base is empty, as every <c>Delete</c>'s, reads no
    /// checkpoint at all.
    /// </summary>
    [Fact]
    public async Task AReadChecksItsBaseAndMeetsAVanishedCheckpointAsStale()
    {
        var opener = await WriteAsync(default, ArchiveCommand.Replace(3));
        var appended = await WriteAsync(opener, ArchiveCommand.Append(4), ArchiveCommand.Append(4), ArchiveCommand.Append(2));
        using (var read = Assert.IsType<ArchiveLogRead.Read>((await _store.ReadAsync(Name, Token)).Value))
        {
            Assert.Equal(new ArchiveLogPosition(1, 1, 10, 3), read.Position);
            Assert.Equal((13, 3), (read.Content.Length, read.BaseLength));
            Assert.True(read.HasBase);
        }

        _service.Remove(AzureArchiveLogMetadata.CheckpointName(Name.Value, 1));
        Assert.Same(ArchiveLogRead.Stale.Instance, (await _store.ReadAsync(Name, Token)).Value);

        _ = await WriteAsync(appended, ArchiveCommand.Delete(default));
        var reads = _service.Records.Count;
        using (var read = Assert.IsType<ArchiveLogRead.Read>((await _store.ReadAsync(Name, Token)).Value))
        {
            Assert.Equal(new ArchiveLogPosition(2, 1, 0, 0), read.Position);
        }

        Assert.DoesNotContain(_service.Records.Skip(reads), static record => record.Blob.StartsWith("checkpoints/", StringComparison.Ordinal));
    }

    /// <summary>
    /// A read whose body breaks mid-way is resumed by the SDK from where it broke under the response's ETag, and reads
    /// whole; when the blob gains a block before the resume, the resume fails its <c>If-Match</c>: <c>Stale</c>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReadWhichBreaksMidBodyResumesUnderItsETag(bool changes)
    {
        FakeAzureBlobService? service = null;
        var gets = 0;
        service = new FakeAzureBlobService(faults: request =>
        {
            if (request.Operation != FakeAzureOperation.GetBlob || !request.Blob.StartsWith("wal/", StringComparison.Ordinal))
            {
                return FakeAzureFault.None;
            }

            if (Interlocked.Increment(ref gets) == 2 && changes)
            {
                service!.AppendRaw("wal/" + Name.Value, [7]);
            }

            return gets == 1 ? FakeAzureFault.BreakMidBody : FakeAzureFault.None;
        });
        using (service)
        {
            var store = new AzureArchiveLogStore(service.Client(), "json", Layout);
            var write = new ArchiveWrite(Segment, default, [ArchiveCommand.Append(4), ArchiveCommand.Append(4)]);
            _ = await new ArchiveLogWriter(store, new FakeTimeProvider()).WriteAsync(Name, write, new ReadOnlySequence<byte>([1, 2, 3, 4, 5, 6, 7, 8]), Token);
            Interlocked.Exchange(ref gets, 0);

            var read = (await store.ReadAsync(Name, Token)).Value;

            Assert.Equal(1, service.FaultsFired().GetValueOrDefault(FakeAzureFault.BreakMidBody));
            if (changes)
            {
                Assert.Same(ArchiveLogRead.Stale.Instance, read);
                return;
            }

            using var whole = Assert.IsType<ArchiveLogRead.Read>(read);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, whole.Content.ToArray());
            Assert.Contains(service.Records, static record => record.Operation == FakeAzureOperation.GetBlob && record.Headers.ContainsKey("if-match"));
        }
    }

    /// <summary>A checkpoint whose bytes are not the MD5 its write-ahead blob records, or whose metadata names another key, is corrupt.</summary>
    [Fact]
    public async Task ACheckpointWhichDiffersFromItsRecordIsCorrupt()
    {
        _ = await WriteAsync(default, ArchiveCommand.Replace(3));
        var checkpoint = AzureArchiveLogMetadata.CheckpointName(Name.Value, 1);
        var metadata = _service.Peek(checkpoint)!.Value.Metadata;

        _service.SetMetadata(checkpoint, [.. metadata.Select(static pair => pair.Key == "durable_key" ? KeyValuePair.Create(pair.Key, "0000000000000000") : pair)]);
        await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await _store.ReadAsync(Name, Token));

        _service.Remove(checkpoint);
        _ = await _service.Client().GetBlobClient(checkpoint).UploadAsync(BinaryData.FromBytes([9, 9, 9]), new global::Azure.Storage.Blobs.Models.BlobUploadOptions { Metadata = metadata.ToDictionary() }, Token);
        await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await _store.ReadAsync(Name, Token));
    }

    /// <summary>
    /// A second opener from the same token whose base differs from the stored checkpoint, which no write from one token
    /// makes, is corrupt: its <c>BlobAlreadyExists</c> is checked by one properties read of the checkpoint's length and
    /// MD5; the same base again lands (archive-logs.md §3).
    /// </summary>
    [Fact]
    public async Task ASecondOpenerWhoseBaseDiffersIsCorrupt()
    {
        var replace = new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, 1);
        Assert.IsType<ArchiveLogWriteOutcome.Applied>((await _store.OpenAsync(Name, ArchiveLogPosition.Origin, replace, new ReadOnlySequence<byte>([1, 2, 3]), Token)).Value);

        Assert.IsType<ArchiveLogWriteOutcome.ConditionFailed>((await _store.OpenAsync(Name, ArchiveLogPosition.Origin, replace, new ReadOnlySequence<byte>([1, 2, 3]), Token)).Value);
        await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await _store.OpenAsync(Name, ArchiveLogPosition.Origin, replace, new ReadOnlySequence<byte>([1, 2, 4]), Token));
        await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await _store.OpenAsync(Name, ArchiveLogPosition.Origin, replace, new ReadOnlySequence<byte>([1, 2]), Token));
    }

    /// <summary>
    /// A block's answer at another offset than the run packed, or a log whose committed blocks exceed its bytes, which a
    /// misbehaving service gives, is corrupt.
    /// </summary>
    [Fact]
    public async Task AnAnswerAtAnotherOffsetOrMoreBlocksThanBytesIsCorrupt()
    {
        _ = await _store.AppendAsync(Name, ArchiveLogPosition.Origin, [new ReadOnlySequence<byte>(new byte[2])], Token);
        _service.AppendOffsetSkew = 1;
        await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await _store.AppendAsync(Name, new ArchiveLogPosition(1, 0, 2, 1), [new ReadOnlySequence<byte>(new byte[1])], Token));
        _service.AppendOffsetSkew = 0;

        _service.SetReportedBlocks("wal/" + Name.Value, 9);
        await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await _store.GetPropertiesAsync(Name, Token));
        await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await _store.ReadAsync(Name, Token));
    }

    /// <summary>A request Azure refuses, which no retry changes, as an append to a block blob, reaches the caller as a refusal.</summary>
    [Fact]
    public async Task ARequestAzureRefusesIsARefusal()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["durable-gen"] = AzureArchiveLogMetadata.TagOf(1) };
        _ = await _service.Client().GetBlobClient("wal/" + Name.Value).UploadAsync(BinaryData.FromBytes([1]), new global::Azure.Storage.Blobs.Models.BlobUploadOptions { Tags = tags }, Token);

        await Assert.ThrowsAsync<ArchiveLogRefusedException>(async () => await _store.AppendAsync(Name, new ArchiveLogPosition(1, 0, 1, 1), [new ReadOnlySequence<byte>(new byte[1])], Token));
        await Assert.ThrowsAsync<ArgumentException>(async () => await _store.AppendAsync(new ArchiveLogName("k/../x", 1), ArchiveLogPosition.Origin, [new ReadOnlySequence<byte>(new byte[1])], Token));
    }

    /// <summary>
    /// A request whose body changes after the store computed its <c>Content-MD5</c> is refused and lands nothing, an
    /// append's and a checkpoint's alike (JS-I5).
    /// </summary>
    [Fact]
    public async Task ABodyDamagedInTransitIsRefusedAndLandsNothing()
    {
        var damage = false;
        using var service = new FakeAzureBlobService(faults: request =>
            damage && request.Operation is FakeAzureOperation.AppendBlock or FakeAzureOperation.PutBlockBlob ? FakeAzureFault.Corrupt : FakeAzureFault.None);
        var store = new AzureArchiveLogStore(service.Client(), "json", Layout);
        Assert.IsType<ArchiveLogWriteOutcome.Applied>((await store.AppendAsync(Name, ArchiveLogPosition.Origin, [new ReadOnlySequence<byte>([1, 2])], Token)).Value);
        damage = true;

        var append = await Assert.ThrowsAsync<RequestFailedException>(async () => await store.AppendAsync(Name, new ArchiveLogPosition(1, 0, 2, 1), [new ReadOnlySequence<byte>([3, 4])], Token));
        var open = await Assert.ThrowsAsync<RequestFailedException>(async () => await store.OpenAsync(Name, new ArchiveLogPosition(1, 0, 2, 1), new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, 1), new ReadOnlySequence<byte>([5, 6]), Token));

        Assert.Equal(400, append.Status);
        Assert.Equal(400, open.Status);
        Assert.Equal([1, 2], service.Peek("wal/" + Name.Value)!.Value.Content);
        Assert.Null(service.Peek(AzureArchiveLogMetadata.CheckpointName(Name.Value, 2)));
    }

    [Fact]
    public async Task MetadataWhichBreaksItsFormatOrNamesAnotherKeyIsCorrupt()
    {
        _ = await WriteAsync(default, ArchiveCommand.Append(2));
        var wal = "wal/" + Name.Value;
        var metadata = _service.Peek(wal)!.Value.Metadata;

        _service.SetMetadata(wal, [.. metadata.Select(static pair => pair.Key == "durable_key" ? KeyValuePair.Create(pair.Key, "0000000000000000") : pair)]);
        await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await _store.ReadAsync(Name, Token));
        await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await _store.GetPropertiesAsync(Name, Token));

        _service.SetMetadata(wal, [.. metadata.Where(static pair => pair.Key != "wal_generation")]);
        await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await _store.ReadAsync(Name, Token));
    }

    /// <summary>A block whose answer contradicts the run's packing, as a token whose units the log does not have gives, is corrupt.</summary>
    [Fact]
    public async Task ABlockAnsweredAtAnotherCountIsCorrupt()
    {
        _ = await _store.AppendAsync(Name, ArchiveLogPosition.Origin, [new ReadOnlySequence<byte>(new byte[1]), new ReadOnlySequence<byte>(new byte[1])], Token);

        await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await _store.AppendAsync(Name, new ArchiveLogPosition(1, 0, 2, 1), [new ReadOnlySequence<byte>(new byte[1])], Token));
    }

    /// <summary>
    /// A listing names, in name order, each checkpoint whose name parses strictly, with the key hash it records, or none when
    /// its <c>durable_key</c> is absent or does not parse as 16 lowercase hexadecimal digits, and nothing else.
    /// </summary>
    [Fact]
    public async Task AListingParsesCheckpointNamesStrictly()
    {
        var opener = await WriteAsync(default, ArchiveCommand.Replace(1));
        _ = await WriteAsync(opener, ArchiveCommand.Replace(1));
        var container = _service.Client();
        foreach (var stray in new[] { "checkpoints/x/1", "checkpoints//00000000000000000001", "checkpoints/y/0000000000000000000a", "checkpoints/z/00000000000000000000", "wal/q" })
        {
            _ = await container.GetBlobClient(stray).UploadAsync(BinaryData.FromBytes([1]), Token);
        }

        // Checkpoints whose names parse, but whose key hash is absent or malformed, are listed with none.
        _ = await container.GetBlobClient("checkpoints/w/00000000000000000001").UploadAsync(BinaryData.FromBytes([1]), Token);
        foreach (var (log, key) in new[] { ("w0", "ABCDEF0123456789"), ("w1", "0123"), ("w2", "0123456789abcdefg"), ("w3", "-123456789abcdef") })
        {
            _ = await container.GetBlobClient($"checkpoints/{log}/00000000000000000001").UploadAsync(BinaryData.FromBytes([1]), new BlobUploadOptions { Metadata = new Dictionary<string, string> { ["durable_key"] = key } }, Token);
        }

        var listed = await _store.ListGenerationsAsync(continuation: null, Token).SelectMany(static page => page.Generations).ToListAsync(Token);

        Assert.Equal(
            [
                new ArchiveLogGeneration(Name.Value, 1, Name.KeyHash), new ArchiveLogGeneration(Name.Value, 2, Name.KeyHash),
                new ArchiveLogGeneration("w", 1, null), new ArchiveLogGeneration("w0", 1, null), new ArchiveLogGeneration("w1", 1, null),
                new ArchiveLogGeneration("w2", 1, null), new ArchiveLogGeneration("w3", 1, null),
            ],
            listed);
        await _store.DeleteGenerationAsync(Name.Value, 1, Token);
        await _store.DeleteGenerationAsync(Name.Value, 1, Token);
        Assert.Equal(new ArchiveLogGeneration(Name.Value, 2, Name.KeyHash), (await _store.ListGenerationsAsync(continuation: null, Token).SelectMany(static page => page.Generations).ToListAsync(Token))[0]);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ArchiveLogOpener? OpenerOf(JsonElement vector) => vector.GetProperty("kind").GetString() switch
    {
        null => null,
        "replace" => new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, SharedVectors.UInt64(vector, "base_rows")),
        _ => new ArchiveLogOpener(ArchiveLogOpenerKind.Delete, vector.GetProperty("tombstone").GetString() is { } tomb ? ulong.Parse(tomb, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) : null, SharedVectors.UInt64(vector, "base_rows")),
    };

    private static List<KeyValuePair<string, string>> Pairs(JsonElement vector) =>
        [.. vector.GetProperty("metadata").EnumerateArray().Select(static pair => KeyValuePair.Create(pair[0].GetString()!, pair[1].GetString()!)).OrderBy(static pair => pair.Key, StringComparer.Ordinal)];

    private static List<KeyValuePair<string, string>> Sorted(Dictionary<string, string> metadata) =>
        [.. metadata.OrderBy(static pair => pair.Key, StringComparer.Ordinal)];

    /// <summary>Writes a key's next write through the writer, its content distinct bytes; returns its token after.</summary>
    private async Task<RowToken> WriteAsync(RowToken token, params ArchiveCommand[] commands)
    {
        var write = new ArchiveWrite(Segment, token, [.. commands]);
        var content = Enumerable.Range(0, commands.Sum(static command => command.ContentLength)).Select(static index => (byte)(index + 1)).ToArray();
        var verdict = await new ArchiveLogWriter(_store, new FakeTimeProvider()).WriteAsync(Name, write, new ReadOnlySequence<byte>(content), Token);
        Assert.IsType<ArchiveLogConditionOutcome.Held>(verdict.Value);
        var plan = new ArchiveLogPlan(Layout, ArchiveLogTokens.PositionOf(token));
        foreach (var command in commands)
        {
            plan.Add(command);
        }

        return plan.End();
    }

    /// <summary>The requests the service served, each with its conditions, then every blob's metadata and tags.</summary>
    private string Snapshot()
    {
        var text = new StringBuilder();
        foreach (var record in _service.Records)
        {
            _ = text.Append(CultureInfo.InvariantCulture, $"{record.Operation} {record.Blob} → {record.Status}\n");
            foreach (var (header, value) in record.Headers)
            {
                _ = text.Append(CultureInfo.InvariantCulture, $"    {header}: {value}\n");
            }
        }

        foreach (var blob in new[] { "wal/" + Name.Value }.Concat(Enumerable.Range(1, 3).Select(generation => AzureArchiveLogMetadata.CheckpointName(Name.Value, (ulong)generation))))
        {
            if (_service.Peek(blob) is { } stored)
            {
                _ = text.Append(CultureInfo.InvariantCulture, $"{blob}: {stored.Content.Length} bytes\n");
                foreach (var (key, value) in stored.Metadata.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    _ = text.Append(CultureInfo.InvariantCulture, $"    metadata {key}: {value}\n");
                }

                foreach (var (key, value) in stored.Tags)
                {
                    _ = text.Append(CultureInfo.InvariantCulture, $"    tag {key}: {value}\n");
                }
            }
        }

        return text.ToString();
    }
}
