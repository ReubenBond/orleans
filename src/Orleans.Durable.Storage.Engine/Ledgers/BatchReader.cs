using System.Diagnostics;
using System.Numerics;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Reads a batch's fields in order across the pages of its buffer (ledgers.md §7), up to a limit: the offset of the
/// batch's CRC-32C. Each read and skip first checks that its field ends at or before the limit, and takes nothing when it
/// does not.
/// </summary>
/// <remarks>
/// <see cref="BatchView.Enumerator"/> holds one for its whole walk, so it must not be copied while in use.
/// </remarks>
internal ref struct BatchReader(ArcBuffer buffer, int limit)
{
    /// <summary>The widest field the reader reads as an integer.</summary>
    private const int MaxFieldLength = sizeof(uint);
    private ArcBuffer.MemoryEnumerator _pages = buffer.MemorySegments;

    // The current page; its unread suffix starts at Position - _pageStart.
    private ReadOnlyMemory<byte> _page;

    // The offset from the start of the buffer of the current page.
    private int _pageStart;

    /// <summary>Gets the offset of the next byte from the start of the buffer.</summary>
    public int Position { get; private set; }

    /// <summary>Gets the bytes between the next byte and the limit.</summary>
    public readonly int Remaining { get => field - Position; } = limit;

    /// <summary>
    /// Moves to the page which holds the next byte, when the current page has no more, and returns its offset from the
    /// start of the buffer. At the end of the buffer it stays in the last page.
    /// </summary>
    public int MoveToPageWithNextByte()
    {
        _ = Unread();
        return _pageStart;
    }

    /// <summary>
    /// Takes the next <paramref name="count"/> bytes when they end at or before the limit and lie in one page, as a view
    /// of that page; otherwise takes nothing.
    /// </summary>
    public bool TryTakeContiguous(int count, out ReadOnlyMemory<byte> bytes)
    {
        var unread = Unread();
        if (count > Remaining || count > unread.Length)
        {
            bytes = default;
            return false;
        }

        bytes = _page.Slice(Position - _pageStart, count);
        Position += count;
        return true;
    }

    /// <summary>Skips the next <paramref name="count"/> bytes when they end at or before the limit.</summary>
    public bool TrySkip(ulong count)
    {
        if (count > (ulong)Remaining)
        {
            return false;
        }

        var left = (int)count;
        while (left > 0)
        {
            var unread = Unread();
            var taken = Math.Min(left, NonEmpty(unread).Length);
            Position += taken;
            left -= taken;
        }

        return true;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with the next bytes, across pages, when they end at or before the limit.
    /// </summary>
    public bool TryCopyTo(scoped Span<byte> destination)
    {
        if (destination.Length > Remaining)
        {
            return false;
        }

        while (!destination.IsEmpty)
        {
            var unread = Unread();
            var taken = Math.Min(destination.Length, NonEmpty(unread).Length);
            unread[..taken].CopyTo(destination);
            Position += taken;
            destination = destination[taken..];
        }

        return true;
    }

    /// <summary>
    /// Reads the next little-endian unsigned integer of <typeparamref name="T"/>'s width, across pages, when it ends at or
    /// before the limit.
    /// </summary>
    public bool TryRead<T>(out T value)
        where T : IBinaryInteger<T>
    {
        var size = T.Zero.GetByteCount();
        var unread = Unread();
        if (size > Remaining)
        {
            value = T.Zero;
            return false;
        }

        if (unread.Length >= size)
        {
            value = T.ReadLittleEndian(unread[..size], isUnsigned: true);
            Position += size;
            return true;
        }

        Debug.Assert(size <= MaxFieldLength, "The reader reads integers of at most 4 bytes.");
        Span<byte> scratch = stackalloc byte[MaxFieldLength];
        scratch = scratch[..size];
        _ = TryCopyTo(scratch);
        value = T.ReadLittleEndian(scratch, isUnsigned: true);
        return true;
    }

    /// <summary>
    /// Reads the next varint (ledgers.md §7), across pages, up to the limit: <see cref="Varint.Status.Unterminated"/> when
    /// the limit comes first, within <see cref="Varint.MaxLength"/> bytes, having consumed every byte to it.
    /// </summary>
    public Varint.Status TryReadVarint(out ulong value)
    {
        value = 0;
        for (var index = 0; index < Varint.MaxLength; index++)
        {
            if (!TryRead(out byte next))
            {
                return Varint.Status.Unterminated;
            }

            if (!Varint.Accumulate(next, index, ref value, out var status))
            {
                return status;
            }
        }

        return Varint.Status.Invalid;
    }

    private static ReadOnlySpan<byte> NonEmpty(ReadOnlySpan<byte> unread) =>
        unread.IsEmpty ? throw new UnreachableException("A batch field was read past the end of its buffer.") : unread;

    /// <summary>Returns the unread bytes of the current page, moving to the next page with bytes when it has none.</summary>
    private ReadOnlySpan<byte> Unread()
    {
        while (Position - _pageStart == _page.Length && _pages.MoveNext())
        {
            _page = _pages.Current;
            _pageStart = Position;
        }

        return _page.Span[(Position - _pageStart)..];
    }
}
