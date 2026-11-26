using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A scattered batch of slots read by <see cref="ILedgerReplica.ReadSlotsAsync"/>: <see cref="Count"/> slots packed
/// into a single owned <see cref="ArcBuffer"/> as repeated
/// <c>[i64 little-endian slot][i32 little-endian length][batch bytes]</c> frames in strictly ascending slot order.
/// </summary>
/// <remarks>
/// <para>
/// A replica returns only the subset of requested slots it holds cleanly within the caller's <c>maxBytes</c> budget;
/// any requested slot omitted from <see cref="Packed"/> was missing, faulty, or exceeded the remaining byte budget on
/// that replica and is fetched from another replica or via single-slot <see cref="ILedgerReplica.ReadAsync(Slot, Slot, CancellationToken)"/>.
/// </para>
/// <para>
/// The caller owns <see cref="Packed"/> and must dispose it via <see cref="Dispose"/>.
/// </para>
/// </remarks>
public readonly struct ReplicaSlotsBatch : IDisposable
{
    /// <summary>Number of header bytes preceding each packed slot's batch bytes (<c>sizeof(long) + sizeof(int)</c>).</summary>
    public const int FrameHeaderLength = sizeof(long) + sizeof(int);

    /// <summary>Initializes a new <see cref="ReplicaSlotsBatch"/>.</summary>
    public ReplicaSlotsBatch(int count, ArcBuffer packed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0 && packed.Length != 0)
        {
            throw new ArgumentException("An empty slots batch carries no packed bytes.", nameof(packed));
        }

        Count = count;
        Packed = packed;
    }

    /// <summary>Gets an empty slots batch carrying zero slots.</summary>
    public static ReplicaSlotsBatch Empty => default;

    /// <summary>Gets the number of slots packed in <see cref="Packed"/>.</summary>
    public int Count { get; }

    /// <summary>Gets the packed frames (<c>[i64 slot][i32 length][bytes]</c> repeated <see cref="Count"/> times); empty when <see cref="Count"/> is 0.</summary>
    public ArcBuffer Packed { get; }

    /// <summary>Releases <see cref="Packed"/>.</summary>
    public void Dispose() => Packed.Dispose();

    /// <summary>
    /// Validates that <paramref name="packed"/> contains exactly <paramref name="count"/> well-formed frames in
    /// strictly ascending slot order, every slot present in the ascending <paramref name="requestedSlots"/>, with
    /// positive batch lengths and no trailing bytes; returns <see langword="null"/> when valid.
    /// </summary>
    public static InvalidDataException? ValidatePacked(int count, ArcBuffer packed, ReadOnlySpan<Slot> requestedSlots)
    {
        if (count < 0 || count > requestedSlots.Length)
        {
            return new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A slots batch cannot hold {count} slots for {requestedSlots.Length} requested slots."));
        }

        if (count == 0)
        {
            return packed.Length == 0
                ? null
                : new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"An empty slots batch carried {packed.Length} unexpected bytes."));
        }

        var offset = 0;
        var total = packed.Length;
        var reqIndex = 0;
        var previousSlot = -1L;
        Span<byte> header = stackalloc byte[FrameHeaderLength];
        for (var index = 0; index < count; index++)
        {
            if (total - offset < FrameHeaderLength)
            {
                return new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Slots batch truncated at entry {index} of {count}: expected {FrameHeaderLength}-byte header."));
            }

            _ = packed.UnsafeSlice(offset, FrameHeaderLength).CopyTo(header);
            var slot = BinaryPrimitives.ReadInt64LittleEndian(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header[sizeof(long)..]);
            offset += FrameHeaderLength;
            if (slot <= previousSlot)
            {
                return new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Slots batch entry {index} of {count} has non-increasing slot {slot} after {previousSlot}."));
            }

            while (reqIndex < requestedSlots.Length && requestedSlots[reqIndex].Value < slot)
            {
                reqIndex++;
            }

            if (reqIndex >= requestedSlots.Length || requestedSlots[reqIndex].Value != slot)
            {
                return new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Slots batch entry {index} of {count} returned unrequested slot {slot}."));
            }

            reqIndex++;
            previousSlot = slot;

            if (length <= 0 || length > total - offset)
            {
                return new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Slots batch entry {index} of {count} (slot {slot}) has invalid length {length} with {total - offset} bytes remaining."));
            }

            offset += length;
        }

        if (offset != total)
        {
            return new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Slots batch has {total - offset} trailing bytes after {count} slots."));
        }

        return null;
    }

    /// <summary>Writes <paramref name="slot"/> and <paramref name="batch"/> into <paramref name="writer"/>.</summary>
    public static void WriteFrame(ArcBufferWriter writer, Slot slot, ArcBuffer batch)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> header = stackalloc byte[FrameHeaderLength];
        BinaryPrimitives.WriteInt64LittleEndian(header, slot.Value);
        BinaryPrimitives.WriteInt32LittleEndian(header[sizeof(long)..], batch.Length);
        writer.Write(header);
        batch.CopyTo(writer);
    }

    /// <summary>Reads the next frame's slot and zero-copy batch slice from <paramref name="packed"/> and advances <paramref name="offset"/>.</summary>
    public static ArcBuffer SliceNext(ArcBuffer packed, ref int offset, out Slot slot)
    {
        Span<byte> header = stackalloc byte[FrameHeaderLength];
        _ = packed.UnsafeSlice(offset, FrameHeaderLength).CopyTo(header);
        slot = new Slot(BinaryPrimitives.ReadInt64LittleEndian(header));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[sizeof(long)..]);
        var dataOffset = offset + FrameHeaderLength;
        offset = dataOffset + length;
        return packed.Slice(dataOffset, length);
    }
}

