using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Orleans.Durable.Storage.Engine.Buffers;

/// <summary>
/// A <see cref="IBufferWriter{T}"/> implementation implemented using pooled arrays which is specialized for creating <see cref="ReadOnlySequence{T}"/> instances.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public sealed class ArcBufferWriter : IBufferWriter<byte>, IDisposable
{
    // Indicates that every page comes from the aligned size class.
    private readonly bool _aligned;

    // The size of the first page, which Reset rents again: the expected length, at most the largest large page.
    private readonly int _firstPageSize;

    // A floor on the size of each page after the first: the largest large page when the expected length exceeds one
    // page, and otherwise 0, so later pages come from the writer's class.
    private readonly int _laterPageMinimumSize;

    // The first page. This is the page which consumers will consume from.
    // This may be equal to the current page, or it may be a previous page.
    private ArcBufferPage _readPage;

    // The current page. This is the page which will be written to when the next write occurs.
    private ArcBufferPage _writePage;

    // The current page. This is the last page which was allocated. In the linked list formed by the pages, _first <= _current <= _tail.
    private ArcBufferPage _tail;

    // The offset into the first page which has been consumed already. When this reaches the end of the page, the page can be unpinned.
    private int _readIndex;

    // The total length of the buffer.
    private int _totalLength;

    // Indicates whether the writer has been disposed.
    private bool _disposed;

    // Indicates that this writer directly references pages pinned from another buffer.
    private bool _hasPinnedPages;

    // Owns the retained view when this writer references pages from another buffer.
    private ArcBuffer _pinnedBuffer;


    /// <summary>
    /// Gets the minimum page size.
    /// </summary>
    public const int MinimumPageSize = ArcBufferPagePool.MinimumPageSize;

    /// <summary>
    /// Gets the size and address alignment of every page of a writer created by <see cref="CreateAligned"/>.
    /// </summary>
    public const int AlignedPageSize = ArcBufferPagePool.AlignedPageSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArcBufferWriter"/> class.
    /// </summary>
    public ArcBufferWriter()
        : this(MinimumPageSize)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArcBufferWriter"/> class with room for an expected write.
    /// </summary>
    /// <param name="expectedLength">
    /// The expected length of the write. The first page holds it, up to the largest large page (1 MiB); a writer which
    /// expects more allocates each later page at that size, so its bytes are a chain of 1 MiB pages. The expected
    /// length survives <see cref="Reset"/>.
    /// </param>
    public ArcBufferWriter(int expectedLength)
        : this(expectedLength, aligned: false)
    {
    }

    private ArcBufferWriter(int expectedLength, bool aligned)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        _aligned = aligned;
        _firstPageSize = Math.Min(expectedLength, ArcBufferPagePool.LargestPageSize);
        _laterPageMinimumSize = expectedLength > _firstPageSize ? ArcBufferPagePool.LargestPageSize : 0;
        _readPage = _writePage = _tail = RentPage(_firstPageSize);
        Debug.Assert(_readPage.ReferenceCount == 0);
        _readPage.Pin(_readPage.Version);
    }

    /// <summary>
    /// Creates a writer whose pages all come from the aligned size class: each holds
    /// <see cref="AlignedPageSize"/> bytes starting at an address aligned to <see cref="AlignedPageSize"/>, on a
    /// pinned slab which never moves.
    /// </summary>
    /// <remarks>
    /// Spans and memory returned by the writer never exceed one page, so a size hint above
    /// <see cref="AlignedPageSize"/> cannot be satisfied. Direct I/O can submit each page as is.
    /// </remarks>
    /// <returns>The writer.</returns>
    public static ArcBufferWriter CreateAligned() => new(0, aligned: true);

    /// <summary>
    /// Gets the number of unconsumed bytes.
    /// </summary>
    public int Length
    {
        get
        {
            ThrowIfDisposed();
            return _totalLength - _readIndex;
        }
    }

    /// <summary>
    /// Gets the reader for this writer.
    /// </summary>
    public ArcBufferReader Reader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(this);
    }

    /// <summary>
    /// Adds additional buffers to the destination list until the list has reached its capacity.
    /// </summary>
    /// <param name="buffers">The destination to add buffers to.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReplenishBuffers(List<ArraySegment<byte>> buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        ThrowIfDisposed();
        ThrowIfPinnedPages();

        // Skip half-full pages in an attempt to minimize the number of buffers added to the destination
        // at the expense of under-utilized memory. This could be tweaked up to increase page utilization.
        const int MinimumUsablePageSize = MinimumPageSize / 2;

        while (buffers.Count < buffers.Capacity)
        {
            // Only use the current page if it is greater than the minimum "usable" page size.
            if (_tail.WriteCapacity > MinimumUsablePageSize)
            {
                buffers.Add(_tail.WritableArraySegment);
            }

            // Allocate a new page.
            _ = AllocatePage(0);
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void IBufferWriter<byte>.Advance(int count) => AdvanceWriter(count);

    /// <summary>
    /// Advances the writer by the specified number of bytes.
    /// </summary>
    /// <param name="count">The numbers of bytes to advance the writer by.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceWriter(int count)
    {
        ThrowIfDisposed();
        ThrowIfPinnedPages();

#if NET5_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 0);
#else
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Length must be greater than or equal to 0.");
#endif
        _totalLength += count;
        while (true)
        {
            var amount = Math.Min(_writePage.WriteCapacity, count);
            _writePage.Advance(amount);
            count -= amount;

            if (count == 0)
            {
                break;
            }

            var next = _writePage.Next;
            Debug.Assert(next is not null);
            _writePage = next;
        }
    }

    /// <summary>
    /// Overwrites bytes which have already been written to this buffer without changing the writer position.
    /// </summary>
    /// <param name="offset">The offset into the unconsumed bytes at which to start writing.</param>
    /// <param name="value">The bytes to write.</param>
    public void WriteAt(int offset, ReadOnlySpan<byte> value)
    {
        ThrowIfDisposed();
        ThrowIfPinnedPages();

#if NET5_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, Length - offset);
#else
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be greater than or equal to 0.");
        if (offset > Length) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be less than or equal to the unconsumed length of the buffer.");
        if (value.Length > Length - offset) throw new ArgumentOutOfRangeException(nameof(value), "Value must fit within the unconsumed length of the buffer.");
#endif

        if (value.IsEmpty)
        {
            return;
        }

        var page = _readPage;
        var pageOffset = _readIndex + offset;
        while (pageOffset >= page.Length)
        {
            pageOffset -= page.Length;
            page = page.Next ?? throw new InvalidOperationException("Buffer page chain ended unexpectedly.");
        }

        while (!value.IsEmpty)
        {
            var destination = page.AsSpan(pageOffset, page.Length - pageOffset);
            var copyLength = Math.Min(destination.Length, value.Length);
            value[..copyLength].CopyTo(destination);
            value = value[copyLength..];

            if (value.IsEmpty)
            {
                return;
            }

            page = page.Next ?? throw new InvalidOperationException("Buffer page chain ended unexpectedly.");
            pageOffset = 0;
        }
    }

    /// <summary>
    /// Truncates this buffer to the specified unconsumed length.
    /// </summary>
    /// <param name="length">The new length of the unconsumed bytes.</param>
    public void Truncate(int length)
    {
        ThrowIfDisposed();
        ThrowIfPinnedPages();

#if NET5_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Length);
#else
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "Length must be greater than or equal to 0.");
        if (length > Length) throw new ArgumentOutOfRangeException(nameof(length), "Length must be less than or equal to the unconsumed length of the buffer.");
#endif

        if (length == Length)
        {
            return;
        }

        var remaining = _readIndex + length;
        var page = _readPage;
        while (remaining > page.Length)
        {
            remaining -= page.Length;
            page = page.Next ?? throw new InvalidOperationException("Buffer page chain ended unexpectedly.");
        }

        page.SetLength(remaining, page.Version);
        var next = page.Next;
        page.ClearNext(page.Version);

        while (next is not null)
        {
            var current = next;
            next = current.Next;
            current.SetLength(0, current.Version);
            current.Unpin(current.Version);
        }

        _writePage = _tail = page;
        _totalLength = _readIndex + length;
    }

    /// <summary>
    /// Resets this instance, returning all memory, and starts again as the writer was created, with its expected length.
    /// </summary>
    public void Reset()
    {
        ThrowIfDisposed();

        ReleaseAll();
        _hasPinnedPages = false;
        _pinnedBuffer = default;
        _totalLength = _readIndex = 0;
        _readPage = _writePage = _tail = RentPage(_firstPageSize);
        Debug.Assert(_readPage.ReferenceCount == 0);
        _readPage.Pin(_readPage.Version);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseAll();
        _hasPinnedPages = false;
        _pinnedBuffer = default;
        _totalLength = _readIndex = 0;
        _readPage = _writePage = _tail = null!;
        _disposed = true;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sizeHint"/> exceeds the largest large page (1 MiB), which no page holds.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ThrowIfDisposed();
        ThrowIfPinnedPages();

        // A hint which the write page holds stays on it, so no empty page is left in the chain.
        if (Math.Max(sizeHint, 1) > _writePage.WriteCapacity)
        {
            return GetMemorySlow(sizeHint);
        }

        return _writePage.WritableMemory;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sizeHint"/> exceeds the largest large page (1 MiB), which no page holds.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        ThrowIfDisposed();
        ThrowIfPinnedPages();

        // A hint which the write page holds stays on it, so no empty page is left in the chain.
        if (Math.Max(sizeHint, 1) > _writePage.WriteCapacity)
        {
            return GetSpanSlow(sizeHint);
        }

        return _writePage.WritableSpan;
    }

    /// <summary>
    /// Attempts to read the provided number of bytes from the buffer.
    /// </summary>
    /// <param name="destination">The destination, which may be used to hold the requested data if the data needs to be copied.</param>
    /// <returns>A span of either zero length, if the data is unavailable, or at least the requested length if the data is available.</returns>
    public ReadOnlySpan<byte> Peek(scoped in Span<byte> destination)
    {
        ThrowIfDisposed();

        // Single span.
        var firstSpan = _readPage.AsSpan(_readIndex, Math.Min(_readPage.Length - _readIndex, Length));
        if (firstSpan.Length >= destination.Length)
        {
            return firstSpan;
        }

        // Multiple spans. Create a slice without pinning it, since we would be immediately unpinning it.
        _ = Peek(destination);
        return destination;
    }

    /// <summary>Copies the contents of this writer to a span.</summary>
    /// <remarks>This method does not advance the read cursor.</remarks>
    public int Peek(Span<byte> output)
    {
        ThrowIfDisposed();

        var remaining = Math.Min(output.Length, Length);
        var bytesCopied = 0;
        var current = _readPage;
        var offset = _readIndex;
        while (remaining > 0 && current != null)
        {
            var segment = current.AsSpan(offset, Math.Min(current.Length - offset, remaining));
            var copyLength = Math.Min(segment.Length, output.Length);
            bytesCopied += copyLength;
            var slice = segment[..copyLength];
            slice.CopyTo(output);
            output = output[slice.Length..];
            remaining -= slice.Length;
            current = current.Next;
            offset = 0;
        }

        return bytesCopied;
    }

    /// <summary>
    /// Gets the contiguous unread bytes in the current read page.
    /// </summary>
    internal ReadOnlySpan<byte> UnreadSpan
    {
        get
        {
            ThrowIfDisposed();
            if (Length == 0)
            {
                return default;
            }

            return _readPage.AsSpan(_readIndex, Math.Min(_readPage.Length - _readIndex, Length));
        }
    }

    /// <summary>
    /// Peeks at a byte at the specified offset into the unread data.
    /// </summary>
    internal bool TryPeek(long offset, out byte value)
    {
        ThrowIfDisposed();

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (offset >= Length)
        {
            value = default;
            return false;
        }

        Debug.Assert(offset <= int.MaxValue);
        var remainingOffset = (int)offset;
        var current = _readPage;
        var pageOffset = _readIndex;
        while (current is not null)
        {
            var available = current.Length - pageOffset;
            if (remainingOffset < available)
            {
                value = current.AsSpan(pageOffset + remainingOffset, 1)[0];
                return true;
            }

            remainingOffset -= available;
            current = current.Next;
            pageOffset = 0;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Returns the offset of the first occurrence of <paramref name="value"/> in the unread data, or -1 if it is not found.
    /// </summary>
    internal int IndexOf(byte value)
    {
        ThrowIfDisposed();

        var result = 0;
        var remaining = Length;
        var current = _readPage;
        var pageOffset = _readIndex;
        while (remaining > 0 && current is not null)
        {
            var span = current.AsSpan(pageOffset, Math.Min(current.Length - pageOffset, remaining));
            var index = span.IndexOf(value);
            if (index >= 0)
            {
                return result + index;
            }

            result += span.Length;
            remaining -= span.Length;
            current = current.Next;
            pageOffset = 0;
        }

        return -1;
    }

    /// <summary>
    /// Returns the offset of the first occurrence of any of <paramref name="values"/> in the unread data, or -1 if none are found.
    /// </summary>
    internal int IndexOfAny(ReadOnlySpan<byte> values)
    {
        ThrowIfDisposed();

        if (values.IsEmpty)
        {
            return -1;
        }

        var result = 0;
        var remaining = Length;
        var current = _readPage;
        var pageOffset = _readIndex;
        while (remaining > 0 && current is not null)
        {
            var span = current.AsSpan(pageOffset, Math.Min(current.Length - pageOffset, remaining));
            for (var i = 0; i < span.Length; i++)
            {
                if (values.IndexOf(span[i]) >= 0)
                {
                    return result + i;
                }
            }

            result += span.Length;
            remaining -= span.Length;
            current = current.Next;
            pageOffset = 0;
        }

        return -1;
    }

    /// <summary>
    /// Writes the provided sequence to this buffer.
    /// </summary>
    /// <param name="input">The data to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySequence<byte> input)
    {
        ThrowIfDisposed();

        foreach (var segment in input)
        {
            Write(segment.Span);
        }
    }

    /// <summary>
    /// Writes the provided value to this buffer.
    /// </summary>
    /// <param name="value">The data to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> value)
    {
        ThrowIfDisposed();

        var destination = GetSpan();

        // Fast path, try copying to the available memory directly
        if (value.Length <= destination.Length)
        {
            value.CopyTo(destination);
            AdvanceWriter(value.Length);
        }
        else
        {
            WriteMultiSegment(value, destination);
        }
    }

    /// <summary>
    /// Appends the pages referenced by <paramref name="input"/> directly to this writer by pinning them.
    /// </summary>
    /// <param name="input">The buffer to append.</param>
    /// <remarks>
    /// The writer must be empty and must not be written to after calling this method. The caller retains ownership
    /// of <paramref name="input"/> and must dispose it independently.
    /// </remarks>
    public void AppendPinned(ArcBuffer input)
    {
        ThrowIfDisposed();

        if (input.Length == 0)
        {
            return;
        }

        ThrowIfPinnedPages();
        if (_totalLength != 0 || _readIndex != 0 || _readPage != _writePage || _writePage != _tail || _readPage.Length != 0)
        {
            throw new InvalidOperationException("Pinned pages can only be appended to an empty writer.");
        }

        var retained = input.Retain();
        ArcBuffer moved = default;
        try
        {
            ArcBufferPage? tail = null;
            foreach (var segment in retained.PageSegments)
            {
                tail = segment.Page;
            }

            Debug.Assert(tail is not null);

            var first = retained.First;
            var readIndex = retained.Offset;
            var totalLength = checked(readIndex + retained.Length);
            moved = retained.Move();
            _readPage.Unpin(_readPage.Version);
            _readPage = first;
            _writePage = _tail = tail;
            _readIndex = readIndex;
            _totalLength = totalLength;
            _pinnedBuffer = moved;
            _hasPinnedPages = true;
        }
        catch
        {
            moved.Dispose();
            retained.Dispose();
            throw;
        }
    }

    private void WriteMultiSegment(in ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var input = source;
        while (true)
        {
            var writeSize = Math.Min(destination.Length, input.Length);
            input[..writeSize].CopyTo(destination);
            AdvanceWriter(writeSize);
            input = input[writeSize..];
            if (input.Length > 0)
            {
                destination = GetSpan();

                continue;
            }

            return;
        }
    }

    /// <summary>
    /// Unpins all pages.
    /// </summary>
    private void ReleaseAll()
    {
        if (_hasPinnedPages)
        {
            _pinnedBuffer.Dispose();
            return;
        }

        UnpinAll();
    }

    /// <summary>
    /// Unpins all pages owned directly by this writer.
    /// </summary>
    private void UnpinAll()
    {
        var current = _readPage;
        while (current != null)
        {
            var previous = current;
            current = ReferenceEquals(previous, _tail) ? null : previous.Next;
            previous.Unpin(previous.Version);
        }
    }

    /// <summary>
    /// Returns a pinned slice of the provided length without marking the data referred to it as consumed.
    /// </summary>
    /// <param name="count">The number of bytes to consume.</param>
    /// <returns>
    /// A pinned slice of unconsumed data. The caller owns the returned buffer and must dispose it.
    /// </returns>
    public ArcBuffer PeekSlice(int count)
    {
        ThrowIfDisposed();

#if NET6_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Length);
#else
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Length must be greater than or equal to 0.");
        if (count > Length) throw new ArgumentOutOfRangeException(nameof(count), "Length must be less than or equal to the unconsumed length of the buffer.");
#endif
        Debug.Assert(count >= 0);
        Debug.Assert(count <= Length);

        // The read index can rest at the end of a page which has since stopped being the write page; a slice
        // starts on the following page.
        AdvanceReader(0);
        return new ArcBuffer(_readPage, token: _readPage.Version, offset: _readIndex, count);
    }

    /// <summary>
    /// Consumes a slice of the provided length.
    /// </summary>
    /// <param name="count">The number of bytes to consume.</param>
    /// <returns>
    /// A pinned buffer representing the consumed data. The caller owns the returned buffer and must dispose it.
    /// </returns>
    public ArcBuffer ConsumeSlice(int count)
    {
        ThrowIfDisposed();

        var result = PeekSlice(count);

        // Advance the cursor so that subsequent slice calls will return the next slice.
        AdvanceReader(count);

        return result;
    }

    /// <summary>
    /// Advances the reader by the specified number of bytes.
    /// </summary>
    /// <param name="count">The number of bytes to advance the reader.</param>
    public void AdvanceReader(int count)
    {
        ThrowIfDisposed();

#if NET6_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Length);
#else
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Count must be greater than or equal to 0.");
        if (count > Length) throw new ArgumentOutOfRangeException(nameof(count), "Count must be less than or equal to the unconsumed length of the buffer.");
#endif

        _readIndex += count;

        // If this call would consume the entire first page and the page is not the current write page, unpin it.
        while (_readIndex >= _readPage.Length && _writePage != _readPage)
        {
            // Advance the consumed length.
            var current = _readPage;
            _readIndex -= current.Length;
            _totalLength -= current.Length;

            // Advance to the next page
            Debug.Assert(current.Next is not null);
            _readPage = current.Next;

            // A retained owner keeps imported pages alive until this writer is disposed or reset.
            if (!_hasPinnedPages)
            {
                current.Unpin(current.Version);
            }
        }

        Debug.Assert(_readPage is not null);
        Debug.Assert(_readIndex <= _readPage.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Span<byte> GetSpanSlow(int sizeHint) => AdvanceWritePage(sizeHint).WritableSpan;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Memory<byte> GetMemorySlow(int sizeHint) => AdvanceWritePage(sizeHint).WritableMemory;

    private ArcBufferPage AllocatePage(int sizeHint)
    {
        ThrowIfPinnedPages();
        Debug.Assert(_tail.Next is null);

        var newBuffer = RentPage(Math.Max(sizeHint, _laterPageMinimumSize));
        Debug.Assert(newBuffer.ReferenceCount == 0);
        newBuffer.Pin(newBuffer.Version);
        _tail.SetNext(newBuffer, _tail.Version);
        return _tail = newBuffer;
    }

    private ArcBufferPage RentPage(int sizeHint)
    {
        if (!_aligned)
        {
            return ArcBufferPagePool.Shared.Rent(sizeHint);
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeHint, AlignedPageSize);
        return ArcBufferPagePool.Shared.RentAligned();
    }

    private ArcBufferPage AdvanceWritePage(int sizeHint)
    {
        // No page holds more than the largest large page.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeHint, ArcBufferPagePool.LargestPageSize);
        var next = _writePage.Next;
        next ??= AllocatePage(sizeHint);

        _writePage = next;
        return next;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ArcBufferWriter));
        }
    }

    private void ThrowIfPinnedPages()
    {
        if (_hasPinnedPages)
        {
            throw new InvalidOperationException("This writer references pages pinned from another buffer and cannot be written to.");
        }
    }
}

