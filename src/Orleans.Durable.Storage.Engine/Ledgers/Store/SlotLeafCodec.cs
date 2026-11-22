using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;

using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// What decoding a slot-index leaf gives (conventions.md §15): the leaf, or the <see cref="InvalidDataException"/>
/// naming the first rule its bytes break, with the rule's reason code (ledgerstore.md §7.4).
/// </summary>
internal readonly union SlotLeafDecoding(FrozenLeaf, InvalidDataException);

/// <summary>
/// The state of one slot in the slot table.
/// </summary>
internal enum SlotKind : byte
{
    /// <summary>The slot holds no record.</summary>
    Missing = 0,

    /// <summary>The slot's latest record holds a non-empty value: DATA.</summary>
    Data = 1,

    /// <summary>
    /// The slot's state cannot be established: its latest record is damaged or may lie in a damaged range. The page is
    /// that of the latest record known, if any. A later record re-establishes the slot.
    /// </summary>
    Faulty = 2,
}

/// <summary>
/// One slot's state and the WAL page holding its latest record, in 16 bytes.
/// </summary>
internal readonly record struct SlotEntry
{
    private readonly ulong _file;
    private readonly uint _index;

    public SlotEntry(SlotKind kind, WalPage page)
    {
        _file = page.File;
        _index = page.Index;
        Kind = kind;
    }

    public SlotKind Kind { get; }

    public WalPage Page => new(_file, _index);

    /// <summary>
    /// Gets a value indicating whether the slot has an entry: DATA or Faulty. A leaf
    /// encodes exactly these entries.
    /// </summary>
    public bool HasRecord => Kind != SlotKind.Missing;
}

/// <summary>
/// Encodes and decodes the 256 entries of a slot-index leaf in the <c>CLSI</c> version 1 leaf format (ledgerstore.md §7.4).
/// </summary>
/// <remarks>
/// Each present entry's page becomes a virtual page, its file's virtual base plus its page index, predicted by a
/// per-leaf line through the slot's leaf index. Residuals from the line are bit-packed at one width, and entries the
/// width cannot hold are stored as patched exceptions (Zukowski et al., ICDE 2006).
/// </remarks>
internal static class SlotLeafCodec
{
    public const int SlotBits = 8;

    public const int Slots = 1 << SlotBits;

    internal const int HeaderLength = 24;
    internal const int FileEntryLength = 16;
    internal const int BitmapLength = Slots / 8;
    internal const int ExceptionLength = 9;
    internal const byte AllPresentFlag = 1;

    private const int ExceptionBits = ExceptionLength * 8;
    private const int MaximumResidualWidth = 63;

    /// <summary>
    /// The length of the shortest valid leaf: every slot present, one file, zero widths, and no exceptions.
    /// </summary>
    internal static readonly int MinimumLength = EncodedLength(Slots, 1, allPresent: true, 0, 0, 0, 0);

    /// <summary>
    /// The length of the longest valid leaf: the widest widths, 256 files, and an exception for every present entry.
    /// </summary>
    internal static readonly int MaximumLength =
        Enumerable.Range(1, Slots).Max(static present => EncodedLength(present, Slots, present == Slots, 2, 8, MaximumResidualWidth, present));

