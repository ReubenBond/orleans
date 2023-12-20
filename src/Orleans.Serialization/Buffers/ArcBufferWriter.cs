#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

#if NET6_0_OR_GREATER
using System.Numerics;
#else
using Orleans.Serialization.Utilities;
#endif

namespace Orleans.Serialization.Buffers;

/// <summary>
/// A <see cref="IBufferWriter{T}"/> implementation implemented using pooled arrays which is specialized for creating <see cref="ReadOnlySequence{T}"/> instances.
/// </summary>
[StructLayout(LayoutKind.Auto)]
[Immutable]
public partial struct ArcBufferWriter : IBufferWriter<byte>, IDisposable
{
    // The first page. This is the page which consumers will consume from.
    // This may be equal to the current page, or it may be a previous page.
    private ArcPage _first;

    // The current page. This is the page which will be written to when the next write occurs.
    private ArcPage _current;

    // The offset into the first page which has been consumed already. When this reaches the end of the page, the page can be unpinned.
    private int _consumedLength;

    // The total length of the buffer.
    private int _totalLength;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArcBufferWriter"/> struct.
    /// </summary>
    public ArcBufferWriter()
    {
        _first = _current = ArcBufferPagePool.Shared.Rent();
    }

    /// <summary>
    /// Gets the number of unconsumed bytes.
    /// </summary>
    public readonly int UnconsumedLength => _totalLength - _consumedLength;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int bytes)
    {
        _current.Advance(bytes);
        _totalLength += bytes;
    }

    /// <summary>
    /// Resets this instance, returning all memory.
    /// </summary>
    public void Reset()
    {
        UnpinAll();
        _totalLength = _consumedLength = 0;
        _first = _current = ArcBufferPagePool.Shared.Rent();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        UnpinAll();
        _totalLength = _consumedLength = 0;
        _first = _current = null!;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (sizeHint >= _current.WritableCapacity)
        {
            return GetMemorySlow(sizeHint);
        }

        return _current.WritableMemory;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (sizeHint >= _current.WritableCapacity)
        {
            return GetSpanSlow(sizeHint);
        }

        return _current.WritableSpan;
    }

    /// <summary>Copies the contents of this writer to a span.</summary>
    public readonly void CopyTo(Span<byte> output)
    {
        var current = _first;
        while (output.Length > 0 && current != null)
        {
            var segment = current.ReadableMemory.Span;
            var slice = segment[..Math.Min(segment.Length, output.Length)];
            slice.CopyTo(output);
            output = output[slice.Length..];
            current = current.Next;
        }
    }

    /// <summary>Copies the contents of this writer to another writer.</summary>
    public readonly void CopyTo<TBufferWriter>(ref Writer<TBufferWriter> writer) where TBufferWriter : IBufferWriter<byte>
    {
        var current = _first;
        while (current != null)
        {
            var span = current.ReadableMemory.Span;
            writer.Write(span);
            current = current.Next;
        }
    }

    /// <summary>Copies the contents of this writer to another writer.</summary>
    public readonly void CopyTo<TBufferWriter>(ref TBufferWriter writer) where TBufferWriter : IBufferWriter<byte>
    {
        var current = _first;
        while (current != null)
        {
            var span = current.ReadableMemory.Span;
            writer.Write(span);
            current = current.Next;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write<TBufferWriter>(ref TBufferWriter writer, ReadOnlySpan<byte> value) where TBufferWriter : IBufferWriter<byte>
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

    private static void WriteMultiSegment<TBufferWriter>(ref TBufferWriter writer, in ReadOnlySpan<byte> source, Span<byte> destination) where TBufferWriter : IBufferWriter<byte>
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
                    throw new ArgumentOutOfRangeException(nameof(writer));
                }

                continue;
            }

            return;
        }
    }

    /// <summary>
    /// Writes the provided sequence to this buffer.
    /// </summary>
    /// <param name="input">The data to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySequence<byte> input)
    {
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
        var destination = GetSpan();

        // Fast path, try copying to the available memory directly
        if (value.Length <= destination.Length)
        {
            value.CopyTo(destination);
            Advance(value.Length);
        }
        else
        {
            WriteMultiSegment(value, destination);
        }
    }

    private void WriteMultiSegment(in ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var input = source;
        while (true)
        {
            var writeSize = Math.Min(destination.Length, input.Length);
            input[..writeSize].CopyTo(destination);
            Advance(writeSize);
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
    private readonly void UnpinAll()
    {
        var current = _first;
        while (current != null)
        {
            var previous = current;
            current = previous.Next;
            previous.Unpin(previous.Version);
        }
    }

    /// <summary>
    /// Returns a slice of the provided length without marking the data referred to it as consumed.
    /// </summary>
    /// <param name="length">The length to consume.</param>
    /// <returns>A slice of unconsumed data.</returns>
    public readonly ArcBuffer PeekSlice(int length)
        // Note that a token of -1 is used to prevent accidental unpinning of the page from the returned buffer.
        => new (_first, token: -1, offset: _consumedLength, length);

    /// <summary>
    /// Consumes a slice of the provided length.
    /// </summary>
    /// <param name="length">The length to consume.</param>
    /// <returns>A buffer representing the consumed data.</returns>
    public ArcBuffer ConsumeSlice(int length)
    {
        // Create a new slice and pin it.
        var result = new ArcBuffer(_first, token: _first.Version, offset: _consumedLength, length);
        result.Pin();

        AdvanceConsumerCursor(length);

        // Return the slice.
        return result;
    }

    private void AdvanceConsumerCursor(int length)
    {
        _consumedLength += length;

        // If this call would consume the entire first page and the page is not the last page, unpin it.
        while (_consumedLength > _first.Length && _current != _first)
        {
            // Advance the consumed length.
            _consumedLength -= _first.Length;
            _totalLength -= _first.Length;

            // Advance to the next page
            Debug.Assert(_first.Next is not null);
            _first = _first.Next!;

            // Unpin the page.
            _first.Unpin(_first.Version);
        }

        Debug.Assert(_first is not null);
        Debug.Assert(_consumedLength < _first.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Span<byte> GetSpanSlow(int sizeHint) => Grow(sizeHint).Array;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Memory<byte> GetMemorySlow(int sizeHint) => Grow(sizeHint).AsMemory(0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Memory<byte> GetExactMemorySlow(int length) => Grow(length).AsMemory(0, length);

    private ArcPage Grow(int sizeHint)
    {
        var newBuffer = ArcBufferPagePool.Shared.Rent(sizeHint);
        newBuffer.Pin(newBuffer.Version);
        _current.SetNext(newBuffer);
        _current = newBuffer;
        return newBuffer;
    }
}

internal sealed class ArcBufferPagePool
{
    public static ArcBufferPagePool Shared { get; } = new();
    public const int MinimumBlockSize = 4 * 1024;
    private readonly ConcurrentQueue<ArcPage> _pages = new();
    private readonly ConcurrentQueue<ArcPage> _largePages = new();

    private ArcBufferPagePool() { }

    public ArcPage Rent(int size = -1)
    {
        ArcPage? block;
        if (size <= MinimumBlockSize)
        {
            if (!_pages.TryDequeue(out block))
            {
                block = new ArcPage(size);
            }
        }
        else if (_largePages.TryDequeue(out block))
        {
            block.ResizeLargeSegment(size);
            return block;
        }

        return block ?? new ArcPage(size);
    }

    internal void Return(ArcPage block)
    {
        Debug.Assert(block.IsValid);
        if (block.IsMinimumSize)
        {
            _pages.Enqueue(block);
        }
        else
        {
            _largePages.Enqueue(block);
        }
    }
}

/// <summary>
/// A page of data.
/// </summary>
public sealed class ArcPage
{
    // The current version of the page. Each time the page is return to the pool, the version is incremented.
    // This helps to ensure that the page is not consumed after it has been returned to the pool.
    // This is a guard against certain programming bugs.
    private int _version;

    // The current reference count. This is used to ensure that a page is not returned to the pool while it is still in use.
    private int _refCount;

    internal ArcPage()
    {
        Array = [];
    }

    internal ArcPage(int length)
    {
#if !NET6_0_OR_GREATER
        Array = [];
#endif
        InitializeArray(length);
    }

    public void ResizeLargeSegment(int length)
    {
        Debug.Assert(length > ArcBufferPagePool.MinimumBlockSize);
        InitializeArray(length);
    }

#if NET6_0_OR_GREATER
    [MemberNotNull(nameof(Array))]
#endif
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InitializeArray(int length)
    {
        if (length <= ArcBufferPagePool.MinimumBlockSize)
        {
            Debug.Assert(Array is null);
#if NET6_0_OR_GREATER
            var array = GC.AllocateUninitializedArray<byte>(ArcBufferPagePool.MinimumBlockSize, pinned: true);
#else
                var array = new byte[ArcBufferPagePool.MinimumBlockSize];
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
    public byte[] Array { get; private set; }

    /// <summary>
    /// Gets the number of bytes which have been written to the page.
    /// </summary>
    public int Length { get; private set; }

    /// <summary>
    /// A <see cref="ReadOnlySpan{T}"/> containing the readable bytes from this page.
    /// </summary>
    public ReadOnlySpan<byte> ReadableSpan => Array.AsSpan(0, Length);

    /// <summary>
    /// A <see cref="ReadOnlyMemory{T}"/> containing the readable bytes from this page.
    /// </summary>
    public ReadOnlyMemory<byte> ReadableMemory => AsMemory(0, Length);

    /// <summary>
    /// An <see cref="ArraySegment{T}"/> containing the readable bytes from this page.
    /// </summary>
    public ArraySegment<byte> ReadableArraySegment => new(Array, 0, Length);

    /// <summary>
    /// Gets the next node.
    /// </summary>
    public ArcPage? Next { get; protected set; }

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
    public bool IsMinimumSize => Array.Length == ArcBufferPagePool.MinimumBlockSize;

    /// <summary>
    /// Gets the number of bytes in the page which are available for writing.
    /// </summary>
    public int WritableCapacity => Array.Length - Length;

    /// <summary>
    /// Gets the writable memory in the page.
    /// </summary>
    public Memory<byte> WritableMemory => AsMemory(Length);

    /// <summary>
    /// Gets a span representing the writable memory in the page.
    /// </summary>
    public Span<byte> WritableSpan => AsSpan(Length);

    /// <summary>
    /// Gets memory starting from the provided offset.
    /// </summary>
    /// <param name="offset">The offset into the array to return memory from.</param>
    /// <returns>Memory which can be written to.</returns>
    public Memory<byte> AsMemory(int offset)
    {
#if NET6_0_OR_GREATER
        if (IsMinimumSize)
        {
            return MemoryMarshal.CreateFromPinnedArray(Array, offset, Array.Length - offset);
        }
#endif

        return Array.AsMemory(offset);
    }

    /// <summary>
    /// Gets a span pointing to the underlying array, starting from the provided offset.
    /// </summary>
    /// <param name="offset">The offset.</param>
    /// <returns>A span pointing to the underlying array.</returns>
    public Span<byte> AsSpan(int offset) => Array.AsSpan(offset);

    /// <summary>
    /// Increases the number of bytes written to the page by the provided amount.
    /// </summary>
    /// <param name="bytes">The number of bytes to increase the length of this page by.</param>
    public void Advance(int bytes)
    {
        Length += bytes;
        Debug.Assert(Length <= Array.Length);
    }

    public Memory<byte> AsMemory(int offset, int length)
    {
#if NET6_0_OR_GREATER
        if (IsMinimumSize)
        {
            return MemoryMarshal.CreateFromPinnedArray(Array, offset, length);
        }
#endif

        return Array.AsMemory(offset, length);
    }

    /// <summary>
    /// Sets the next page in the sequence.
    /// </summary>
    /// <param name="next">The next page in the sequence.</param>
    public void SetNext(ArcPage next)
    {
        Debug.Assert(Next is null);
        Next = next;
    }

    private void Return()
    {
        Length = 0;
        Next = default;
        Interlocked.Increment(ref _version);
        ArcBufferPagePool.Shared.Return(this);
    }

    /// <summary>
    /// Pins this page to prevent it from being returned to the page pool.
    /// </summary>
    /// <param name="token">The token, which must match the page's <see cref="Version"/> for this operation to be allowed.</param>
    public void Pin(int token)
    {
        ThrowIfTokenIsInvalid(token);
        Interlocked.Increment(ref _refCount);
    }

    /// <summary>
    /// Unpins this page, allowing it to be returned to the page pool.
    /// </summary>
    /// <param name="token">The token, which must match the page's <see cref="Version"/> for this operation to be allowed.</param>
    public void Unpin(int token)
    {
        ThrowIfTokenIsInvalid(token);
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            Return();
        }
    }

    /// <summary>
    /// Throws if the provided <paramref name="token"/> does not match the page's <see cref="Version"/>.
    /// </summary>
    /// <param name="token">The token, which must match the page's <see cref="Version"/>.</param>
    public void ThrowIfTokenIsInvalid(int token)
    {
        if (token != _version)
        {
            ThrowInvalidVersion();
        }
    }

    [DoesNotReturn]
    private static void ThrowInvalidVersion() => throw new InvalidOperationException("An invalid token was provided when attempting to perform an operation on this page.");
}

/// <summary>
/// Represents a slice of a <see cref="ArcBufferWriter"/>.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ArcBuffer"/> type.
/// </remarks>
/// <param name="first">The first page in the sequence.</param>
/// <param name="token">The token of the first page in the sequence.</param>
/// <param name="offset">The offset into the buffer at which this slice begins.</param>
/// <param name="length">The length of this slice.</param>
public readonly struct ArcBuffer(ArcPage first, int token, int offset, int length)
{
    /// <summary>
    /// Gets the token of the first page pointed to by this slice.
    /// </summary>
    private readonly int _firstPageToken = token;

    /// <summary>
    /// Gets the first page.
    /// </summary>
    public readonly ArcPage First = first;

    /// <summary>
    /// Gets the first span.
    /// </summary>
    public readonly ReadOnlySpan<byte> FirstSpan => First.ReadableSpan;

    /// <summary>
    /// Gets the offset into the first page at which this slice begins.
    /// </summary>
    public readonly int Offset = offset;

    /// <summary>
    /// Gets the length of this sequence.
    /// </summary>
    public readonly int Length = length;

    /// <summary>Copies the contents of this writer to a span.</summary>
    public readonly int CopyTo(Span<byte> output)
    {
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
    public readonly void CopyTo(ref ArcBufferWriter output)
    {
        foreach (var span in this)
        {
            output.Write(span);
        }
    }

    /// <summary>Copies the contents of this writer to a buffer writer.</summary>
    public readonly void CopyTo<TBufferWriter>(ref TBufferWriter output) where TBufferWriter : struct, IBufferWriter<byte>
    {
        foreach (var span in this)
        {
            output.Write(span);
        }
    }

    /// <summary>
    /// Returns the data which has been written as an array.
    /// </summary>
    /// <returns>The data which has been written.</returns>
    public readonly byte[] ToArray()
    {
        var result = new byte[Length];
        CopyTo(result);
        return result;
    }

    /// <summary>
    /// Pins this slice, preventing the referenced pages from being returned to the pool.
    /// </summary>
    public void Pin()
    {
        var pageEnumerator = Pages.GetEnumerator();
        if (pageEnumerator.MoveNext())
        {
            var page = pageEnumerator.Current!;
            page.Pin(_firstPageToken);
        }

        while (pageEnumerator.MoveNext())
        {
            var page = pageEnumerator.Current!;
            page.Pin(page.Version);
        }
    }

    /// <summary>
    /// Unpins this slice, allowing the referenced pages to be returned to the pool.
    /// </summary>
    public void Unpin()
    {
        var pageEnumerator = Pages.GetEnumerator();
        if (pageEnumerator.MoveNext())
        {
            var page = pageEnumerator.Current!;
            page.Unpin(_firstPageToken);
        }

        while (pageEnumerator.MoveNext())
        {
            var page = pageEnumerator.Current!;
            page.Unpin(page.Version);
        }
    }

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the span segments referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly SpanEnumerator GetEnumerator() => new(this);

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the pages referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly PageEnumerator Pages => new(this);

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the span segments referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly SpanEnumerator SpanSegments => new(this);

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the memory segments referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly MemoryEnumerator MemorySegments => new(this);

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the array segments referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly ArraySegmentEnumerator ArraySegments => new(this);

    /// <summary>
    /// Enumerates over pages in a <see cref="ArcBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="PageEnumerator"/> type.
    /// </remarks>
    /// <param name="slice">The slice to enumerate.</param>
    public struct PageEnumerator(ArcBuffer slice)
    {
        private readonly ArcBuffer _slice = slice;
        private int _position;
        private ArcPage? _segment = slice.First;

        internal readonly ArcPage First => _slice.First;
        internal readonly int Offset => _slice.Offset;
        internal readonly int Length => _slice.Length;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly PageEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public ArcPage? Current { get; private set; }

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            if (_segment == First)
            {
                var length = Math.Min(Length, _segment.Length - Offset);
                _position += length;
                Current = _segment;
                _segment = _segment.Next;
                return true;
            }

            if (_segment is not null && _position != Length)
            {
                var length = Math.Min(Length - _position, _segment.Length);
                _position += length;
                Current = _segment;
                _segment = _segment.Next;
                return true;
            }

            Current = default;
            Debug.Assert(_position == Length);
            return false;
        }
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
        private readonly ArcBuffer _slice = slice;
        private int _position;
        private ArcPage? _segment = slice.First;

        internal readonly ArcPage First => _slice.First;
        internal readonly int Offset => _slice.Offset;
        internal readonly int Length => _slice.Length;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly SpanEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public ReadOnlySpan<byte> Current { get; private set; } = [];

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            if (_segment == First)
            {
                var offset = Offset;
                var length = Math.Min(Length, _segment.Length - offset);
                _position += length;
                Current = _segment.ReadableMemory.Span[offset..length];
                _segment = _segment.Next;
                return true;
            }

            if (_segment is not null && _position != Length)
            {
                var length = Math.Min(Length - _position, _segment.Length);
                _position += length;
                Current = _segment.ReadableMemory.Span[..length];
                _segment = _segment.Next;
                return true;
            }

            Current = [];
            Debug.Assert(_position == Length);
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
    public struct MemoryEnumerator(ArcBuffer slice)
    {
        private readonly ArcBuffer _slice = slice;
        private int _position;
        private ArcPage? _segment = slice.First;

        internal readonly ArcPage First => _slice.First;
        internal readonly int Offset => _slice.Offset;
        internal readonly int Length => _slice.Length;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly MemoryEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public ReadOnlyMemory<byte> Current { get; private set; }

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            if (_segment == First)
            {
                var offset = Offset;
                var length = Math.Min(Length, _segment.Length - offset);
                _position += length;
                Current = _segment.ReadableMemory[offset..length];
                _segment = _segment.Next;
                return true;
            }

            if (_segment is not null && _position != Length)
            {
                var length = Math.Min(Length - _position, _segment.Length);
                _position += length;
                Current = _segment.ReadableMemory[..length];
                _segment = _segment.Next;
                return true;
            }

            Current = default;
            Debug.Assert(_position == Length);
            return false;
        }
    }

    /// <summary>
    /// Enumerates over array segments in a <see cref="ArcBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="ArraySegmentEnumerator"/> type.
    /// </remarks>
    /// <param name="slice">The slice to enumerate.</param>
    public struct ArraySegmentEnumerator(ArcBuffer slice)
    {
        private readonly ArcBuffer _slice = slice;
        private int _position;
        private ArcPage? _segment = slice.First;

        internal readonly ArcPage First => _slice.First;
        internal readonly int Offset => _slice.Offset;
        internal readonly int Length => _slice.Length;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly ArraySegmentEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public ArraySegment<byte> Current { get; private set; }

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            if (_segment == First)
            {
                var offset = Offset;
                var length = Math.Min(Length, _segment.Length - offset);
                _position += length;
                Current = _segment.ReadableArraySegment[offset..length];
                _segment = _segment.Next;
                return true;
            }

            if (_segment is not null && _position != Length)
            {
                var length = Math.Min(Length - _position, _segment.Length);
                _position += length;
                Current = _segment.ReadableArraySegment[..length];
                _segment = _segment.Next;
                return true;
            }

            Current = default;
            Debug.Assert(_position == Length);
            return false;
        }
    }
}