internal sealed class ArcBufferPagePool
{
    private const int MaximumRetainedLargePagesPerBucket = 1;

    public static ArcBufferPagePool Shared { get; } = new();

    public const int MinimumPageSize = 1024;

    /// <summary>
    /// The capacity of the largest large page, which caps a writer's capacity and its size hints.
    /// </summary>
    public const int LargestPageSize = 1024 * 1024;

    /// <summary>
    /// The size and address alignment of pages in the aligned size class.
    /// </summary>
    public const int AlignedPageSize = 4096;

    // Each pinned slab holds this many aligned pages, plus one page of slack to reach alignment.
    private const int AlignedPagesPerSlab = 256;

    private readonly ConcurrentQueue<ArcBufferPage> _pages = new();
    private readonly ConcurrentQueue<ArcBufferPage> _alignedPages = new();
    private readonly ConcurrentDictionary<int, LargePageBucket> _largePages = new();
    private readonly Lock _slabLock = new();

    internal ArcBufferPagePool() { }

    /// <summary>
    /// Gets the capacities of the large pages which have returned to this pool, each of which keeps a bucket.
    /// </summary>
    internal ICollection<int> LargePageCapacities => _largePages.Keys;

    /// <summary>
    /// Rents a page from the aligned size class: <see cref="AlignedPageSize"/> bytes starting at an address
    /// aligned to <see cref="AlignedPageSize"/>, inside a slab on the pinned object heap.
    /// </summary>
    public ArcBufferPage RentAligned()
    {
        if (!_alignedPages.TryDequeue(out var page))
        {
            lock (_slabLock)
            {
                page = _alignedPages.TryDequeue(out var dequeued) ? dequeued : AllocateAlignedSlab();
            }
        }

        page.OnRented();
        return page;
    }

