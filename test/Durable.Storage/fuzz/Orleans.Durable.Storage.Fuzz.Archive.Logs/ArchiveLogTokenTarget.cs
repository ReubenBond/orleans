using System.Diagnostics;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Archive.Logs;

namespace Orleans.Durable.Storage.Fuzz.Archive.Logs;

/// <summary>
/// Archive logs' token codec (archive-logs.md §7.2): rejected with the reason <see cref="ArchiveLogTokenRules"/> gives, one
/// of <c>tokens.json</c>'s rejection reasons, or accepted as the position the rules give, which encodes back to the same
/// bytes, since each encoding is canonical.
/// </summary>
public sealed class ArchiveLogTokenTarget : FuzzTarget
{
    private static readonly Lazy<HashSet<string>> Reasons = new(static () =>
        [.. GoldenVectors.Find().Document("archive-logs", "tokens.json").GetProperty("rejections").EnumerateArray().Select(static vector => GoldenVectors.Text(vector, "reason"))]);

    public override string Name => "archive-log-token";

    public override int MaxLength => 64;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        var document = vectors.Document("archive-logs", "tokens.json");
        return [.. document.GetProperty("cases").EnumerateArray().Concat(document.GetProperty("rejections").EnumerateArray())
            .Select(static vector => GoldenVectors.Hex(vector))];
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var bound = MeasureAllocations(input.Length);
        var decoded = ArchiveLogTokens.Decode(input);
        bound.Check("ArchiveLogTokens.Decode");
        var (reason, expected) = ArchiveLogTokenRules.Read(input);
        switch (decoded.Value)
        {
            case InvalidDataException rejection:
                var actual = DecodeRejection.ReasonOf(rejection);
                Require(actual == reason, $"rejected the token as '{actual}' where the rules give '{reason ?? "valid"}'.");
                Require(actual is not null && Reasons.Value.Contains(actual), $"rejected the token as '{actual}', which no token rejection vector names.");
                return FuzzOutcome.Rejected;
            case ArchiveLogPosition position:
                Require(reason is null, $"accepted a token which the rules reject as '{reason}'.");
                Require((position.Generation, position.BaseRows, position.Length, position.Units) == expected, $"decoded {position} where the rules give {expected}.");
                Require(ArchiveLogTokens.Encode(position).AsSpan().SequenceEqual(input), $"accepted a token which does not encode back to its bytes: {position}.");
                return FuzzOutcome.Accepted;
            case null:
            default:
                throw new UnreachableException();
        }
    }
}
