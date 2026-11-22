using System.Buffers;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Archive.Logs;

namespace Orleans.Durable.Storage.Fuzz.Archive.Logs;

/// <summary>
/// Fuzzes the chunk layout's base decoder, <c>ArchiveLogChunkBase.Decode</c> (archive-logs.md §7.4), against
/// <see cref="ArchiveLogChunkBaseRules"/>: it rejects exactly the bases the rules reject, by the same reason, which a
/// rejection vector names, and accepts the rest with the rules' fields, each encoding back to its bytes.
/// </summary>
public sealed class ArchiveLogChunkBaseTarget : FuzzTarget
{
    private static readonly Lazy<HashSet<string>> Reasons = new(static () =>
        [.. GoldenVectors.Find().Document("archive-logs", "chunk-bases.json").GetProperty("rejections").EnumerateArray().Select(static vector => GoldenVectors.Text(vector, "reason"))]);

    /// <inheritdoc/>
    public override string Name => "archive-log-chunk-base";

    /// <inheritdoc/>
    public override int MaxLength => 256;

    /// <inheritdoc/>
    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        var document = vectors.Document("archive-logs", "chunk-bases.json");
        return [.. document.GetProperty("cases").EnumerateArray().Concat(document.GetProperty("rejections").EnumerateArray())
            .Select(static vector => GoldenVectors.Hex(vector))];
    }

    /// <inheritdoc/>
    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var bytes = input.ToArray();
        var decoded = ArchiveLogChunkBase.Decode(new ReadOnlySequence<byte>(bytes));
        var (reason, expected) = ArchiveLogChunkBaseRules.Read(input);
        switch (decoded.Value)
        {
            case InvalidDataException rejection:
                var actual = DecodeRejection.ReasonOf(rejection);
                Require(actual == reason, $"rejected the base as '{actual}' where the rules give '{reason ?? "valid"}'.");
                Require(actual is not null && Reasons.Value.Contains(actual), $"rejected the base as '{actual}', which no base rejection vector names.");
                return FuzzOutcome.Rejected;
            case ArchiveLogChunkBase header:
                Require(reason is null, $"accepted a base which the rules reject as '{reason}'.");
                var kind = header.Opener is not { } opener ? 0 : opener.Kind == ArchiveLogOpenerKind.Replace ? 1 : 2;
                var fields = (header.KeyHash, kind, header.Opener?.Tombstone, header.Opener?.Rows ?? 0, header.ContentLength);
                Require(fields == expected, $"decoded {fields} where the rules give {expected}.");
                var encoded = ArchiveLogChunkBase.Encode(header.KeyHash, header.Opener, new ReadOnlySequence<byte>(bytes, ArchiveLogChunkBase.HeaderLength, bytes.Length - ArchiveLogChunkBase.HeaderLength));
                Require(encoded.AsSpan().SequenceEqual(input), "accepted a base which does not encode back to its bytes.");
                return FuzzOutcome.Accepted;
            case null:
            default:
                throw new UnreachableException();
        }
    }
}