/// <summary>
/// The answer of <see cref="ILedgerReplica.ReadSlotsAsync"/>: a <see cref="ReplicaSlotsBatch"/>,
/// <see cref="ReplicaWriteOutcome.Dropped"/>, or <see cref="ReplicaWriteOutcome.Backpressure"/>.
/// </summary>
[Union]
public readonly struct ReplicaSlotsRead : IUnion
{
    private readonly ReplicaSlotsBatch _batch;
    private readonly Kind _kind;

    /// <summary>Initializes a new <see cref="ReplicaSlotsRead"/> holding a slots batch.</summary>
    public ReplicaSlotsRead(ReplicaSlotsBatch value)
    {
        _batch = value;
        _kind = Kind.Batch;
    }

    /// <summary>Initializes a new <see cref="ReplicaSlotsRead"/> holding <see cref="ReplicaWriteOutcome.Dropped"/>.</summary>
    public ReplicaSlotsRead(ReplicaWriteOutcome.Dropped value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _kind = Kind.Dropped;
    }

    /// <summary>Initializes a new <see cref="ReplicaSlotsRead"/> holding <see cref="ReplicaWriteOutcome.Backpressure"/>.</summary>
    public ReplicaSlotsRead(ReplicaWriteOutcome.Backpressure value)
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

    /// <summary>Converts a <see cref="ReplicaSlotsBatch"/> into a <see cref="ReplicaSlotsRead"/>.</summary>
    public static implicit operator ReplicaSlotsRead(ReplicaSlotsBatch value) => new(value);

    /// <summary>Converts <see cref="ReplicaWriteOutcome.Dropped"/> into a <see cref="ReplicaSlotsRead"/>.</summary>
    public static implicit operator ReplicaSlotsRead(ReplicaWriteOutcome.Dropped value) => new(value);

    /// <summary>Converts <see cref="ReplicaWriteOutcome.Backpressure"/> into a <see cref="ReplicaSlotsRead"/>.</summary>
    public static implicit operator ReplicaSlotsRead(ReplicaWriteOutcome.Backpressure value) => new(value);

    /// <summary>Extracts the <see cref="ReplicaSlotsBatch"/> without boxing when this answer holds one.</summary>
    public bool TryGetValue(out ReplicaSlotsBatch value)
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
