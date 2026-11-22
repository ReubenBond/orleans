using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Numerics;


namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// Defines the version 1 file layout.
/// </summary>
/// <remarks>
/// <para>
/// Every page of P bytes holds the CRC-32C of bytes 4 through P - 1 in bytes 0-3. Page 0 of a file is the
/// header; data page i starts at file offset <c>P * (1 + i)</c>. Integers are unsigned little-endian.
/// </para>
/// <para>
/// A data page holds frames back to back from offset 4, then zero. Each frame starts with a 32-bit word: bits
/// 31..30 are the kind and bits 29..0 the fragment length. A Full frame holds a whole record. A First frame adds an
/// 8-byte total record length and fills its page; Continuation frames follow at offset 4 of each following page,
/// and a Continuation which does not finish its record fills its page. A zero word, or fewer than 4 remaining
/// bytes, ends the page's frames.
/// </para>
/// <para>
/// A hole page holds one word of kind End with length P - 8, then zero. Replay writes it over a page which a crash
/// left invalid.
/// </para>
/// </remarks>
internal static class WalFormat
{
    public const uint Version = 1;

    public const int CrcLength = sizeof(uint);

    public const int FrameWordLength = sizeof(uint);

    public const int FirstFrameHeaderLength = FrameWordLength + sizeof(ulong);

    public const int HeaderFieldsLength = 24;

    public const int HeaderMagicOffset = 4;

    public const int HeaderVersionOffset = 8;

    public const int HeaderPageSizeOffset = 12;

    public const int HeaderWindowOffset = 16;

    public const int HeaderFlagsOffset = 20;

    public const uint FirstOfOpenFlag = 1;

    public const string FileExtension = ".clfl";

    /// <summary>
    /// The name of the file whose exclusive handle owns a file-system directory.
    /// </summary>
    public const string LockFileName = "lock";

    /// <summary>
    /// The extension of a file written under a temporary name before its name is published.
    /// </summary>
    public const string TemporaryExtension = ".tmp";

    private const int KindShift = 30;

    private const uint LengthMask = (1u << KindShift) - 1;

    public static ReadOnlySpan<byte> Magic => "CLFL"u8;