    /// <summary>
    /// Encodes a leaf with at least one present entry, canonically: equal entries always produce equal bytes.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<SlotEntry> entries)
    {
        if (entries.Length != Slots)
        {
            throw new ArgumentException("A leaf holds exactly 256 entries.", nameof(entries));
        }

        Span<int> indexes = stackalloc int[Slots];
        var n = 0;
        for (var index = 0; index < Slots; index++)
        {
            if (entries[index].HasRecord)
            {
                indexes[n++] = index;
            }
        }

        if (n == 0)
        {
            throw new ArgumentException("An empty leaf is never encoded.", nameof(entries));
        }

        indexes = indexes[..n];

        // The file table: distinct files ascending, each based just past the previous one's highest page.
        var files = new SortedSet<ulong>();
        foreach (var index in indexes)
        {
            _ = files.Add(entries[index].Page.File);
        }

        var fileTable = files.ToArray();
        var maxPage = new ulong[fileTable.Length];
        foreach (var index in indexes)
        {
            var page = entries[index].Page;
            var g = Array.BinarySearch(fileTable, page.File);
            maxPage[g] = Math.Max(maxPage[g], page.Index);
        }

        var bases = new ulong[fileTable.Length];
        for (var g = 1; g < bases.Length; g++)
        {
            bases[g] = checked(bases[g - 1] + maxPage[g - 1] + 1);
        }

        Span<long> virtualPages = stackalloc long[n];
        Span<int> fileIndexes = stackalloc int[n];
        Span<int> states = stackalloc int[n];
        var stateWidth = 0;
        for (var j = 0; j < n; j++)
        {
            var entry = entries[indexes[j]];
            var g = Array.BinarySearch(fileTable, entry.Page.File);
            fileIndexes[j] = g;
            virtualPages[j] = checked((long)(bases[g] + entry.Page.Index));
            states[j] = (int)entry.Kind - 1;

            // States are 0 DATA and 1 Faulty, so the width is the largest state's bit length.
            stateWidth = Math.Max(stateWidth, BitLength((ulong)states[j]));
        }

        // The slope runs through the quartile entries, so one outlier at either end does not bend the line.
        var j1 = (n - 1) / 4;
        var j3 = n - 1 - j1;
        var slope = indexes[j3] == indexes[j1]
            ? 0
            : FloorDivide(checked((virtualPages[j3] - virtualPages[j1]) * Slots), indexes[j3] - indexes[j1]);

        Span<long> offsets = stackalloc long[n];
        for (var j = 0; j < n; j++)
        {
            offsets[j] = checked((long)(virtualPages[j] - Predict(indexes[j], slope)));
        }

        var (intercept, residualWidth) = ChooseWidth(offsets);
        var exceptions = 0;
        for (var j = 0; j < n; j++)
        {
            if (!Retained(offsets[j], intercept, residualWidth))
            {
                exceptions++;
            }
        }

        var fileIndexWidth = BitLength((ulong)(fileTable.Length - 1));
        var allPresent = n == Slots;
        var length = EncodedLength(n, fileTable.Length, allPresent, stateWidth, fileIndexWidth, residualWidth, exceptions);
        var bytes = new byte[length];
        var span = bytes.AsSpan();
        span[0] = allPresent ? AllPresentFlag : (byte)0;
        span[1] = (byte)stateWidth;
        span[2] = (byte)fileIndexWidth;
        span[3] = (byte)residualWidth;
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)fileTable.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)exceptions);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], intercept);
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], slope);
        var offset = HeaderLength;
        for (var g = 0; g < fileTable.Length; g++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], fileTable[g]);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(offset + 8)..], bases[g]);
            offset += FileEntryLength;
        }

        if (!allPresent)
        {
            foreach (var index in indexes)
            {
                span[offset + (index >> 3)] |= (byte)(1 << (index & 7));
            }

            offset += BitmapLength;
        }

        var statesBits = span.Slice(offset, PackedLength(n, stateWidth));
        offset += statesBits.Length;
        var fileIndexBits = span.Slice(offset, PackedLength(n, fileIndexWidth));
        offset += fileIndexBits.Length;
        var residualBits = span.Slice(offset, PackedLength(n, residualWidth));
        offset += residualBits.Length;
        for (var j = 0; j < n; j++)
        {
            Pack(statesBits, j, stateWidth, (ulong)states[j]);
            Pack(fileIndexBits, j, fileIndexWidth, (ulong)fileIndexes[j]);
            if (Retained(offsets[j], intercept, residualWidth))
            {
                Pack(residualBits, j, residualWidth, (ulong)(offsets[j] - intercept));
            }
            else
            {
                span[offset] = (byte)indexes[j];
                BinaryPrimitives.WriteUInt64LittleEndian(span[(offset + 1)..], (ulong)virtualPages[j]);
                offset += ExceptionLength;
            }
        }

        return bytes;
    }

    internal static int EncodedLength(int present, int fileCount, bool allPresent, int stateWidth, int fileIndexWidth, int residualWidth, int exceptions) =>
        HeaderLength
        + FileEntryLength * fileCount
        + (allPresent ? 0 : BitmapLength)
        + PackedLength(present, stateWidth)
        + PackedLength(present, fileIndexWidth)
        + PackedLength(present, residualWidth)
        + ExceptionLength * exceptions;

    internal static int PackedLength(int count, int width) => checked(count * width + 7) / 8;

    /// <summary>
    /// Returns <c>floor(index * slope / 256)</c>; an arithmetic shift of the exact product is that floor.
    /// </summary>
    internal static Int128 Predict(int index, long slope) => ((Int128)index * slope) >> SlotBits;

    internal static ulong Unpack(ReadOnlySpan<byte> bits, int index, int width)
    {
        if (width == 0)
        {
            return 0;
        }

        var bit = (long)index * width;
        var start = (int)(bit >> 3);
        var shift = (int)(bit & 7);
        if (shift + width <= 64 && start + sizeof(ulong) <= bits.Length)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(bits[start..]) >> shift;
            return width == 64 ? value : value & ((1UL << width) - 1);
        }

        Span<byte> window = stackalloc byte[16];
        window.Clear();
        bits.Slice(start, Math.Min(16, bits.Length - start)).CopyTo(window);
        var word = BinaryPrimitives.ReadUInt128LittleEndian(window) >> (int)(bit & 7);
        return (ulong)(word & ((UInt128.One << width) - 1));
    }

    internal static int BitLength(ulong value) => 64 - BitOperations.LeadingZeroCount(value);

    private static void Pack(Span<byte> bits, int index, int width, ulong value)
    {
        if (width == 0)
        {
            return;
        }

        var bit = (long)index * width;
        var start = (int)(bit >> 3);
        var count = Math.Min(16, bits.Length - start);
        Span<byte> window = stackalloc byte[16];
        window.Clear();
        bits.Slice(start, count).CopyTo(window);
        var word = BinaryPrimitives.ReadUInt128LittleEndian(window) | ((UInt128)value << (int)(bit & 7));
        BinaryPrimitives.WriteUInt128LittleEndian(window, word);
        window[..count].CopyTo(bits[start..]);
    }

    private static bool Retained(long offset, long intercept, int width) =>
        offset >= intercept && (ulong)(offset - intercept) < 1UL << width;

    /// <summary>
    /// Chooses the residual width minimizing the packed residuals plus exceptions, preferring the smaller width, and
    /// the lowest window of offsets that width retains.
    /// </summary>
    private static (long Intercept, int Width) ChooseWidth(ReadOnlySpan<long> offsets)
    {
        Span<long> sorted = stackalloc long[offsets.Length];
        offsets.CopyTo(sorted);
        sorted.Sort();
        var n = sorted.Length;
        var bestCost = long.MaxValue;
        var bestWidth = 0;
        var bestIntercept = sorted[0];
        for (var width = 0; width <= MaximumResidualWidth; width++)
        {
            var span = 1UL << width;
            var low = 0;
            var retained = 0;
            var intercept = sorted[0];
            for (var high = 0; high < n; high++)
            {
                while ((ulong)(sorted[high] - sorted[low]) >= span)
                {
                    low++;
                }

                if (high - low + 1 > retained)
                {
                    retained = high - low + 1;
                    intercept = sorted[low];
                }
            }

            var cost = (long)n * width + (long)ExceptionBits * (n - retained);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestWidth = width;
                bestIntercept = intercept;
            }

            if (retained == n)
            {
                break;
            }
        }

        return (bestIntercept, bestWidth);
    }

    private static long FloorDivide(long dividend, long divisor)
    {
        var quotient = Math.DivRem(dividend, divisor, out var remainder);
        return remainder != 0 && remainder < 0 != divisor < 0 ? quotient - 1 : quotient;
    }
}

