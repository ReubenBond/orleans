using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A contiguous batch of slots read by <see cref="ILedgerReplica.ReadRangeAsync"/>: <see cref="Count"/> slots starting
/// at <see cref="FirstSlot"/>, packed into a single owned <see cref="ArcBuffer"/> as repeated
/// <c>[u32 little-endian length][batch bytes]</c> frames.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Count"/> == 0 with <see cref="FirstSlotTooLargeLength"/> == 0 means <see cref="FirstSlot"/> is missing on
/// the replica (or the requested range was empty). <see cref="Count"/> == 0 with <see cref="FirstSlotTooLargeLength"/> &gt; 0
/// means <see cref="FirstSlot"/> exists on the replica and its single batch exceeds the caller's <c>maxBytes</c>
/// budget, so the caller reads it via <see cref="ILedgerReplica.ReadAsync(Slot, Slot, CancellationToken)"/>.
/// </para>
/// <para>
/// The caller owns <see cref="Packed"/> and must dispose it via <see cref="Dispose"/>.
/// </para>
/// </remarks>
public readonly struct ReplicaRangeBatch : IDisposable
{
    /// <summary>Number of header bytes preceding each packed slot's batch bytes.</summary>
    public const int FrameHeaderLength = sizeof(int);

    /// <summary>Initializes a new <see cref="ReplicaRangeBatch"/>.</summary>
    public ReplicaRangeBatch(Slot firstSlot, int count, ArcBuffer packed, int firstSlotTooLargeLength = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(firstSlotTooLargeLength);
        if (count == 0 && packed.Length != 0)
        {
            throw new ArgumentException("An empty range batch carries no packed bytes.", nameof(packed));
        }

        if (count > 0 && firstSlotTooLargeLength != 0)
        {
            throw new ArgumentException("A non-empty range batch cannot report FirstSlotTooLargeLength.", nameof(firstSlotTooLargeLength));
        }

        FirstSlot = firstSlot;
        Count = count;
        Packed = packed;
        FirstSlotTooLargeLength = firstSlotTooLargeLength;
    }

    /// <summary>Gets the first slot requested in this range read.</summary>
    public Slot FirstSlot { get; }

    /// <summary>Gets the number of contiguous slots packed in <see cref="Packed"/> starting at <see cref="FirstSlot"/>.</summary>
    public int Count { get; }

    /// <summary>Gets the packed frames (<c>[u32 length][bytes]</c> repeated <see cref="Count"/> times); empty when <see cref="Count"/> is 0.</summary>
    public ArcBuffer Packed { get; }

    /// <summary>Gets the length of <see cref="FirstSlot"/>'s batch when it alone exceeded <c>maxBytes</c>, or 0 otherwise.</summary>
    public int FirstSlotTooLargeLength { get; }

    /// <summary>Returns an empty range batch indicating <paramref name="firstSlot"/> was missing on the replica.</summary>
    public static ReplicaRangeBatch Empty(Slot firstSlot) => new(firstSlot, 0, default);

    /// <summary>Returns a range batch indicating <paramref name="firstSlot"/>'s single batch exceeded <c>maxBytes</c>.</summary>
    public static ReplicaRangeBatch TooLarge(Slot firstSlot, int batchLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchLength);
        return new(firstSlot, 0, default, batchLength);
    }

    /// <summary>Releases <see cref="Packed"/>.</summary>
    public void Dispose() => Packed.Dispose();

    /// <summary>
    /// Validates that <paramref name="packed"/> contains exactly <paramref name="count"/> well-formed length-prefixed
    /// non-empty slot frames with no trailing bytes; returns <see langword="null"/> when valid.
    /// </summary>
    public static InvalidDataException? ValidatePacked(int count, ArcBuffer packed)
    {
        if (count < 0)
        {
            return new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A range batch cannot hold a negative slot count ({count})."));
        }

        if (count == 0)
        {
            return packed.Length == 0
                ? null
                : new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"An empty range batch carried {packed.Length} unexpected bytes."));
        }

        var offset = 0;
        var total = packed.Length;
        Span<byte> header = stackalloc byte[FrameHeaderLength];
        for (var index = 0; index < count; index++)
        {
            if (total - offset < FrameHeaderLength)
            {
                return new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Range batch truncated at slot {index} of {count}: expected {FrameHeaderLength}-byte length header."));
            }

            _ = packed.UnsafeSlice(offset, FrameHeaderLength).CopyTo(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            offset += FrameHeaderLength;
            if (length <= 0 || length > total - offset)
            {
                return new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Range batch slot {index} of {count} has invalid length {length} with {total - offset} bytes remaining."));
            }

            offset += length;
        }

        if (offset != total)
        {
            return new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Range batch has {total - offset} trailing bytes after {count} slots."));
        }

        return null;
    }

    /// <summary>Writes <paramref name="batch"/> prefixed by its 4-byte little-endian length into <paramref name="writer"/>.</summary>
    public static void WriteFrame(ArcBufferWriter writer, ArcBuffer batch)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> header = stackalloc byte[FrameHeaderLength];
        BinaryPrimitives.WriteInt32LittleEndian(header, batch.Length);
        writer.Write(header);
        batch.CopyTo(writer);
    }

    /// <summary>Reads the next slot's length from <paramref name="packed"/> at <paramref name="offset"/> and advances <paramref name="offset"/> past the frame.</summary>
    public static ArcBuffer SliceNext(ArcBuffer packed, ref int offset)
    {
        Span<byte> header = stackalloc byte[FrameHeaderLength];
        _ = packed.UnsafeSlice(offset, FrameHeaderLength).CopyTo(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var dataOffset = offset + FrameHeaderLength;
        offset = dataOffset + length;
        return packed.Slice(dataOffset, length);
    }
}

