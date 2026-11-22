using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Numerics;
using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Fuzz.Wal;

/// <summary>
/// The rules of wal.md §7, applied to bytes independently of <see cref="WalFormat"/> and the reader: the oracle of
/// every WAL target. Each rule is checked in the order a reader meets it, and a broken rule is named by the reason
/// code of the shared page and header vectors.
/// </summary>
internal static class WalRules
{
    public const int CrcLength = 4;
    public const int WordLength = 4;
    public const int FirstHeaderLength = WordLength + sizeof(ulong);
    public const int HeaderFieldsLength = 24;
    public const uint FirstOfOpenFlag = 1;

    private const int KindShift = 30;
    private const uint LengthMask = (1u << KindShift) - 1;

    public static ReadOnlySpan<byte> Magic => "CLFL"u8;

    /// <summary>
    /// Returns whether <paramref name="pageSize"/> is a page size the WAL supports: a power of two from 512 bytes to
    /// 64 KiB.
    /// </summary>
    public static bool IsPageSize(ulong pageSize) =>
        pageSize is >= WalOptions.MinimumPageSize and <= WalOptions.MaximumPageSize && BitOperations.IsPow2(pageSize);

    public static bool HasValidCrc(ReadOnlySpan<byte> page) =>
        page.Length >= CrcLength && BinaryPrimitives.ReadUInt32LittleEndian(page) == Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, page[CrcLength..]);

    public static void Seal(Span<byte> page) => BinaryPrimitives.WriteUInt32LittleEndian(page, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, page[CrcLength..]));

    /// <summary>
    /// Returns the page size the fixed fields of a header name, when they carry the magic, version 1, and a supported page
    /// size: the bytes a reader then reads as the header page.
    /// </summary>
    public static int? NamedPageSize(ReadOnlySpan<byte> fields)
    {
        if (fields.Length < HeaderFieldsLength
            || !fields.Slice(WalFormat.HeaderMagicOffset, Magic.Length).SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt32LittleEndian(fields[WalFormat.HeaderVersionOffset..]) != 1)
        {
            return null;
        }

        var pageSize = BinaryPrimitives.ReadUInt32LittleEndian(fields[WalFormat.HeaderPageSizeOffset..]);
        return IsPageSize(pageSize) ? (int)pageSize : null;
    }

    /// <summary>
    /// Returns the first header rule a page breaks, named by its reason code in the order wal.md §7 lists them, or the
    /// header's fields: at least 24 bytes, not all zero, the magic, version 1, a supported page size equal to the page's
    /// length, a matching CRC, a write window from 1 to <c>int.MaxValue</c>, no flag but bit 0, and zero from byte 24.
    /// </summary>
    public static HeaderVerdict Header(ReadOnlySpan<byte> page)
    {
        if (page.Length < HeaderFieldsLength)
        {
            return HeaderVerdict.Rejected("truncated");
        }

        // wal.md §7: a header's zero rule reads its 24 fixed bytes, which are all the reader holds when they name no page.
        if (!page[..HeaderFieldsLength].ContainsAnyExcept((byte)0))
        {
            return HeaderVerdict.Rejected("zero-page");
        }

        if (!page.Slice(WalFormat.HeaderMagicOffset, Magic.Length).SequenceEqual(Magic))
        {
            return HeaderVerdict.Rejected("bad-magic");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(page[WalFormat.HeaderVersionOffset..]) != 1)
        {
            return HeaderVerdict.Rejected("unsupported-version");
        }

        var pageSize = BinaryPrimitives.ReadUInt32LittleEndian(page[WalFormat.HeaderPageSizeOffset..]);
        if (!IsPageSize(pageSize))
        {
            return HeaderVerdict.Rejected("page-size-unsupported");
        }

        if (pageSize != (uint)page.Length)
        {
            return HeaderVerdict.Rejected("page-size-mismatch");
        }

        if (!HasValidCrc(page))
        {
            return HeaderVerdict.Rejected("crc-mismatch");
        }

        var window = BinaryPrimitives.ReadUInt32LittleEndian(page[WalFormat.HeaderWindowOffset..]);
        if (window == 0)
        {
            return HeaderVerdict.Rejected("window-zero");
        }

        if (window > int.MaxValue)
        {
            return HeaderVerdict.Rejected("window-out-of-range");
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(page[WalFormat.HeaderFlagsOffset..]);
        if ((flags & ~FirstOfOpenFlag) != 0)
        {
            return HeaderVerdict.Rejected("unknown-flag");
        }

        return page[HeaderFieldsLength..].ContainsAnyExcept((byte)0)
            ? HeaderVerdict.Rejected("nonzero-padding")
            : new HeaderVerdict(null, (int)pageSize, (int)window, (flags & FirstOfOpenFlag) != 0);
    }

    /// <summary>
    /// Classifies one data page: invalid (its CRC fails), a hole page, ill-formed (a frame rule broken, with the rule's
    /// reason), or a valid page with its frames.
    /// </summary>
    public static PageVerdict Classify(ReadOnlySpan<byte> page)
    {
        if (!HasValidCrc(page))
        {
            return new PageVerdict(PageKind.Invalid, page.ContainsAnyExcept((byte)0) ? "crc-mismatch" : "zero-page", []);
        }

        var holeWord = (uint)(page.Length - CrcLength - WordLength);
        if (BinaryPrimitives.ReadUInt32LittleEndian(page[CrcLength..]) == holeWord && !page[(CrcLength + WordLength)..].ContainsAnyExcept((byte)0))
        {
            return new PageVerdict(PageKind.Hole, null, []);
        }

        var frames = new List<Frame>();
        var reason = Frames(page, frames);
        return reason is null ? new PageVerdict(PageKind.Valid, null, frames) : new PageVerdict(PageKind.IllFormed, reason, []);
    }

    /// <summary>
    /// Walks a valid page's frames: a leading Continuation of 1 to P − 8 bytes; then Full frames which fit; then at
    /// most one First frame, which is last, carries at least one byte, fills the page, and has a total above its fragment
    /// and at most <c>int.MaxValue</c>; then a zero word, or fewer than four bytes, and zero to the page's end.
    /// </summary>
    /// <returns>The reason code of the rule the page breaks, or <see langword="null"/>.</returns>
    private static string? Frames(ReadOnlySpan<byte> page, List<Frame> frames)
    {
        var offset = CrcLength;
        var word = BinaryPrimitives.ReadUInt32LittleEndian(page[offset..]);
        if (Kind(word) == FrameKind.Continuation)
        {
            var length = (int)(word & LengthMask);
            if (length == 0)
            {
                return "continuation-empty";
            }

            if (length > page.Length - CrcLength - WordLength)
            {
                return "continuation-overlong";
            }

            frames.Add(new Frame(FrameKind.Continuation, offset, 0, page.Slice(offset + WordLength, length).ToArray()));
            offset += WordLength + length;
        }

        while (page.Length - offset >= WordLength)
        {
            word = BinaryPrimitives.ReadUInt32LittleEndian(page[offset..]);
            if (word == 0)
            {
                break;
            }

            var length = (int)(word & LengthMask);
            switch (Kind(word))
            {
                case FrameKind.Full:
                    if (length > page.Length - offset - WordLength)
                    {
                        return "frame-overrun";
                    }

                    frames.Add(new Frame(FrameKind.Full, offset, 0, page.Slice(offset + WordLength, length).ToArray()));
                    offset += WordLength + length;
                    continue;
                case FrameKind.Continuation:
                    return "continuation-not-leading";
                case FrameKind.End:
                    return length == page.Length - CrcLength - WordLength ? "hole-not-zero" : "end-with-length";
                case FrameKind.First:
                    if (length == 0)
                    {
                        return "first-empty";
                    }

                    if ((long)offset + FirstHeaderLength + length > page.Length)
                    {
                        return "frame-overrun";
                    }

                    if (offset + FirstHeaderLength + length < page.Length)
                    {
                        return "first-not-filling";
                    }

                    var total = BinaryPrimitives.ReadUInt64LittleEndian(page[(offset + WordLength)..]);
                    if (total <= (ulong)length)
                    {
                        return "first-total-not-above-fragment";
                    }

                    if (total > int.MaxValue)
                    {
                        return "first-total-out-of-range";
                    }

                    frames.Add(new Frame(FrameKind.First, offset, (int)total, page.Slice(offset + FirstHeaderLength, length).ToArray()));
                    return null;
                default:
                    throw new UnreachableException("A frame kind has two bits.");
            }
        }

        return page[offset..].ContainsAnyExcept((byte)0) ? "nonzero-after-end" : null;
    }

    private static FrameKind Kind(uint word) => (FrameKind)(word >> KindShift);
}

/// <summary>
/// A header page's verdict under wal.md §7: the reason code of the first rule it breaks, or its fields.
/// </summary>
internal sealed record HeaderVerdict(string? Reason, int PageSize, int Window, bool FirstOfOpen)
{
    public static HeaderVerdict Rejected(string reason) => new(reason, 0, 0, FirstOfOpen: false);
}

/// <summary>
/// A data page's class under wal.md §7.
/// </summary>
internal enum PageKind
{
    Invalid,
    Hole,
    IllFormed,
    Valid,
}

/// <summary>
/// One data page's class, the reason code of the rule an invalid or ill-formed page breaks, and a valid page's frames.
/// </summary>
internal sealed record PageVerdict(PageKind Kind, string? Reason, IReadOnlyList<Frame> Frames);

/// <summary>
/// One frame: its kind, its offset in its page, a First frame's total record length, and the bytes it carries.
/// </summary>
internal sealed record Frame(FrameKind Kind, int Offset, int Total, byte[] Bytes);
