using System.Buffers.Binary;
using System.Numerics;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Fuzz.Wal;

/// <summary>
/// One data page of a WAL file (wal.md §7), classified as a reader does: a valid hole page, a valid page of
/// well-formed frames, or a page breaking a rule, named by the shared page vectors' reason for it. The format's rules,
/// applied to the page independently, give the expected class and reason. The input is the page, zero-filled to the
/// next page size the WAL supports.
/// </summary>
public sealed class WalPageTarget : FuzzTarget
{
    /// <summary>
    /// The reasons of <c>test/Durable.Storage/shared-vectors/wal/page-rejections.json</c>.
    /// </summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () => GoldenVectors.Find().Reasons("wal", "page-rejections.json"));

    public override string Name => "wal-page";

    public override int MaxLength => 4096;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return SeedsOf(vectors);
    }

    private static IEnumerable<byte[]> SeedsOf(GoldenVectors vectors)
    {
        foreach (var file in new[] { "pages.json", "page-rejections.json" })
        {
            foreach (var vector in vectors.Cases("wal", file))
            {
                yield return GoldenVectors.Expand(GoldenVectors.Hex(vector), vector.GetProperty("pageSize").GetInt32());
            }
        }

        var example = vectors.Document("wal", "example.json");
        foreach (var page in example.GetProperty("pages").EnumerateArray())
        {
            yield return GoldenVectors.Expand(GoldenVectors.Hex(page), example.GetProperty("pageSize").GetInt32());
        }
    }

    /// <summary>
    /// Returns the page an input stands for: its bytes, zero-filled to the smallest page size the WAL supports which holds
    /// them, or its first 64 KiB.
    /// </summary>
    public static byte[] ToPage(ReadOnlySpan<byte> input)
    {
        var size = (int)Math.Clamp(BitOperations.RoundUpToPowerOf2((uint)Math.Max(input.Length, 1)), WalOptions.MinimumPageSize, WalOptions.MaximumPageSize);
        var page = new byte[size];
        input[..Math.Min(input.Length, size)].CopyTo(page);
        return page;
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var page = ToPage(input);
        var bound = MeasureAllocations(page.Length);
        var rejection = WalFormat.ValidatePage(page);
        bound.Check("WalFormat.ValidatePage");
        var verdict = WalRules.Classify(page);
        var problem = rejection is null ? null : DecodeRejection.ReasonOf(rejection);
        Require(
            problem == verdict.Reason,
            $"classified a page as '{problem ?? "valid"}' where the format's rules give '{verdict.Reason ?? "valid"}'.");
        if (problem is not null)
        {
            Require(Reasons.Value.Contains(problem), $"rejected a page for '{problem}', which the page vectors do not name.");
            return FuzzOutcome.Rejected;
        }

        // An accepted page is exactly its frames, or the hole page, laid out as the writer lays a page out.
        var encoded = new byte[page.Length];
        if (verdict.Kind == PageKind.Hole)
        {
            WalFormat.WriteHolePage(encoded);
        }
        else
        {
            Encode(verdict.Frames, encoded);
        }

        Require(page.AsSpan().SequenceEqual(encoded), "accepted a page which its frames do not encode to.");
        return FuzzOutcome.Accepted;
    }

    protected override byte[]? Seal(ReadOnlySpan<byte> input)
    {
        var page = ToPage(input);
        WalRules.Seal(page);
        return page;
    }

    private static void Encode(IReadOnlyList<Frame> frames, Span<byte> page)
    {
        var offset = WalRules.CrcLength;
        foreach (var frame in frames)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(page[offset..], WalFormat.EncodeFrame(frame.Kind, frame.Bytes.Length));
            offset += WalRules.WordLength;
            if (frame.Kind == FrameKind.First)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(page[offset..], (ulong)frame.Total);
                offset += sizeof(ulong);
            }

            frame.Bytes.CopyTo(page[offset..]);
            offset += frame.Bytes.Length;
        }

        WalRules.Seal(page);
    }
}
