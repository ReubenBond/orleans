using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Fuzz.Archive.Logs;

/// <summary>
/// Fuzzes the chunk layout's chunk check, <c>ArchiveLogChunk.Check</c> (archive-logs.md §7.4), against an independent
/// reading of its rule: at least one byte of content, followed by its CRC-32C in 4 little-endian bytes, the first rule
/// broken naming the rejection, <c>truncated</c> or <c>bad-crc</c>; an accepted chunk is the encoding of its content.
/// </summary>
public sealed class ArchiveLogChunkTarget : FuzzTarget
{
    private static readonly Lazy<HashSet<string>> Reasons = new(static () =>
        [.. GoldenVectors.Find().Document("archive-logs", "chunks.json").GetProperty("rejections").EnumerateArray().Select(static vector => GoldenVectors.Text(vector, "reason"))]);

    /// <inheritdoc/>
    public override string Name => "archive-log-chunk";

    /// <inheritdoc/>
    public override int MaxLength => 256;

    /// <inheritdoc/>
    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        var document = vectors.Document("archive-logs", "chunks.json");
        return [.. document.GetProperty("cases").EnumerateArray().Concat(document.GetProperty("rejections").EnumerateArray())
            .Select(static vector => GoldenVectors.Hex(vector))];
    }

    /// <inheritdoc/>
    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var bytes = input.ToArray();
        var rejection = ArchiveLogChunk.Check(new ReadOnlySequence<byte>(bytes));
        var reason = input.Length <= 4 ? "truncated"
            : Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, input[..^4]) != BinaryPrimitives.ReadUInt32LittleEndian(input[^4..]) ? "bad-crc"
            : null;
        if (rejection is not null)
        {
            var actual = DecodeRejection.ReasonOf(rejection);
            Require(actual == reason, $"rejected the chunk as '{actual}' where the rule gives '{reason ?? "valid"}'.");
            Require(actual is not null && Reasons.Value.Contains(actual), $"rejected the chunk as '{actual}', which no chunk rejection vector names.");
            return FuzzOutcome.Rejected;
        }

        Require(reason is null, $"accepted a chunk which the rule rejects as '{reason}'.");
        using var writer = new ArcBufferWriter();
        ArchiveLogChunk.Encode(writer, new ReadOnlySequence<byte>(bytes, 0, bytes.Length - ArchiveLogChunk.TrailerLength));
        using var encoded = writer.ConsumeSlice(writer.Length);
        Require(encoded.ToArray().AsSpan().SequenceEqual(input), "accepted a chunk which is not the encoding of its content.");
        return FuzzOutcome.Accepted;
    }
}
