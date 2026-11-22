using System.Buffers.Binary;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Fuzz.Wal;

/// <summary>
/// The header page of a WAL file (wal.md §7), read as the WAL opens a file: the fixed fields give the page size, and
/// the first page of that size is the header. The input is the start of a file.
/// </summary>
public sealed class WalHeaderTarget : FuzzTarget
{
    /// <summary>
    /// The reasons of <c>tests/shared-vectors/wal/header-rejections.json</c>.
    /// </summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () => GoldenVectors.Find().Reasons("wal", "header-rejections.json"));

    public override string Name => "wal-header";

    public override int MaxLength => 4096;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return SeedsOf(vectors);
    }

    private static IEnumerable<byte[]> SeedsOf(GoldenVectors vectors)
    {
        foreach (var file in new[] { "headers.json", "header-rejections.json" })
        {
            foreach (var vector in vectors.Cases("wal", file))
            {
                yield return GoldenVectors.Expand(GoldenVectors.Hex(vector), vector.GetProperty("pageSize").GetInt32());
            }
        }

        var example = vectors.Document("wal", "example.json");
        yield return GoldenVectors.Expand(GoldenVectors.Hex(example, "headerHex"), example.GetProperty("pageSize").GetInt32());
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        // The reader reads the fixed fields, then as much of the page they name as the file holds, or decodes the fields
        // alone when they name none.
        var fields = input[..Math.Min(input.Length, WalFormat.HeaderFieldsLength)];
        var named = WalRules.NamedPageSize(fields);
        Require(WalFormat.NamedPageSize(fields) == named, "read a page size from the fixed fields which the format's rules do not give.");
        var page = named is { } size ? input[..Math.Min(size, input.Length)] : fields;
        var bound = MeasureAllocations(page.Length);
        var decoding = WalFormat.DecodeHeader(page);
        bound.Check("WalFormat.DecodeHeader");
        var expected = WalRules.Header(page);
        Debug.Assert(decoding.Value is not null, "conventions.md §15: a decoder answers a union which holds a case.");
        return decoding switch
        {
            InvalidDataException rejection => Rejected(rejection, expected),
            WalFileHeader decoded => Accepted(decoded, expected, page),
            null => throw new UnreachableException(),
        };
    }

    private FuzzOutcome Rejected(InvalidDataException rejection, HeaderVerdict expected)
    {
        var reason = DecodeRejection.ReasonOf(rejection);
        Require(reason is not null && Reasons.Value.Contains(reason), $"rejected a header for '{reason}', which the header vectors do not name.");
        Require(reason == expected.Reason, $"rejected a header for '{reason}' where the format's rules give '{expected.Reason ?? "valid"}'.");
        return FuzzOutcome.Rejected;
    }

    private FuzzOutcome Accepted(WalFileHeader decoded, HeaderVerdict expected, ReadOnlySpan<byte> page)
    {
        Require(expected.Reason is null, $"accepted a header which breaks '{expected.Reason}'.");
        Require(
            (expected.PageSize, expected.Window, expected.FirstOfOpen) == (decoded.PageSize, decoded.WriteWindowPages, decoded.FirstOfOpen),
            "accepted a header whose fields the format's rules do not give.");
        var encoded = new byte[page.Length];
        WalFormat.WriteHeader(encoded, decoded.WriteWindowPages, decoded.FirstOfOpen);
        Require(page.SequenceEqual(encoded), "accepted a header page which its fields do not encode to.");
        return FuzzOutcome.Accepted;
    }

    protected override byte[]? Seal(ReadOnlySpan<byte> input)
    {
        if (input.Length < WalFormat.HeaderFieldsLength)
        {
            return null;
        }

        // The page the header names, when the input holds it; otherwise the whole input.
        var named = BinaryPrimitives.ReadUInt32LittleEndian(input[WalFormat.HeaderPageSizeOffset..]);
        var size = named is >= WalFormat.HeaderFieldsLength && named <= (uint)input.Length ? (int)named : input.Length;
        var bytes = input.ToArray();
        WalRules.Seal(bytes.AsSpan(0, size));
        return bytes;
    }
}
