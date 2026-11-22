using System.Buffers;
using CsCheck;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// The chunk layout's objects (archive-logs.md §7.4): the base header, <c>CLAB</c> 1, by <c>chunk-bases.json</c> in both
/// directions, each malformed base rejected by its reason, and every opener and content round-tripping; and chunks, by
/// <c>chunks.json</c>, each with its CRC-32C trailer, and each malformed chunk rejected by its reason.
/// </summary>
[NoPageLeakCheck("Bases are byte arrays; the tests rent no pages.")]
public sealed class ArchiveLogChunkBaseTests
{
    public static TheoryData<string> Cases => [.. SharedVectors.Cases("archive-logs", "chunk-bases.json").Select(SharedVectors.Name)];

    public static TheoryData<string> Rejections => [.. SharedVectors.Cases("archive-logs", "chunk-bases.json", "rejections").Select(SharedVectors.Name)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void ABaseVectorEncodesFromItsFieldsAndDecodesBack(string name)
    {
        var vector = SharedVectors.Cases("archive-logs", "chunk-bases.json").Single(element => SharedVectors.Name(element) == name);
        var keyHash = Convert.ToUInt64(SharedVectors.Text(vector, "keyHash"), 16);
        var content = Convert.FromHexString(SharedVectors.Text(vector, "content"));
        var tombstone = vector.GetProperty("tombstone").GetString() is { } tomb ? Convert.ToUInt64(tomb, 16) : (ulong?)null;
        ArchiveLogOpener? opener = SharedVectors.Text(vector, "opener") switch
        {
            "none" => null,
            "Replace" => new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, SharedVectors.UInt64(vector, "rows")),
            "Delete" => new ArchiveLogOpener(ArchiveLogOpenerKind.Delete, tombstone, SharedVectors.UInt64(vector, "rows")),
            var other => throw new InvalidDataException($"A vector names the opener '{other}'."),
        };

        var encoded = ArchiveLogChunkBase.Encode(keyHash, opener, new ReadOnlySequence<byte>(content));
        SharedVectors.AssertBytes(vector, encoded);
        var header = Assert.IsType<ArchiveLogChunkBase>(ArchiveLogChunkBase.Decode(new ReadOnlySequence<byte>(encoded)).Value);
        Assert.Equal((keyHash, opener, content.LongLength), (header.KeyHash, header.Opener, header.ContentLength));
        Assert.Equal(opener is null, ArchiveLogChunkBase.RecordsNoOpener(encoded));
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void AMalformedBaseIsRejectedByItsReason(string name)
    {
        var vector = SharedVectors.Cases("archive-logs", "chunk-bases.json", "rejections").Single(element => SharedVectors.Name(element) == name);

        var rejection = Assert.IsType<InvalidDataException>(ArchiveLogChunkBase.Decode(new ReadOnlySequence<byte>(SharedVectors.Hex(vector))).Value);
        Assert.Equal(SharedVectors.Text(vector, "reason"), DecodeRejection.ReasonOf(rejection));
    }

    /// <summary>Every opener, key hash, and content encodes to a base which decodes back to them, of the header's length more.</summary>
    [Fact]
    public void EveryBaseRoundTrips() =>
        Gen.Select(Gen.ULong, Gen.Int[0, 3], Gen.ULong[1, ArchiveLogOpener.MaxRows], Gen.Byte.Array[0, 64]).Sample(static fields =>
        {
            var (keyHash, kind, rows, content) = fields;
            ArchiveLogOpener? opener = kind switch
            {
                0 => null,
                1 => new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, rows),
                2 => new ArchiveLogOpener(ArchiveLogOpenerKind.Delete, null, rows),
                _ => new ArchiveLogOpener(ArchiveLogOpenerKind.Delete, keyHash ^ rows, rows),
            };
            content = opener is null ? [] : content;
            var encoded = ArchiveLogChunkBase.Encode(keyHash, opener, new ReadOnlySequence<byte>(content));

            Assert.Equal(ArchiveLogChunkBase.HeaderLength + content.Length, encoded.Length);
            var header = Assert.IsType<ArchiveLogChunkBase>(ArchiveLogChunkBase.Decode(new ReadOnlySequence<byte>(encoded)).Value);
            Assert.Equal((keyHash, opener, content.LongLength), (header.KeyHash, header.Opener, header.ContentLength));
        });

    public static TheoryData<string> ChunkCases => [.. SharedVectors.Cases("archive-logs", "chunks.json").Select(SharedVectors.Name)];

    public static TheoryData<string> ChunkRejections => [.. SharedVectors.Cases("archive-logs", "chunks.json", "rejections").Select(SharedVectors.Name)];

    /// <summary>A chunk vector is its content followed by the content's CRC-32C, which the check accepts.</summary>
    [Theory]
    [MemberData(nameof(ChunkCases))]
    public void AChunkVectorEncodesFromItsContentAndChecks(string name)
    {
        var vector = SharedVectors.Cases("archive-logs", "chunks.json").Single(element => SharedVectors.Name(element) == name);
        using var writer = new Buffers.ArcBufferWriter();
        ArchiveLogChunk.Encode(writer, new ReadOnlySequence<byte>(Convert.FromHexString(SharedVectors.Text(vector, "content"))));
        using var encoded = writer.ConsumeSlice(writer.Length);

        SharedVectors.AssertBytes(vector, encoded.ToArray());
        Assert.Null(ArchiveLogChunk.Check(new ReadOnlySequence<byte>(SharedVectors.Hex(vector))));
    }

    [Theory]
    [MemberData(nameof(ChunkRejections))]
    public void AMalformedChunkIsRejectedByItsReason(string name)
    {
        var vector = SharedVectors.Cases("archive-logs", "chunks.json", "rejections").Single(element => SharedVectors.Name(element) == name);

        var rejection = Assert.IsType<InvalidDataException>(ArchiveLogChunk.Check(new ReadOnlySequence<byte>(SharedVectors.Hex(vector))));
        Assert.Equal(SharedVectors.Text(vector, "reason"), DecodeRejection.ReasonOf(rejection));
    }

    [Fact]
    public void AGenerationNoOpenerCreatedHasNoContent() =>
        Assert.Throws<ArgumentException>(() => ArchiveLogChunkBase.Encode(1, null, new ReadOnlySequence<byte>([1])));
}