    public static uint EncodeFrame(FrameKind kind, int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)length, LengthMask);
        return ((uint)kind << KindShift) | (uint)length;
    }

    public static FrameKind GetKind(uint word) => (FrameKind)(word >> KindShift);

    public static int GetLength(uint word) => (int)(word & LengthMask);

    public static long DataPageOffset(int pageSize, long page) => checked(pageSize * (1 + page));

    /// <summary>
    /// Gets the number of data pages a file of <paramref name="fileLength"/> bytes reaches, counting a partial last
    /// page.
    /// </summary>
    public static long DataPageCount(long fileLength, int pageSize) => Math.Max(0, (fileLength - 1) / pageSize);

    public static string GetFileName(ulong file) =>
        string.Create(CultureInfo.InvariantCulture, $"{file:D20}{FileExtension}");

    public static bool TryParseFileName(string fileName, out ulong file)
    {
        file = 0;
        return fileName.Length == 20 + FileExtension.Length
            && fileName.EndsWith(FileExtension, StringComparison.Ordinal)
            && ulong.TryParse(fileName.AsSpan(0, 20), NumberStyles.None, CultureInfo.InvariantCulture, out file);
    }

    /// <summary>
    /// Rejects a named-file name which is not a simple file name, or which a log file, a temporary file, or the lock file uses.
    /// </summary>
    public static void ValidateNamedFileName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.AsSpan().IndexOfAny(['/', '\\', ':']) >= 0
            || name is "." or ".." or LockFileName
            || name.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(TemporaryExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{name}' is not a valid named-file name.", nameof(name));
        }
    }

    /// <summary>
    /// Fills <paramref name="page"/>, which is exactly one page, with a file header.
    /// </summary>
    public static void WriteHeader(Span<byte> page, int writeWindowPages, bool firstOfOpen)
    {
        page.Clear();
        Magic.CopyTo(page[HeaderMagicOffset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(page[HeaderVersionOffset..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(page[HeaderPageSizeOffset..], (uint)page.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(page[HeaderWindowOffset..], (uint)writeWindowPages);
        BinaryPrimitives.WriteUInt32LittleEndian(page[HeaderFlagsOffset..], firstOfOpen ? FirstOfOpenFlag : 0);
        SealPage(page);
    }

    /// <summary>
    /// Returns the page size the fixed header fields name, when they carry the magic, version 1, and a supported page
    /// size, so a reader knows how many bytes the header page holds; otherwise <see langword="null"/>, when
    /// <see cref="DecodeHeader"/> of the fields names the rule they break.
    /// </summary>
    public static int? NamedPageSize(ReadOnlySpan<byte> fields)
    {
        if (fields.Length < HeaderFieldsLength
            || !fields.Slice(HeaderMagicOffset, Magic.Length).SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt32LittleEndian(fields[HeaderVersionOffset..]) != Version)
        {
            return null;
        }

        var pageSize = BinaryPrimitives.ReadUInt32LittleEndian(fields[HeaderPageSizeOffset..]);
        return IsPageSize(pageSize) ? (int)pageSize : null;
    }

    /// <summary>
    /// Decodes a header page: its fields, or the rejection naming the first rule it breaks (wal.md §7). The WAL classifies
    /// <c>unsupported-version</c> as <see cref="WalUnsupportedFormatException"/> and every other rejection as damage
    /// (WAL-I3).
    /// </summary>
    /// <param name="page">The page the header's fields name, or the bytes read when they name none.</param>
    public static WalHeaderDecoding DecodeHeader(ReadOnlySpan<byte> page)
    {
        if (page.Length < HeaderFieldsLength)
        {
            return Rejection("truncated");
        }

        // The reader decodes the fixed fields alone when they name no page size, so the zero rule reads only those.
        if (!page[..HeaderFieldsLength].ContainsAnyExcept((byte)0))
        {
            return Rejection("zero-page");
        }

        if (!page.Slice(HeaderMagicOffset, Magic.Length).SequenceEqual(Magic))
        {
            return Rejection("bad-magic");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(page[HeaderVersionOffset..]);
        if (version != Version)
        {
            return DecodeRejection.Create(
                "unsupported-version",
                string.Create(CultureInfo.InvariantCulture, $"The header carries the CLFL magic with version {version}; this reader reads version {Version}."));
        }

        var pageSize = BinaryPrimitives.ReadUInt32LittleEndian(page[HeaderPageSizeOffset..]);
        if (!IsPageSize(pageSize))
        {
            return Rejection("page-size-unsupported");
        }

        if (pageSize != (uint)page.Length)
        {
            return Rejection("page-size-mismatch");
        }

        if (!IsValidPage(page))
        {
            return Rejection("crc-mismatch");
        }

        var window = BinaryPrimitives.ReadUInt32LittleEndian(page[HeaderWindowOffset..]);
        if (window == 0)
        {
            return Rejection("window-zero");
        }

        if (window > int.MaxValue)
        {
            return Rejection("window-out-of-range");
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(page[HeaderFlagsOffset..]);
        if ((flags & ~FirstOfOpenFlag) != 0)
        {
            return Rejection("unknown-flag");
        }

        if (page[HeaderFieldsLength..].ContainsAnyExcept((byte)0))
        {
            return Rejection("nonzero-padding");
        }

        return new WalFileHeader(page.Length, (int)window, (flags & FirstOfOpenFlag) != 0);
    }

    private static bool IsPageSize(uint pageSize) =>
        pageSize is >= WalOptions.MinimumPageSize and <= WalOptions.MaximumPageSize && BitOperations.IsPow2(pageSize);

    /// <summary>
    /// Stores the CRC-32C of bytes 4 through P - 1 in bytes 0-3.
    /// </summary>
    public static void SealPage(Span<byte> page) =>
        BinaryPrimitives.WriteUInt32LittleEndian(page, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, page[CrcLength..]));

    public static bool IsValidPage(ReadOnlySpan<byte> page) =>
        BinaryPrimitives.ReadUInt32LittleEndian(page) == Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, page[CrcLength..]);

    /// <summary>
    /// Fills <paramref name="page"/>, which is exactly one page, with a hole page.
    /// </summary>
    public static void WriteHolePage(Span<byte> page)
    {
        page.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(page[CrcLength..], HoleWord(page.Length));
        SealPage(page);
    }

    /// <summary>
    /// Determines whether a valid page is a hole page.
    /// </summary>
    public static bool IsHolePage(ReadOnlySpan<byte> page) =>
        BinaryPrimitives.ReadUInt32LittleEndian(page[CrcLength..]) == HoleWord(page.Length)
        && !page[(CrcLength + FrameWordLength)..].ContainsAnyExcept((byte)0);

    private static uint HoleWord(int pageSize) => EncodeFrame(FrameKind.End, pageSize - CrcLength - FrameWordLength);

    /// <summary>
    /// Applies the frame rules which one data page satisfies on its own: the probe a reader runs on each page it meets,
    /// which allocates nothing.
    /// </summary>
    /// <param name="page">The complete page.</param>
    /// <param name="frameStart">
    /// When non-negative, an offset which must start a Full or First frame on the page.
    /// </param>
    public static bool IsWellFormed(ReadOnlySpan<byte> page, int frameStart = -1) => FrameReason(page, frameStart) is null;

    /// <summary>
    /// Validates one data page as a reader classifies it: <see langword="null"/> for a valid hole page or a valid,
    /// well-formed page, and otherwise the rejection naming the first rule it breaks (wal.md §7).
    /// </summary>
    public static InvalidDataException? ValidatePage(ReadOnlySpan<byte> page) =>
        PageReason(page) is { } reason ? Rejection(reason) : null;

    /// <summary>
    /// Returns the reason code of the first rule a data page breaks: its CRC, then its frames unless it is a hole page.
    /// </summary>
    private static string? PageReason(ReadOnlySpan<byte> page)
    {
        if (!IsValidPage(page))
        {
            return page.ContainsAnyExcept((byte)0) ? "crc-mismatch" : "zero-page";
        }

        if (IsHolePage(page))
        {
            return null;
        }

        return FrameReason(page);
    }

    /// <summary>
    /// Returns the reason code of the first frame rule a data page breaks, or <see langword="null"/> when it is well
    /// formed.
    /// </summary>
    /// <param name="page">The complete page.</param>
    /// <param name="frameStart">
    /// When non-negative, an offset which must start a Full or First frame on the page.
    /// </param>
    private static string? FrameReason(ReadOnlySpan<byte> page, int frameStart = -1)
    {
        var perPage = page.Length - CrcLength - FrameWordLength;
        var offset = CrcLength;
        var leading = BinaryPrimitives.ReadUInt32LittleEndian(page[offset..]);
        if (GetKind(leading) == FrameKind.Continuation)
        {
            var fragment = GetLength(leading);
            if (fragment == 0)
            {
                return "continuation-empty";
            }

            if (fragment > perPage)
            {
                return "continuation-overlong";
            }

            offset += FrameWordLength + fragment;
        }

        var found = frameStart < 0;
        while (offset <= page.Length - FrameWordLength)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(page[offset..]);
            if (word == 0)
            {
                break;
            }

            var length = GetLength(word);
            found |= offset == frameStart;
            switch (GetKind(word))
            {
                case FrameKind.Full when offset + FrameWordLength + length <= page.Length:
                    offset += FrameWordLength + length;
                    continue;
                case FrameKind.Full:
                    return "frame-overrun";
                case FrameKind.Continuation:
                    return "continuation-not-leading";
                case FrameKind.End:
                    return word == HoleWord(page.Length) ? "hole-not-zero" : "end-with-length";
                case FrameKind.First:
                default:
                    return FirstFrameReason(page, offset, length, found);
            }
        }

        if (!found)
        {
            return "frame-start-not-found";
        }

        if (page[offset..].ContainsAnyExcept((byte)0))
        {
            return "nonzero-after-end";
        }

        return null;
    }

    /// <summary>
    /// A First frame fills its page and carries a total above its fragment and within a record's limit.
    /// </summary>
    private static string? FirstFrameReason(ReadOnlySpan<byte> page, int offset, int length, bool found)
    {
        if (length == 0)
        {
            return "first-empty";
        }

        var end = offset + FirstFrameHeaderLength + length;
        if (end > page.Length)
        {
            return "frame-overrun";
        }

        if (end < page.Length)
        {
            return "first-not-filling";
        }

        var total = BinaryPrimitives.ReadUInt64LittleEndian(page[(offset + FrameWordLength)..]);
        if (total <= (ulong)length)
        {
            return "first-total-not-above-fragment";
        }

        if (total > int.MaxValue)
        {
            return "first-total-out-of-range";
        }

        if (!found)
        {
            return "frame-start-not-found";
        }

        return null;
    }

    /// <summary>
    /// Creates the rejection of a header or page which breaks the rule <paramref name="reason"/> names.
    /// </summary>
    private static InvalidDataException Rejection(string reason) => DecodeRejection.Create(reason, reason switch
    {
        "truncated" => "The header is shorter than its 24 bytes of fixed fields.",
        "zero-page" => "The page, or a header's fixed fields, are all zero.",
        "bad-magic" => "The header does not carry the CLFL magic.",
        "page-size-unsupported" => "The header names a page size which is not a power of two from 512 bytes to 64 KiB.",
        "page-size-mismatch" => "The header names a page size other than its page's length.",
        "crc-mismatch" => "The page fails its CRC-32C.",
        "window-zero" => "The header's write window is zero.",
        "window-out-of-range" => "The header's write window exceeds int.MaxValue.",
        "unknown-flag" => "The header sets a flag other than bit 0.",
        "nonzero-padding" => "The header has a non-zero byte from offset 24.",
        "continuation-empty" => "The page's leading Continuation frame carries no bytes.",
        "continuation-overlong" => "The page's leading Continuation frame is longer than a page holds.",
        "frame-overrun" => "A frame runs past the page's end.",
        "continuation-not-leading" => "A Continuation frame follows another frame.",
        "hole-not-zero" => "The page holds a hole page's word but non-zero bytes after it.",
        "end-with-length" => "An End word carries a length.",
        "first-empty" => "A First frame carries no bytes.",
        "first-not-filling" => "A First frame does not fill its page.",
        "first-total-not-above-fragment" => "A First frame's total record length is not above its fragment's.",
        "first-total-out-of-range" => "A First frame's total record length exceeds int.MaxValue.",
        "frame-start-not-found" => "No Full or First frame starts at the offset the reader requires.",
        "nonzero-after-end" => "The page has a non-zero byte after its frames end.",
        _ => throw new UnreachableException(),
    });
}

/// <summary>
/// What decoding a WAL header page gives (conventions.md §15): its fields, or the <see cref="InvalidDataException"/>
/// naming the first rule it breaks, with the rule's reason code (wal.md §7).
/// </summary>
internal readonly union WalHeaderDecoding(WalFileHeader, InvalidDataException);

/// <summary>
/// The kind held in bits 31..30 of a frame word.
/// </summary>
internal enum FrameKind
{
    /// <summary>
    /// A zero word ends a page's frames; a word of this kind with length P - 8 makes the page a hole page.
    /// </summary>
    End = 0,
    Full = 1,
    First = 2,
    Continuation = 3,
}

/// <summary>
/// The fields of a valid file header.
/// </summary>
/// <param name="PageSize">The page size P.</param>
/// <param name="WriteWindowPages">The write window W.</param>
/// <param name="FirstOfOpen">Whether the file is the first one its open created.</param>
internal readonly record struct WalFileHeader(int PageSize, int WriteWindowPages, bool FirstOfOpen);