/// <summary>
/// A validated, immutable encoded leaf which decodes any entry in constant time.
/// </summary>
internal sealed class FrozenLeaf
{
    private readonly ulong[] _files;
    private readonly ulong[] _bases;
    private readonly long _intercept;
    private readonly long _slope;
    private readonly int _stateWidth;
    private readonly int _fileIndexWidth;
    private readonly int _residualWidth;
    private readonly int _bitmapOffset;
    private readonly int _statesOffset;
    private readonly int _fileIndexesOffset;
    private readonly int _residualsOffset;
    private readonly int _exceptionsOffset;
    private readonly int _exceptionCount;

    private FrozenLeaf(byte[] bytes, int present, ulong[] files, ulong[] bases, long intercept, long slope, int stateWidth, int fileIndexWidth, int residualWidth, int bitmapOffset, int exceptionCount)
    {
        Bytes = bytes;
        Count = present;
        _files = files;
        _bases = bases;
        _intercept = intercept;
        _slope = slope;
        _stateWidth = stateWidth;
        _fileIndexWidth = fileIndexWidth;
        _residualWidth = residualWidth;
        _bitmapOffset = bitmapOffset;
        _statesOffset = bitmapOffset < 0 ? SlotLeafCodec.HeaderLength + SlotLeafCodec.FileEntryLength * files.Length : bitmapOffset + SlotLeafCodec.BitmapLength;
        _fileIndexesOffset = _statesOffset + SlotLeafCodec.PackedLength(present, stateWidth);
        _residualsOffset = _fileIndexesOffset + SlotLeafCodec.PackedLength(present, fileIndexWidth);
        _exceptionsOffset = _residualsOffset + SlotLeafCodec.PackedLength(present, residualWidth);
        _exceptionCount = exceptionCount;
    }