    // Slabs live for the process: the pool retains every page carved from them, so the GC never frees or moves
    // memory which storage may have registered with the kernel.
    private ArcBufferPage AllocateAlignedSlab()
    {
        var slab = GC.AllocateUninitializedArray<byte>((AlignedPagesPerSlab + 1) * AlignedPageSize, pinned: true);
        var address = Marshal.UnsafeAddrOfPinnedArrayElement(slab, 0);
        var origin = (int)((AlignedPageSize - (address & (AlignedPageSize - 1))) & (AlignedPageSize - 1));
        var first = ArcBufferPage.CreateAligned(slab, origin);
        for (var index = 1; index < AlignedPagesPerSlab; index++)
        {
            _alignedPages.Enqueue(ArcBufferPage.CreateAligned(slab, origin + index * AlignedPageSize));
        }

        return first;
    }

    public ArcBufferPage Rent(int size = -1)
    {
        Debug.Assert(size <= LargestPageSize);
        ArcBufferPage? block = null;
        if (size <= MinimumPageSize)
        {
            _ = _pages.TryDequeue(out block);
        }
        else
        {
            var bucketSize = checked((int)BitOperations.RoundUpToPowerOf2((uint)size));
            if (_largePages.TryGetValue(bucketSize, out var pages)
                && pages.TryRent(out block))
            {
                block.ResizeLargeSegment(size);
            }
        }

        block ??= new ArcBufferPage(size);
        block.OnRented();
        return block;
    }

    internal void Return(ArcBufferPage block)
    {
        Debug.Assert(block.IsValid);
        block.Poison();
        block.OnReturned();
        if (block.IsAligned)
        {
            _alignedPages.Enqueue(block);
        }
        else if (block.IsMinimumSize)
        {
            _pages.Enqueue(block);
        }
        else
        {
            var bucket = _largePages
                .GetOrAdd(
                    block.Array.Length,
                    static _ => new LargePageBucket());
            if (!bucket.TryReturn(block))
            {
                block.ReleaseLargeSegment();
            }
        }
    }

    private sealed class LargePageBucket
    {
        private readonly ConcurrentQueue<ArcBufferPage> _pages = new();
        private int _count;

        internal bool TryRent([NotNullWhen(true)] out ArcBufferPage? page)
        {
            if (_pages.TryDequeue(out page))
            {
                _ = Interlocked.Decrement(ref _count);
                return true;
            }

            return false;
        }