/// <summary>
/// The answer of <see cref="ILedgerReplica.ReadRangeAsync"/>: a <see cref="ReplicaRangeBatch"/>,
/// <see cref="ReplicaWriteOutcome.Dropped"/>, or <see cref="ReplicaWriteOutcome.Backpressure"/>.
/// </summary>
[Union]
public readonly struct ReplicaRangeRead : IUnion
{
    private readonly ReplicaRangeBatch _batch;
    private readonly Kind _kind;

    /// <summary>Initializes a new <see cref="ReplicaRangeRead"/> holding a range batch.</summary>
    public ReplicaRangeRead(ReplicaRangeBatch value)
    {
        _batch = value;
        _kind = Kind.Batch;
    }

    /// <summary>Initializes a new <see cref="ReplicaRangeRead"/> holding <see cref="ReplicaWriteOutcome.Dropped"/>.</summary>
    public ReplicaRangeRead(ReplicaWriteOutcome.Dropped value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _kind = Kind.Dropped;
    }

    /// <summary>Initializes a new <see cref="ReplicaRangeRead"/> holding <see cref="ReplicaWriteOutcome.Backpressure"/>.</summary>
    public ReplicaRangeRead(ReplicaWriteOutcome.Backpressure value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _kind = Kind.Backpressure;
    }

    private enum Kind : byte
    {
        None,
        Batch,
        Dropped,
        Backpressure,
    }

    /// <inheritdoc/>
    public object? Value => _kind switch
    {
        Kind.Batch => _batch,
        Kind.Dropped => ReplicaWriteOutcome.Dropped.Instance,
        Kind.Backpressure => ReplicaWriteOutcome.Backpressure.Instance,
        Kind.None => null,
        _ => throw new UnreachableException(),
    };

    /// <inheritdoc/>
    public bool HasValue => _kind != Kind.None;

    /// <summary>Converts a <see cref="ReplicaRangeBatch"/> into a <see cref="ReplicaRangeRead"/>.</summary>
    public static implicit operator ReplicaRangeRead(ReplicaRangeBatch value) => new(value);

    /// <summary>Converts <see cref="ReplicaWriteOutcome.Dropped"/> into a <see cref="ReplicaRangeRead"/>.</summary>
    public static implicit operator ReplicaRangeRead(ReplicaWriteOutcome.Dropped value) => new(value);

    /// <summary>Converts <see cref="ReplicaWriteOutcome.Backpressure"/> into a <see cref="ReplicaRangeRead"/>.</summary>
    public static implicit operator ReplicaRangeRead(ReplicaWriteOutcome.Backpressure value) => new(value);

    /// <summary>Extracts the <see cref="ReplicaRangeBatch"/> without boxing when this answer holds one.</summary>
    public bool TryGetValue(out ReplicaRangeBatch value)
    {
        if (_kind == Kind.Batch)
        {
            value = _batch;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Extracts <see cref="ReplicaWriteOutcome.Dropped"/> when this answer holds it.</summary>
    public bool TryGetValue([NotNullWhen(true)] out ReplicaWriteOutcome.Dropped? value)
    {
        if (_kind == Kind.Dropped)
        {
            value = ReplicaWriteOutcome.Dropped.Instance;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Extracts <see cref="ReplicaWriteOutcome.Backpressure"/> when this answer holds it.</summary>
    public bool TryGetValue([NotNullWhen(true)] out ReplicaWriteOutcome.Backpressure? value)
    {
        if (_kind == Kind.Backpressure)
        {
            value = ReplicaWriteOutcome.Backpressure.Instance;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Releases any packed buffer held by this answer.</summary>
    public void Release()
    {
        if (_kind == Kind.Batch)
        {
            _batch.Dispose();
        }
    }
}