    /// <summary>
    /// Gets the encoded bytes.
    /// </summary>
    public byte[] Bytes { get; }

    /// <summary>
    /// Gets the number of present entries.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Gets the highest file an entry names.
    /// </summary>
    public ulong MaxFile => _files[^1];

    /// <summary>
    /// Encodes entries into a leaf; the encoder's output satisfies every rule <see cref="Decode(byte[])"/> checks.
    /// </summary>
    public static FrozenLeaf Encode(ReadOnlySpan<SlotEntry> entries)
    {
        var decoding = Decode(SlotLeafCodec.Encode(entries));
        Debug.Assert(decoding.Value is not null, "conventions.md §15: a decoder answers a union which holds a case.");
        return decoding switch
        {
            FrozenLeaf leaf => leaf,
            InvalidDataException rejection => throw new UnreachableException("ledgerstore.md §7.4: the canonical encoder's output keeps every rule of the layout.", rejection),
            null => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// Decodes an encoded slot-index leaf, validating every entry it holds: the leaf, or the rejection naming the first
    /// rule of ledgerstore.md §7.4 its bytes break, in the order that section lists them: its header and section layout,
    /// then its entries (<see cref="CheckEntries"/>).
    /// </summary>
    public static SlotLeafDecoding Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var span = bytes.AsSpan();
        if (span.Length < SlotLeafCodec.HeaderLength)
        {
            return Rejection("truncated");
        }

        var flags = span[0];
        int stateWidth = span[1];
        int fileIndexWidth = span[2];
        int residualWidth = span[3];
        int fileCount = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        int exceptionCount = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        var intercept = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);
        var slope = BinaryPrimitives.ReadInt64LittleEndian(span[16..]);
        if ((flags & ~SlotLeafCodec.AllPresentFlag) != 0)
        {
            return Rejection("reserved-flags");
        }

        if (stateWidth > 1 || fileIndexWidth > 8 || residualWidth > 63)
        {
            return Rejection("width-out-of-range");
        }

        if (fileCount is < 1 or > SlotLeafCodec.Slots)
        {
            return Rejection("file-count-out-of-range");
        }

        var allPresent = (flags & SlotLeafCodec.AllPresentFlag) != 0;
        var tableEnd = SlotLeafCodec.HeaderLength + SlotLeafCodec.FileEntryLength * fileCount;
        if (span.Length < tableEnd + (allPresent ? 0 : SlotLeafCodec.BitmapLength))
        {
            return Rejection("truncated");
        }

        var files = new ulong[fileCount];
        var bases = new ulong[fileCount];
        for (var g = 0; g < fileCount; g++)
        {
            var entry = span[(SlotLeafCodec.HeaderLength + g * SlotLeafCodec.FileEntryLength)..];
            files[g] = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            bases[g] = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            if (g > 0 && (files[g] <= files[g - 1] || bases[g] <= bases[g - 1]))
            {
                return Rejection("file-table-order");
            }
        }

        int present;
        var bitmapOffset = -1;
        if (allPresent)
        {
            present = SlotLeafCodec.Slots;
        }
        else
        {
            bitmapOffset = tableEnd;
            present = 0;
            for (var word = 0; word < SlotLeafCodec.BitmapLength; word += sizeof(ulong))
            {
                present += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(span[(bitmapOffset + word)..]));
            }

            if (present is 0 or SlotLeafCodec.Slots)
            {
                return Rejection("bitmap-empty-or-full");
            }
        }