        internal bool TryReturn(ArcBufferPage page)
        {
            if (Interlocked.Increment(ref _count) > MaximumRetainedLargePagesPerBucket)
            {
                _ = Interlocked.Decrement(ref _count);
                return false;
            }

            _pages.Enqueue(page);
            return true;
        }
    }
}

/// <summary>
/// A page of data.
/// </summary>
public sealed class ArcBufferPage
{
    private readonly bool _isExternal;

    // The offset into Array at which this page begins. Only aligned pages, which are windows of a shared slab,
    // have a non-zero origin.

    // The current version of the page. Each time the page is return to the pool, the version is incremented.
    // This helps to ensure that the page is not consumed after it has been returned to the pool.
    // This is a guard against certain programming bugs.
    private int _version;

    // The current reference count. This is used to ensure that a page is not returned to the pool while it is still in use.
    private int _refCount;

    // The accounting the page was rented under, which its return reaches (BUF-I1, BUF-I2).
    private ArcBufferPageAccounting? _accounting;

    internal ArcBufferPage()
    {
        Array = [];
    }

    internal ArcBufferPage(int length)
    {
#if !NET6_0_OR_GREATER
        Array = null!;
#endif
        InitializeArray(length);
    }

    internal ArcBufferPage(byte[] array, int length)
    {
        Array = array;
        Length = length;
        _isExternal = true;
    }

    private ArcBufferPage(byte[] slab, int origin, bool isAligned)
    {
        Debug.Assert(isAligned);
        Array = slab;
        Origin = origin;
        IsAligned = isAligned;
    }

    internal static ArcBufferPage CreateAligned(byte[] slab, int origin) => new(slab, origin, isAligned: true);

    /// <summary>
    /// The byte which fills each pooled page as it returns to its pool in Debug builds.
    /// </summary>
    internal const byte PoisonByte = 0xDD;

    /// <summary>
    /// In Debug builds, fills the page with <see cref="PoisonByte"/> as it returns to its pool, so a read through a
    /// sequence or stream after its buffer's release sees the poison rather than plausible bytes (conventions.md §1).
    /// </summary>
    [Conditional("DEBUG")]
    internal void Poison() => AsSpan(0).Fill(PoisonByte);

    public void ResizeLargeSegment(int length)
    {
        Debug.Assert(length > ArcBufferPagePool.MinimumPageSize);
        if (_isExternal)
        {
            throw new InvalidOperationException("An externally backed page cannot be resized.");
        }

        InitializeArray(length);
    }

    internal void ReleaseLargeSegment()
    {
        Debug.Assert(!_isExternal);
        Debug.Assert(!IsMinimumSize);
        Debug.Assert(ReferenceCount == 0);
        ArrayPool<byte>.Shared.Return(Array);
        Array = [];
        Length = 0;
    }

#if NET6_0_OR_GREATER
    [MemberNotNull(nameof(Array))]
#endif
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InitializeArray(int length)
    {
        if (length <= ArcBufferPagePool.MinimumPageSize)
        {
            Debug.Assert(Array is null);
#if NET6_0_OR_GREATER
            var array = GC.AllocateUninitializedArray<byte>(ArcBufferPagePool.MinimumPageSize, pinned: true);
#else
                var array = new byte[ArcBufferPagePool.MinimumPageSize];
#endif
            Array = array;
        }
        else
        {
            // Round up to a power of two.
            length = (int)BitOperations.RoundUpToPowerOf2((uint)length);

            if (Array is not null)
            {
                // The segment has an appropriate size already.
                if (Array.Length == length)
                {
                    return;
                }

                // The segment is being resized.
                ArrayPool<byte>.Shared.Return(Array);
            }

            Array = ArrayPool<byte>.Shared.Rent(length);
        }
    }

    /// <summary>
    /// Gets the array underpinning the page.
    /// </summary>
    /// <remarks>
    /// The page occupies <see cref="Capacity"/> bytes of the array starting at <see cref="Origin"/>.
    /// </remarks>
    public byte[] Array { get; private set; }

    /// <summary>
    /// Gets the offset into <see cref="Array"/> at which the page begins.
    /// </summary>
    public int Origin { get; }

    /// <summary>
    /// Gets the number of bytes the page can hold.
    /// </summary>
    public int Capacity => IsAligned ? ArcBufferPagePool.AlignedPageSize : Array.Length;

    /// <summary>
    /// Gets a value indicating whether this page belongs to the aligned size class: its
    /// <see cref="Capacity"/> bytes start at an address aligned to <see cref="ArcBufferWriter.AlignedPageSize"/>
    /// and never move.
    /// </summary>
    public bool IsAligned { get; }

    /// <summary>
    /// Gets the number of bytes which have been written to the page.
    /// </summary>
    public int Length { get; private set; }

    /// <summary>
    /// Gets a <see cref="ReadOnlySpan{T}"/> containing the readable bytes from this page.
    /// </summary>
    public ReadOnlySpan<byte> ReadableSpan => AsSpan(0, Length);

    /// <summary>
    /// Gets a <see cref="ReadOnlyMemory{T}"/> containing the readable bytes from this page.
    /// </summary>
    public ReadOnlyMemory<byte> ReadableMemory => AsMemory(0, Length);

    /// <summary>
    /// Gets an <see cref="ArraySegment{T}"/> containing the readable bytes from this page.
    /// </summary>
    public ArraySegment<byte> ReadableArraySegment => new(Array, Origin, Length);

    /// <summary>
    /// Gets an <see cref="ArraySegment{T}"/> containing the writable bytes from this page.
    /// </summary>
    public ArraySegment<byte> WritableArraySegment => new(Array, Origin + Length, Capacity - Length);

    /// <summary>
    /// Gets the next node.
    /// </summary>
    public ArcBufferPage? Next { get; private set; }

    /// <summary>
    /// Gets the current page version.
    /// </summary>
    public int Version => _version;

    /// <summary>
    /// Gets a value indicating whether this page is valid.
    /// </summary>
    public bool IsValid => Array is { Length: > 0 };

    /// <summary>
    /// Gets a value indicating whether this page is equal to the minimum page size.
    /// </summary>
    public bool IsMinimumSize => !_isExternal && !IsAligned && Array.Length == ArcBufferPagePool.MinimumPageSize;

    /// <summary>
    /// Gets the number of bytes in the page which are available for writing.
    /// </summary>
    public int WriteCapacity => Capacity - Length;

    /// <summary>
    /// Gets the writable memory in the page.
    /// </summary>
    public Memory<byte> WritableMemory => AsMemory(Length);

    /// <summary>
    /// Gets a span representing the writable memory in the page.
    /// </summary>
    public Span<byte> WritableSpan => AsSpan(Length);

    /// <summary>
    /// Gets the current page reference count.
    /// </summary>
    internal int ReferenceCount => _refCount;

    /// <summary>
    /// Creates a new memory region over the portion of the target page beginning at a specified position.
    /// </summary>
    /// <param name="offset">The offset into the array to return memory from.</param>
    /// <returns>The memory region.</returns>
    public Memory<byte> AsMemory(int offset)
    {
#if NET6_0_OR_GREATER
        if (IsMinimumSize || IsAligned)
        {
            return MemoryMarshal.CreateFromPinnedArray(Array, Origin + offset, Capacity - offset);
        }
#endif

        return Array.AsMemory(offset);
    }

    /// <summary>
    /// Creates a new memory region over the portion of the target page beginning at a specified position with a specified length.
    /// </summary>
    /// <param name="offset">The offset into the array that the memory region starts from.</param>
    /// <param name="length">The length of the memory region.</param>
    /// <returns>The memory region.</returns>
    public Memory<byte> AsMemory(int offset, int length)
    {
#if NET6_0_OR_GREATER
        if (IsMinimumSize || IsAligned)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)offset + (uint)length, (uint)Capacity);
            return MemoryMarshal.CreateFromPinnedArray(Array, Origin + offset, length);
        }
#endif

        return Array.AsMemory(offset, length);
    }

    /// <summary>
    /// Returns an array segment pointing to the underlying array, starting from the provided offset, and having the provided length.
    /// </summary>
    /// <param name="offset">The offset into the array that the array segment starts from.</param>
    /// <param name="length">The length of the array segment.</param>
    /// <returns>The array segment.</returns>
    public ArraySegment<byte> AsArraySegment(int offset, int length) => new(Array, Origin + offset, length);

    /// <summary>
    /// Gets a span pointing to the underlying array, starting from the provided offset.
    /// </summary>
    /// <param name="offset">The offset.</param>
    /// <returns>The span.</returns>
    public Span<byte> AsSpan(int offset) => Array.AsSpan(Origin, Capacity)[offset..];

    /// <summary>
    /// Gets a span pointing to the underlying array, starting from the provided offset.
    /// </summary>
    /// <param name="offset">The offset.</param>
    /// <param name="length">The length.</param>
    /// <returns>The span.</returns>
    public Span<byte> AsSpan(int offset, int length) => Array.AsSpan(Origin, Capacity).Slice(offset, length);

    /// <summary>
    /// Increases the number of bytes written to the page by the provided amount.
    /// </summary>
    /// <param name="bytes">The number of bytes to increase the length of this page by.</param>
    public void Advance(int bytes)
    {
        Debug.Assert(bytes >= 0, "Advance called with negative bytes");
        Length += bytes;
        Debug.Assert(Length <= Capacity);
    }

    internal void SetLength(int length, int token)
    {
        CheckValidity(token);
        Debug.Assert(length >= 0);
        Debug.Assert(length <= Capacity);
        Length = length;
    }

    /// <summary>
    /// Sets the next page in the sequence.
    /// </summary>
    /// <param name="next">The next page in the sequence.</param>
    /// <param name="token">The token, which must match the page's <see cref="Version"/> for this operation to be allowed.</param>
    public void SetNext(ArcBufferPage next, int token)
    {
        Debug.Assert(Next is null);
        CheckValidity(token);
        Debug.Assert(next is not null, "SetNext called with null next page");
        Debug.Assert(next != this, "SetNext called with self as next page");
        Next = next;
    }

    internal void ClearNext(int token)
    {
        CheckValidity(token);
        Next = null;
    }

    /// <summary>
    /// Pins this page to prevent it from being returned to the page pool.
    /// </summary>
    /// <param name="token">The token, which must match the page's <see cref="Version"/> for this operation to be allowed.</param>
    public void Pin(int token)
    {
        if (token != _version)
        {
            ThrowInvalidVersion();
        }

        _ = Interlocked.Increment(ref _refCount);
    }

    /// <summary>
    /// Unpins this page, allowing it to be returned to the page pool.
    /// </summary>
    /// <param name="token">The token, which must match the page's <see cref="Version"/> for this operation to be allowed.</param>
    public void Unpin(int token)
    {
        CheckValidity(token);
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            Return();
        }
    }

    private void Return()
    {
        Debug.Assert(_refCount == 0);
        Length = 0;
        Next = default;
        _ = Interlocked.Increment(ref _version);
        if (_isExternal)
        {
            Array = [];
            OnReturned();
            return;
        }

        ArcBufferPagePool.Shared.Return(this);
    }

    /// <summary>
    /// Attributes a page which is leaving the pool, or wrapping an array, to the accounting current in the renter's
    /// execution context.
    /// </summary>
    internal void OnRented()
    {
        Debug.Assert(_refCount == 0 && _accounting is null);
        if (ArcBufferPageAccounting.Current is { } accounting)
        {
            _accounting = accounting;
            accounting.OnRented(this);
        }
    }

    /// <summary>
    /// Reports a page which is returning to the pool, or releasing its array, to the accounting it was rented under.
    /// </summary>
    internal void OnReturned()
    {
        if (_accounting is { } accounting)
        {
            _accounting = null;
            accounting.OnReturned(this);
        }
    }

    /// <summary>
    /// Throws if the provided <paramref name="token"/> does not match the page's <see cref="Version"/>.
    /// </summary>
    /// <param name="token">The token, which must match the page's <see cref="Version"/>.</param>
    public void CheckValidity(int token)
    {
        if (token != _version)
        {
            ThrowInvalidVersion();
        }

        if (_refCount <= 0)
        {
            ThrowAccessViolation();
        }
    }

    [DoesNotReturn]
    private static void ThrowInvalidVersion() => throw new InvalidOperationException("An invalid token was provided when attempting to perform an operation on this page.");

    [DoesNotReturn]
    private static void ThrowAccessViolation() => throw new InvalidOperationException("An attempt was made to access a page with an invalid reference count.");
}

internal sealed class ArcBufferOwnershipLease
{
    private const int DisposedGeneration = -1;

    private readonly ArcBufferPage _first;
    private readonly int _firstPageToken;
    private readonly int _offset;
    private readonly int _length;
    private SpinLock _sync = new(enableThreadOwnerTracking: false);
    private int _generation;

    private ArcBufferOwnershipLease(ArcBufferPage first, int firstPageToken, int offset, int length)
    {
        _first = first;
        _firstPageToken = firstPageToken;
        _offset = offset;
        _length = length;
    }

    internal static ArcBufferOwnershipLease CreateRetained(
        ArcBufferPage first,
        int firstPageToken,
        int offset,
        int length)
    {
        var result = new ArcBufferOwnershipLease(first, firstPageToken, offset, length);
        PinRange(first, firstPageToken, offset, length);
        return result;
    }

    internal int Move(int generation)
    {
        var lockTaken = false;
        try
        {
            _sync.Enter(ref lockTaken);
            CheckActiveCore(generation, _first, _firstPageToken);
            if (generation == int.MaxValue)
            {
                throw new InvalidOperationException("The ArcBuffer ownership lease has been moved too many times.");
            }

            var nextGeneration = generation + 1;
            if (Interlocked.CompareExchange(ref _generation, nextGeneration, generation) != generation)
            {
                ThrowInactive();
            }

            return nextGeneration;
        }
        finally
        {
            if (lockTaken)
            {
                _sync.Exit();
            }
        }
    }

    internal bool TryDispose(int generation)
    {
        var lockTaken = false;
        try
        {
            _sync.Enter(ref lockTaken);
            if (Interlocked.CompareExchange(ref _generation, DisposedGeneration, generation) != generation)
            {
                return false;
            }

            UnpinRange(_first, _firstPageToken, _offset, _length);
            return true;
        }
        finally
        {
            if (lockTaken)
            {
                _sync.Exit();
            }
        }
    }

    internal void CheckActive(int generation, ArcBufferPage first, int firstPageToken)
    {
        if (Volatile.Read(ref _generation) != generation)
        {
            ThrowInactive();
        }

        first.CheckValidity(firstPageToken);
    }

    internal ArcBuffer CreateRetainedSlice(
        int generation,
        ArcBufferPage first,
        int firstPageToken,
        int sourceOffset,
        int sourceLength,
        int offset,
        int length)
    {
        var lockTaken = false;
        try
        {
            _sync.Enter(ref lockTaken);
            CheckActiveCore(generation, first, firstPageToken);
            GetSliceBounds(
                first,
                firstPageToken,
                sourceOffset,
                sourceLength,
                offset,
                length,
                out var sliceFirst,
                out var sliceFirstPageToken,
                out var sliceOffset);
            var lease = CreateRetained(sliceFirst, sliceFirstPageToken, sliceOffset, length);
            return new ArcBuffer(lease, 0, sliceFirst, sliceFirstPageToken, sliceOffset, length);
        }
        finally
        {
            if (lockTaken)
            {
                _sync.Exit();
            }
        }
    }

    internal ArcBuffer CreateAliasSlice(
        int generation,
        ArcBufferPage first,
        int firstPageToken,
        int sourceOffset,
        int sourceLength,
        int offset,
        int length)
    {
        var lockTaken = false;
        try
        {
            _sync.Enter(ref lockTaken);
            CheckActiveCore(generation, first, firstPageToken);
            GetSliceBounds(
                first,
                firstPageToken,
                sourceOffset,
                sourceLength,
                offset,
                length,
                out var sliceFirst,
                out var sliceFirstPageToken,
                out var sliceOffset);
            return new ArcBuffer(this, generation, sliceFirst, sliceFirstPageToken, sliceOffset, length);
        }
        finally
        {
            if (lockTaken)
            {
                _sync.Exit();
            }
        }
    }

    private void CheckActiveCore(int generation, ArcBufferPage first, int firstPageToken)
    {
        if (_generation != generation)
        {
            ThrowInactive();
        }

        first.CheckValidity(firstPageToken);
    }

    private static void GetSliceBounds(
        ArcBufferPage first,
        int firstPageToken,
        int sourceOffset,
        int sourceLength,
        int offset,
        int length,
        out ArcBufferPage sliceFirst,
        out int sliceFirstPageToken,
        out int sliceOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, sourceLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, sourceLength - offset);