        if (exceptionCount > present)
        {
            return Rejection("too-many-exceptions");
        }

        if (span.Length != SlotLeafCodec.EncodedLength(present, fileCount, allPresent, stateWidth, fileIndexWidth, residualWidth, exceptionCount))
        {
            return Rejection("length-mismatch");
        }

        var leaf = new FrozenLeaf(bytes, present, files, bases, intercept, slope, stateWidth, fileIndexWidth, residualWidth, bitmapOffset, exceptionCount);
        if (leaf.CheckEntries() is { } entryRejection)
        {
            return entryRejection;
        }

        return leaf;
    }

    /// <summary>
    /// Decodes the entry at <paramref name="index"/>.
    /// </summary>
    public SlotEntry Get(int index)
    {
        var span = Bytes.AsSpan();
        int rank;
        if (_bitmapOffset < 0)
        {
            rank = index;
        }
        else
        {
            if ((span[_bitmapOffset + (index >> 3)] & (1 << (index & 7))) == 0)
            {
                return default;
            }

            rank = Rank(span, index);
        }

        return Decode(span, index, rank, FindException(span, index));
    }

    /// <summary>
    /// Decodes every entry into <paramref name="entries"/>, which holds 256 entries.
    /// </summary>
    public void DecodeAll(Span<SlotEntry> entries)
    {
        var span = Bytes.AsSpan();
        var rank = 0;
        var exception = 0;
        for (var index = 0; index < SlotLeafCodec.Slots; index++)
        {
            if (_bitmapOffset >= 0 && (span[_bitmapOffset + (index >> 3)] & (1 << (index & 7))) == 0)
            {
                entries[index] = default;
                continue;
            }

            ulong? value = null;
            if (exception < _exceptionCount && ExceptionIndex(span, exception) == index)
            {
                value = ExceptionPage(span, exception++);
            }

            entries[index] = Decode(span, index, rank++, value);
        }
    }

    private static InvalidDataException Rejection(string reason) => DecodeRejection.Create(reason, "The slot-index leaf " + reason switch
    {
        "truncated" => "is truncated",
        "reserved-flags" => "has reserved flags",
        "width-out-of-range" => "has a width out of range",
        "file-count-out-of-range" => "has a file count out of range",
        "file-table-order" => "has a file table out of order",
        "bitmap-empty-or-full" => "has an empty or full presence bitmap",
        "too-many-exceptions" => "has more exceptions than entries",
        "length-mismatch" => "has the wrong length",
        "exception-order" => "has exceptions out of order",
        "exception-absent-slot" => "has an exception for an absent slot",
        "exception-residual" => "has a residual at an exception",
        "file-index-out-of-range" => "has a file index out of range",
        "page-out-of-range" => "has a page out of range",
        "nonzero-padding" => "has nonzero padding bits",
        _ => throw new UnreachableException(),
    } + ".");

    private int Rank(ReadOnlySpan<byte> span, int index)
    {
        var rank = 0;
        var word = 0;
        for (; (word + 1) * 64 <= index; word++)
        {
            rank += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(span[(_bitmapOffset + word * 8)..]));
        }

        var bits = index - word * 64;
        if (bits > 0)
        {
            var last = BinaryPrimitives.ReadUInt64LittleEndian(span[(_bitmapOffset + word * 8)..]);
            rank += BitOperations.PopCount(last & ((1UL << bits) - 1));
        }

        return rank;
    }

    private ulong? FindException(ReadOnlySpan<byte> span, int index)
    {
        int low = 0, high = _exceptionCount - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            var at = ExceptionIndex(span, middle);
            if (at == index)
            {
                return ExceptionPage(span, middle);
            }

            if (at < index)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return null;
    }

    private SlotEntry Decode(ReadOnlySpan<byte> span, int index, int rank, ulong? exception)
    {
        var state = (int)SlotLeafCodec.Unpack(span[_statesOffset..], rank, _stateWidth);
        var g = (int)SlotLeafCodec.Unpack(span[_fileIndexesOffset..], rank, _fileIndexWidth);
        var virtualPage = exception ?? PredictedPage(span, index, rank);
        var page = virtualPage - _bases[g];
        return new SlotEntry((SlotKind)(state + 1), new WalPage(_files[g], (uint)page));
    }

    /// <summary>
    /// Checks the rules no entry may break, returning the rejection naming the first one the leaf breaks, or
    /// <see langword="null"/>: exceptions only for present slots in ascending order with zero residuals, then for each
    /// present entry in slot order a file index within the table and a page within 32 bits, then zero padding bits.
    /// </summary>
    private InvalidDataException? CheckEntries()
    {
        var span = Bytes.AsSpan();
        var previous = -1;
        for (var at = 0; at < _exceptionCount; at++)
        {
            var index = ExceptionIndex(span, at);
            if (index <= previous)
            {
                return Rejection("exception-order");
            }

            if (_bitmapOffset >= 0 && (span[_bitmapOffset + (index >> 3)] & (1 << (index & 7))) == 0)
            {
                return Rejection("exception-absent-slot");
            }

            if (SlotLeafCodec.Unpack(span[_residualsOffset..], _bitmapOffset < 0 ? index : Rank(span, index), _residualWidth) != 0)
            {
                return Rejection("exception-residual");
            }

            previous = index;
        }

        var rank = 0;
        var exception = 0;
        for (var index = 0; index < SlotLeafCodec.Slots; index++)
        {
            if (_bitmapOffset >= 0 && (span[_bitmapOffset + (index >> 3)] & (1 << (index & 7))) == 0)
            {
                continue;
            }

            // Decode admits a state width of at most 1, so every state is DATA (0) or Faulty (1).
            Debug.Assert(SlotLeafCodec.Unpack(span[_statesOffset..], rank, _stateWidth) <= 1, "A state of at most one bit is at most 1.");
            var g = SlotLeafCodec.Unpack(span[_fileIndexesOffset..], rank, _fileIndexWidth);
            if (g >= (ulong)_files.Length)
            {
                return Rejection("file-index-out-of-range");
            }

            var virtualPage = exception < _exceptionCount && ExceptionIndex(span, exception) == index
                ? ExceptionPage(span, exception++)
                : PredictedPage(span, index, rank);
            var page = virtualPage - _bases[(int)g];
            if (page < 0 || page > uint.MaxValue)
            {
                return Rejection("page-out-of-range");
            }

            rank++;
        }

        if (!PaddingIsZero(span[_statesOffset.._fileIndexesOffset], Count, _stateWidth)
            || !PaddingIsZero(span[_fileIndexesOffset.._residualsOffset], Count, _fileIndexWidth)
            || !PaddingIsZero(span[_residualsOffset.._exceptionsOffset], Count, _residualWidth))
        {
            return Rejection("nonzero-padding");
        }

        return null;
    }

    /// <summary>
    /// Returns the leaf index exception <paramref name="exception"/> names.
    /// </summary>
    private int ExceptionIndex(ReadOnlySpan<byte> span, int exception) =>
        span[_exceptionsOffset + exception * SlotLeafCodec.ExceptionLength];

    /// <summary>
    /// Returns the virtual page exception <paramref name="exception"/> stores, a <c>u64</c>.
    /// </summary>
    private ulong ExceptionPage(ReadOnlySpan<byte> span, int exception) =>
        BinaryPrimitives.ReadUInt64LittleEndian(span[(_exceptionsOffset + exception * SlotLeafCodec.ExceptionLength + 1)..]);

    /// <summary>
    /// Returns the virtual page the line and residual give the entry at <paramref name="index"/> and
    /// <paramref name="rank"/>, in 128-bit arithmetic.
    /// </summary>
    private Int128 PredictedPage(ReadOnlySpan<byte> span, int index, int rank) =>
        (Int128)_intercept + SlotLeafCodec.Predict(index, _slope) + SlotLeafCodec.Unpack(span[_residualsOffset..], rank, _residualWidth);

    private static bool PaddingIsZero(ReadOnlySpan<byte> bits, int count, int width)
    {
        var used = (long)count * width % 8;
        return used == 0 || bits[^1] >> (int)used == 0;
    }
}