        first.CheckValidity(firstPageToken);
        if (sourceOffset + offset < first.Length || length == 0)
        {
            sliceFirst = first;
            sliceFirstPageToken = firstPageToken;
            sliceOffset = sourceOffset + offset;
            return;
        }

        offset -= first.Length - sourceOffset;
        var page = first.Next ?? throw new InvalidOperationException("Buffer page chain ended unexpectedly.");
        while (offset >= page.Length)
        {
            offset -= page.Length;
            page = page.Next ?? throw new InvalidOperationException("Buffer page chain ended unexpectedly.");
        }

        sliceFirst = page;
        sliceFirstPageToken = page.Version;
        sliceOffset = offset;
    }

    private static void PinRange(ArcBufferPage first, int firstPageToken, int offset, int length)
    {
        var pinnedCount = 0;
        try
        {
            if (length == 0)
            {
                first.Pin(firstPageToken);
                return;
            }

            var page = first;
            var pageOffset = offset;
            var remaining = length;
            var token = firstPageToken;
            while (remaining > 0)
            {
                page.Pin(token);
                pinnedCount++;
                var segmentLength = Math.Min(remaining, page.Length - pageOffset);
                if (segmentLength <= 0)
                {
                    throw new InvalidOperationException("Buffer page chain ended unexpectedly.");
                }

                remaining -= segmentLength;
                if (remaining == 0)
                {
                    return;
                }

                page = page.Next ?? throw new InvalidOperationException("Buffer page chain ended unexpectedly.");
                pageOffset = 0;
                token = page.Version;
            }
        }
        catch
        {
            UnpinPagePrefix(first, firstPageToken, pinnedCount);
            throw;
        }
    }

    private static void UnpinRange(ArcBufferPage first, int firstPageToken, int offset, int length)
    {
        if (length == 0)
        {
            first.Unpin(firstPageToken);
            return;
        }

        var page = first;
        var pageOffset = offset;
        var remaining = length;
        var token = firstPageToken;
        while (remaining > 0)
        {
            var segmentLength = Math.Min(remaining, page.Length - pageOffset);
            if (segmentLength <= 0)
            {
                throw new InvalidOperationException("Buffer page chain ended unexpectedly.");
            }

            remaining -= segmentLength;
            var next = remaining > 0
                ? page.Next ?? throw new InvalidOperationException("Buffer page chain ended unexpectedly.")
                : null;
            page.Unpin(token);
            if (next is null)
            {
                return;
            }

            page = next;
            pageOffset = 0;
            token = page.Version;
        }
    }

    private static void UnpinPagePrefix(ArcBufferPage first, int firstPageToken, int count)
    {
        var page = first;
        var token = firstPageToken;
        for (var index = 0; index < count; index++)
        {
            var next = page.Next;
            page.Unpin(token);
            if (next is null)
            {
                return;
            }

            page = next;
            token = page.Version;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInactive() =>
        throw new InvalidOperationException("The ArcBuffer ownership lease has already been moved or released.");
}

/// <summary>
/// Provides reader access to an <see cref="ArcBufferWriter"/>.
/// </summary>
public readonly struct ArcBufferReader
{
    private readonly ArcBufferWriter _writer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArcBufferReader"/> struct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    internal ArcBufferReader(ArcBufferWriter writer)
    {
        _writer = writer;
    }

    /// <summary>
    /// Gets the number of unconsumed bytes.
    /// </summary>
    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _writer.Length;
    }

    /// <summary>
    /// Attempts to read the provided number of bytes from the buffer.
    /// </summary>
    /// <param name="destination">The destination, which may be used to hold the requested data if the data needs to be copied.</param>
    /// <returns>A span of either zero length, if the data is unavailable, or the requested length if the data is available.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> Peek(scoped in Span<byte> destination) => _writer.Peek(in destination);

    /// <summary>
    /// Peeks at a byte at the specified offset into the unread data.
    /// </summary>
    /// <param name="offset">The offset into the unread data.</param>
    /// <returns>The byte at the specified offset.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Peek(long offset)
    {
        if (!_writer.TryPeek(offset, out var value))
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must be within the unread data.");
        }

        return value;
    }

    /// <summary>
    /// Returns a pinned slice of the provided length without marking the data referred to it as consumed.
    /// </summary>
    /// <param name="count">The number of bytes to consume.</param>
    /// <returns>
    /// A pinned slice of unconsumed data. The caller owns the returned buffer and must dispose it.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArcBuffer PeekSlice(int count) => _writer.PeekSlice(count);

    /// <summary>
    /// Consumes a slice of the provided length.
    /// </summary>
    /// <param name="count">The number of bytes to consume.</param>
    /// <returns>
    /// A pinned buffer representing the consumed data. The caller owns the returned buffer and must dispose it.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArcBuffer ConsumeSlice(int count) => _writer.ConsumeSlice(count);

    /// <summary>
    /// Reads bytes until <paramref name="delimiter"/> is found.
    /// </summary>
    /// <param name="slice">
    /// The pinned bytes before the delimiter, if it was found. The caller owns the returned buffer and must dispose it.
    /// </param>
    /// <param name="delimiter">The delimiter to search for.</param>
    /// <param name="advancePastDelimiter">Whether to advance past the delimiter when it is found.</param>
    /// <returns><see langword="true"/> if the delimiter was found; otherwise, <see langword="false"/>.</returns>
    public bool TryReadTo(out ArcBuffer slice, byte delimiter, bool advancePastDelimiter = true)
    {
        var index = _writer.IndexOf(delimiter);
        if (index < 0)
        {
            slice = default;
            return false;
        }

        slice = _writer.ConsumeSlice(index);
        if (advancePastDelimiter)
        {
            _writer.AdvanceReader(1);
        }

        return true;
    }

    /// <summary>
    /// Checks whether the next bytes match <paramref name="next"/>.
    /// </summary>
    /// <param name="next">The bytes to compare to the next bytes.</param>
    /// <param name="advancePast">Whether to advance past the bytes if they match.</param>
    /// <returns><see langword="true"/> if the next bytes match; otherwise, <see langword="false"/>.</returns>
    public bool IsNext(ReadOnlySpan<byte> next, bool advancePast = false)
    {
        if (next.Length > _writer.Length)
        {
            return false;
        }

        for (var i = 0; i < next.Length; i++)
        {
            if (!_writer.TryPeek(i, out var value) || value != next[i])
            {
                return false;
            }
        }

        if (advancePast)
        {
            _writer.AdvanceReader(next.Length);
        }

        return true;
    }

    /// <summary>
    /// Consumes the amount of data present in the span.
    /// </summary>
    /// <param name="output">The destination to copy the consumed data into.</param>
    public void Consume(Span<byte> output)
    {
        var count = _writer.Peek(output);
        if (count != output.Length)
        {
            throw new InvalidOperationException("Attempted to consume more data than is available.");
        }

        _writer.AdvanceReader(count);
    }

    /// <summary>
    /// Advances the reader by the specified number of bytes.
    /// </summary>
    /// <param name="count">The number of bytes to advance.</param>
    public void Skip(int count) => _writer.AdvanceReader(count);
}

/// <summary>
/// Represents a slice of a <see cref="ArcBufferWriter"/>.
/// </summary>
/// <remarks>
/// The default value is the empty payload: it owns nothing, every member which reads bytes answers as for an empty
/// buffer, <see cref="Retain"/>, <see cref="Move"/>, and a slice of <c>(0, 0)</c> return the default value, and
/// <see cref="Dispose"/> does nothing. <see cref="First"/> and <see cref="Unpin"/> throw, since it has no page and no
/// lease.
/// </remarks>
public readonly struct ArcBuffer : IDisposable
{
    private readonly ArcBufferOwnershipLease? _lease;
    private readonly int _leaseGeneration;
    private readonly ArcBufferPage _first;
    private readonly int _firstPageToken;
    private readonly int _offset;
    private readonly int _length;

    internal ArcBuffer(ArcBufferPage first, int token, int offset, int length)
    {
        _lease = ArcBufferOwnershipLease.CreateRetained(first, token, offset, length);
        _leaseGeneration = 0;
        _first = first;
        _firstPageToken = token;
        _offset = offset;
        _length = length;
    }

    internal ArcBuffer(
        ArcBufferOwnershipLease lease,
        int leaseGeneration,
        ArcBufferPage first,
        int firstPageToken,
        int offset,
        int length)
    {
        _lease = lease;
        _leaseGeneration = leaseGeneration;
        _first = first;
        _firstPageToken = firstPageToken;
        _offset = offset;
        _length = length;
    }

    /// <summary>
    /// Creates an owned, zero-copy view over array-backed memory.
    /// </summary>
    /// <param name="memory">The array-backed memory to wrap.</param>
    /// <returns>
    /// A buffer which keeps the underlying array alive until it is disposed, or the default value, which owns nothing,
    /// when <paramref name="memory"/> is empty.
    /// </returns>
    public static ArcBuffer Wrap(ReadOnlyMemory<byte> memory)
    {
        if (memory.IsEmpty)
        {
            return default;
        }

        if (!MemoryMarshal.TryGetArray(memory, out var segment) || segment.Array is null)
        {
            throw new ArgumentException("The memory to wrap must be array-backed.", nameof(memory));
        }

        var page = new ArcBufferPage(segment.Array, checked(segment.Offset + segment.Count));
        page.OnRented();
        return new ArcBuffer(page, page.Version, segment.Offset, segment.Count);
    }

    /// <summary>
    /// Gets a value indicating whether this is the default value, which owns nothing.
    /// </summary>
    /// <remarks>
    /// Only tests use it: code treats the default value as any other zero-length buffer (buffers.md §2).
    /// </remarks>
    internal bool IsDefault => _lease is null;

    /// <summary>
    /// Gets the first page.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is the default value, which has no page.</exception>
    public ArcBufferPage First
    {
        get
        {
            if (_lease is null)
            {
                throw new InvalidOperationException("The default ArcBuffer is empty and has no page.");
            }

            CheckValidity();
            return _first;
        }
    }

    /// <summary>
    /// Gets the offset into the first page at which this slice begins.
    /// </summary>
    public int Offset
    {
        get
        {
            CheckValidity();
            return _offset;
        }
    }

    /// <summary>
    /// Gets the length of this sequence.
    /// </summary>
    public int Length
    {
        get
        {
            CheckValidity();
            return _length;
        }
    }

    /// <summary>Copies the contents of this writer to a span.</summary>
    public readonly int CopyTo(Span<byte> output)
    {
        CheckValidity();
        if (output.Length < Length)
        {
            throw new ArgumentException("Destination span is not large enough to hold the buffer contents.", nameof(output));
        }

        var copied = 0;
        foreach (var span in this)
        {
            var slice = span[..Math.Min(span.Length, output.Length)];
            slice.CopyTo(output);
            output = output[slice.Length..];
            copied += slice.Length;
        }

        return copied;
    }

    /// <summary>Copies the contents of this writer to a pooled buffer.</summary>
    public readonly void CopyTo(ArcBufferWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        CheckValidity();
        foreach (var span in this)
        {
            output.Write(span);
        }
    }

    /// <summary>Copies the contents of this writer to a buffer writer.</summary>
    public readonly void CopyTo<TBufferWriter>(ref TBufferWriter output)
        where TBufferWriter : IBufferWriter<byte>
    {
        CheckValidity();
        foreach (var span in this)
        {
            Write(ref output, span);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write<TBufferWriter>(ref TBufferWriter writer, ReadOnlySpan<byte> value)
        where TBufferWriter : IBufferWriter<byte>
    {
        var destination = writer.GetSpan();

        // Fast path, try copying to the available memory directly
        if (value.Length <= destination.Length)
        {
            value.CopyTo(destination);
            writer.Advance(value.Length);
        }
        else
        {
            WriteMultiSegment(ref writer, value, destination);
        }
    }

    private static void WriteMultiSegment<TBufferWriter>(ref TBufferWriter writer, in ReadOnlySpan<byte> source, Span<byte> destination)
        where TBufferWriter : IBufferWriter<byte>
    {
        var input = source;
        while (true)
        {
            var writeSize = Math.Min(destination.Length, input.Length);
            input[..writeSize].CopyTo(destination);
            writer.Advance(writeSize);
            input = input[writeSize..];
            if (input.Length > 0)
            {
                destination = writer.GetSpan();

                if (destination.IsEmpty)
                {
                    ThrowInsufficientSpaceException();
                }

                continue;
            }

            return;
        }
    }

    [DoesNotReturn]
    private static void ThrowInsufficientSpaceException() => throw new InvalidOperationException("Insufficient capacity in provided buffer");

    /// <summary>
    /// Returns a new <see cref="ReadOnlySequence{T}"/> which must not be accessed after disposing this instance.
    /// </summary>
    public readonly ReadOnlySequence<byte> AsReadOnlySequence()
    {
        if (Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        CheckValidity();

        if (Length <= First.Length - Offset)
        {
            return new ReadOnlySequence<byte>(First.AsMemory(Offset, Length));
        }

        var runningIndex = 0L;
        ReadOnlySequenceSegment? firstSegment = null;
        ReadOnlySequenceSegment? previous = null;
        var endIndex = 0;
        foreach (var memory in MemorySegments)
        {
            var current = new ReadOnlySequenceSegment(memory, runningIndex);
            firstSegment ??= current;
            endIndex = memory.Length;
            runningIndex += endIndex;
            previous?.SetNext(current);
            previous = current;
        }

        if (firstSegment is null)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        Debug.Assert(previous is not null);
        if (previous == firstSegment)
        {
            return new ReadOnlySequence<byte>(firstSegment.Memory);
        }

        return new ReadOnlySequence<byte>(firstSegment, 0, previous, endIndex);
    }

    /// <summary>
    /// Returns the data which has been written as an array.
    /// </summary>
    /// <returns>The data which has been written.</returns>
    public readonly byte[] ToArray()
    {
        CheckValidity();
        var result = new byte[Length];
        _ = CopyTo(result);
        return result;
    }

    /// <summary>
    /// Throws if the buffer is no longer valid. The default value, which owns nothing, is always valid.
    /// </summary>
    private void CheckValidity() => _lease?.CheckActive(_leaseGeneration, _first, _firstPageToken);

    /// <summary>
    /// Moves this buffer's ownership into a new value without incrementing page reference counts.
    /// </summary>
    public ArcBuffer Move()
    {
        if (_lease is null)
        {
            return default;
        }

        var generation = _lease.Move(_leaseGeneration);
        return new ArcBuffer(_lease, generation, _first, _firstPageToken, _offset, _length);
    }

    /// <summary>Creates an independently owned retained view of this buffer.</summary>
    public ArcBuffer Retain() => Slice(0, Length);

    public ArcBuffer Slice(int offset) => Slice(offset, Length - offset);

    public ArcBuffer Slice(int offset, int length)
    {
        if (_lease is null)
        {
            CheckEmptyRange(offset, length);
            return default;
        }

        return _lease.CreateRetainedSlice(
            _leaseGeneration,
            _first,
            _firstPageToken,
            _offset,
            _length,
            offset,
            length);
    }

    public ArcBuffer UnsafeSlice(int offset, int length)
    {
        if (_lease is null)
        {
            CheckEmptyRange(offset, length);
            return default;
        }

        return _lease.CreateAliasSlice(
            _leaseGeneration,
            _first,
            _firstPageToken,
            _offset,
            _length,
            offset,
            length);
    }

    // The only range of the default value is (0, 0).
    private static void CheckEmptyRange(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(offset, 0);
        ArgumentOutOfRangeException.ThrowIfNotEqual(length, 0);
    }

    /// <summary>
    /// Unpins this slice, allowing the referenced pages to be returned to the pool.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// This is the default value, which has no lease, or its lease was already moved or released.
    /// </exception>
    public void Unpin()
    {
        if (_lease is null)
        {
            throw new InvalidOperationException("The default ArcBuffer owns no lease.");
        }

        if (!_lease.TryDispose(_leaseGeneration))
        {
            throw new InvalidOperationException("The ArcBuffer ownership lease has already been moved or released.");
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _ = _lease?.TryDispose(_leaseGeneration);

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the span segments referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly SpanEnumerator GetEnumerator() => new(this);

    /// <summary>
    /// Gets an enumerator which can be used to enumerate the pages referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    internal readonly PageEnumerator Pages => new(this);

    /// <summary>
    /// Gets an enumerator which can be used to enumerate the pages referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    internal readonly PageSegmentEnumerator PageSegments => new(this);

    /// <summary>
    /// Gets an enumerator which can be used to enumerate the span segments referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly SpanEnumerator SpanSegments => new(this);

    /// <summary>
    /// Gets an enumerator which can be used to enumerate the memory segments referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly MemoryEnumerator MemorySegments => new(this);

    /// <summary>
    /// Gets an enumerator which can be used to enumerate the array segments referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly ArraySegmentEnumerator ArraySegments => new(this);

    /// <summary>
    /// Defines a region of data within a page.
    /// </summary>
    public readonly struct PageSegment(ArcBufferPage page, int offset, int length)
    {
        /// <summary>
        /// Gets the page which this segment refers to.
        /// </summary>
        public readonly ArcBufferPage Page = page;

        /// <summary>
        /// Gets the offset into the page at which this region begins.
        /// </summary>
        public readonly int Offset = offset;

        /// <summary>
        /// Gets the length of this region.
        /// </summary>
        public readonly int Length = length;

        /// <summary>
        /// Gets a <see cref="ReadOnlySpan{T}"/> representation of this region.
        /// </summary>
        public readonly ReadOnlySpan<byte> Span => Page.AsSpan(Offset, Length);

        /// <summary>
        /// Gets a <see cref="ReadOnlyMemory{T}"/> representation of this region.
        /// </summary>
        public readonly ReadOnlyMemory<byte> Memory => Page.AsMemory(Offset, Length);

        /// <summary>
        /// Gets an <see cref="ArraySegment{T}"/> representation of this region.
        /// </summary>
        public readonly ArraySegment<byte> ArraySegment => Page.AsArraySegment(Offset, Length);
    }

    /// <summary>
    /// Enumerates over page segments in a <see cref="ArcBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="PageSegmentEnumerator"/> type.
    /// </remarks>
    /// <param name="slice">The buffer to enumerate.</param>
    internal struct PageSegmentEnumerator(ArcBuffer slice) : IEnumerable<PageSegment>, IEnumerator<PageSegment>
    {
#if DEBUG
        /// <summary>
        /// The segments every enumerator on this thread has yielded, which the stream adapter's tests read to bound its
        /// steps (buffers.md §2).
        /// </summary>
        [ThreadStatic]
        internal static long DebugSteps;
#endif

        internal readonly ArcBuffer Slice = slice;
        private int _position;
        private ArcBufferPage? _page = slice.Length > 0 ? slice.First : null;

        internal readonly ArcBufferPage First => Slice.First;

        internal readonly int Offset => Slice.Offset;

        internal readonly int Length => Slice.Length;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly PageSegmentEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public PageSegment Current { get; private set; }

        /// <inheritdoc/>
        readonly object? IEnumerator.Current => Current;

        /// <summary>
        /// Gets a value indicating whether enumeration has completed.
        /// </summary>
        public readonly bool IsCompleted => _page is null || _position == Length;

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            Debug.Assert(_position <= Length, "Enumerator position exceeds slice length");
            if (_page is null || _position == Length)
            {
                Current = default;
                Debug.Assert(_position == Length, "Enumerator ended before reaching full length");
                return false;
            }

            if (_page == First)
            {
                Debug.Assert(_position == 0);
                Slice.CheckValidity();
                var offset = Offset;
                var length = Math.Min(Length, _page.Length - offset);
                Debug.Assert(length >= 0, "Calculated negative length for first segment");
                _position += length;
                Current = new PageSegment(_page, offset, length);
                _page = _page.Next;
                CountStep();
                return true;
            }

            var segmentLength = Math.Min(Length - _position, _page.Length);
            Debug.Assert(segmentLength >= 0, "Calculated negative length for subsequent segment");
            _position += segmentLength;
            Current = new PageSegment(_page, 0, segmentLength);
            _page = _page.Next;
            CountStep();
            return true;
        }

        [Conditional("DEBUG")]
        private static void CountStep()
        {
#if DEBUG
            DebugSteps++;
#endif
        }

        /// <inheritdoc/>
        readonly IEnumerator<PageSegment> IEnumerable<PageSegment>.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        void IEnumerator.Reset()
        {
            _position = 0;
            _page = Slice.Length > 0 ? Slice.First : null;
            Current = default;
        }

        /// <inheritdoc/>
        readonly void IDisposable.Dispose() { }
    }

    /// <summary>
    /// Enumerates over pages in a <see cref="ArcBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="PageEnumerator"/> type.
    /// </remarks>
    /// <param name="slice">The slice to enumerate.</param>
    internal struct PageEnumerator(ArcBuffer slice) : IEnumerable<ArcBufferPage>, IEnumerator<ArcBufferPage?>
    {
        private PageSegmentEnumerator _enumerator = slice.PageSegments;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly PageEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public ArcBufferPage? Current { get; private set; }

        /// <inheritdoc/>
        readonly object? IEnumerator.Current => Current;

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            if (_enumerator.MoveNext())
            {
                Current = _enumerator.Current.Page;
                return true;
            }

            Current = default;
            return false;
        }

        /// <inheritdoc/>
        readonly IEnumerator<ArcBufferPage> IEnumerable<ArcBufferPage>.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        void IEnumerator.Reset()
        {
            _enumerator = _enumerator.Slice.PageSegments;
            Current = default;
        }

        /// <inheritdoc/>
        readonly void IDisposable.Dispose() { }
    }

    /// <summary>
    /// Enumerates over spans of bytes in a <see cref="ArcBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="SpanEnumerator"/> type.
    /// </remarks>
    /// <param name="slice">The slice to enumerate.</param>
    public ref struct SpanEnumerator(ArcBuffer slice)
    {
        private PageSegmentEnumerator _enumerator = slice.PageSegments;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly SpanEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public ReadOnlySpan<byte> Current { get; private set; }

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            if (_enumerator.MoveNext())
            {
                Current = _enumerator.Current.Span;
                return true;
            }

            Current = default;
            return false;
        }
    }

    /// <summary>
    /// Enumerates over sequences of bytes in a <see cref="ArcBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="MemoryEnumerator"/> type.
    /// </remarks>
    /// <param name="slice">The slice to enumerate.</param>
    public struct MemoryEnumerator(ArcBuffer slice) : IEnumerable<ReadOnlyMemory<byte>>, IEnumerator<ReadOnlyMemory<byte>>
    {
        private PageSegmentEnumerator _enumerator = slice.PageSegments;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly MemoryEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public ReadOnlyMemory<byte> Current { get; private set; }

        /// <inheritdoc/>
        readonly object? IEnumerator.Current => Current;

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            if (_enumerator.MoveNext())
            {
                Current = _enumerator.Current.Memory;
                return true;
            }

            Current = default;
            return false;
        }

        /// <inheritdoc/>
        readonly IEnumerator<ReadOnlyMemory<byte>> IEnumerable<ReadOnlyMemory<byte>>.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        void IEnumerator.Reset()
        {
            _enumerator = _enumerator.Slice.PageSegments;
            Current = default;
        }

        /// <inheritdoc/>
        readonly void IDisposable.Dispose() { }
    }

    /// <summary>
    /// Enumerates over array segments in a <see cref="ArcBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="ArraySegmentEnumerator"/> type.
    /// </remarks>
    /// <param name="slice">The slice to enumerate.</param>
    public struct ArraySegmentEnumerator(ArcBuffer slice) : IEnumerable<ArraySegment<byte>>, IEnumerator<ArraySegment<byte>>
    {
        private PageSegmentEnumerator _enumerator = slice.PageSegments;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly ArraySegmentEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public ArraySegment<byte> Current { get; private set; }

        /// <inheritdoc/>
        readonly object? IEnumerator.Current => Current;

        /// <summary>
        /// Gets a value indicating whether enumeration has completed.
        /// </summary>
        public readonly bool IsCompleted => _enumerator.IsCompleted;

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            if (_enumerator.MoveNext())
            {
                Current = _enumerator.Current.ArraySegment;
                return true;
            }

            Current = default;
            return false;
        }

        /// <inheritdoc/>
        readonly IEnumerator<ArraySegment<byte>> IEnumerable<ArraySegment<byte>>.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        void IEnumerator.Reset()
        {
            _enumerator = _enumerator.Slice.PageSegments;
            Current = default;
        }

        /// <inheritdoc/>
        readonly void IDisposable.Dispose() { }
    }

    private sealed class ReadOnlySequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public ReadOnlySequenceSegment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public void SetNext(ReadOnlySequenceSegment next)
        {
            Debug.Assert(Next is null);
            Next = next;
        }
    }
}
