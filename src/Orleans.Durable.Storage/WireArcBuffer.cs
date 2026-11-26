using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Durable.Storage;

/// <summary>
/// A zero-copy, consume-on-serialize wire lease over Durable Storage's pooled <see cref="ArcBuffer"/> (orleans.md §5.12,
/// OR-I18). Each instance owns at most one <see cref="ArcBuffer"/> lease, released atomically at most once by whichever
/// consumer takes it first: <see cref="WireArcBufferCodec.WriteField{TBufferWriter}"/> during remote serialization,
/// <see cref="TakeBuffer"/> at a local callee/caller, or <see cref="Dispose"/> on drop-before-serialization or deadline
/// expiry.
/// </summary>
internal sealed class WireArcBuffer : IDisposable
{
    private ArcBuffer _buffer;
    private int _disposed;
    private ITimer? _deadlineTimer;
    private WireArcBuffer? _lastCopy;

    private WireArcBuffer(ArcBuffer buffer, bool disposed)
    {
        _buffer = buffer;
        _disposed = disposed ? 1 : 0;
    }

    /// <summary>Gets the shared empty, already-consumed instance.</summary>
    public static WireArcBuffer Empty { get; } = new(default, disposed: true);

    /// <summary>Gets the length in bytes of the live buffer, or 0 once consumed or disposed.</summary>
    public int Length => Volatile.Read(ref _disposed) == 0 ? _buffer.Length : 0;

    /// <summary>Gets a value indicating whether this instance carries no live bytes.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Gets a value indicating whether this instance's lease has been consumed or disposed.</summary>
    public bool IsConsumed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Takes ownership of <paramref name="buffer"/>'s existing lease without copying or retaining; the caller must not
    /// dispose <paramref name="buffer"/> after calling this method.
    /// </summary>
    public static WireArcBuffer Take(ArcBuffer buffer) =>
        buffer.Length == 0 ? Empty : new WireArcBuffer(buffer, disposed: false);

    /// <summary>Retains an independent zero-copy lease on <paramref name="buffer"/>; the caller keeps its own lease.</summary>
    public static WireArcBuffer Retain(ArcBuffer buffer) =>
        buffer.Length == 0 ? Empty : new WireArcBuffer(buffer.Retain(), disposed: false);

    /// <summary>Encodes <paramref name="record"/> directly into Durable Storage's pooled buffer pool.</summary>
    public static WireArcBuffer FromRecord(JournalEntryRecord record) =>
        record.Length == 0
            ? throw new InvalidOperationException("The default JournalEntryRecord holds no record.")
            : Take(record.ToArcBuffer());

    /// <summary>
    /// Atomically takes ownership of the underlying <see cref="ArcBuffer"/> lease and cancels any attached safety timer,
    /// leaving this instance consumed. Returns <see langword="default"/> if already consumed or empty.
    /// </summary>
    public ArcBuffer TakeBuffer()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return default;
        }

        Interlocked.Exchange(ref _deadlineTimer, null)?.Dispose();
        var buffer = _buffer;
        _buffer = default;
        return buffer;
    }

    /// <summary>
    /// Retains an independent zero-copy <see cref="WireArcBuffer"/> sharing the same pooled pages, recording it as the
    /// latest proxy copy for drop-before-serialization cleanup.
    /// </summary>
    public WireArcBuffer RetainCopy()
    {
        if (Volatile.Read(ref _disposed) != 0 || _buffer.Length == 0)
        {
            return Empty;
        }

        var copy = new WireArcBuffer(_buffer.Retain(), disposed: false);
        Volatile.Write(ref _lastCopy, copy);
        return copy;
    }

    /// <summary>Detaches and returns the latest copy produced by <see cref="RetainCopy"/>, if any.</summary>
    public WireArcBuffer? DetachLastCopy() => Interlocked.Exchange(ref _lastCopy, null);

    /// <summary>
    /// Attaches a safety timer which disposes this buffer if it is dropped before serialization; cancelled immediately
    /// once <see cref="TakeBuffer"/> or <see cref="Dispose"/> consumes the lease.
    /// </summary>
    internal void AttachDeadlineTimer(ITimer timer)
    {
        var previous = Interlocked.Exchange(ref _deadlineTimer, timer);
        previous?.Dispose();
        if (Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Exchange(ref _deadlineTimer, null)?.Dispose();
        }
    }

    /// <summary>Copies the live buffer's bytes into a new array, for diagnostics and test assertions.</summary>
    public byte[] ToArray() => Volatile.Read(ref _disposed) == 0 ? _buffer.ToArray() : [];

    /// <summary>Atomically releases the underlying <see cref="ArcBuffer"/> lease at most once.</summary>
    public void Dispose() => TakeBuffer().Dispose();
}

/// <summary>
/// Consume-on-serialize Orleans field codec for <see cref="WireArcBuffer"/>: writes Durable Storage's pooled spans
/// directly into the transport writer and immediately returns the pages to the pool, and reads incoming wire bytes
/// directly into Durable Storage's page pool with zero intermediate buffer copies.
/// </summary>
[RegisterSerializer]
internal sealed class WireArcBufferCodec : IFieldCodec<WireArcBuffer>
{
    /// <inheritdoc/>
    public void WriteField<TBufferWriter>(ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, [AllowNull] Type expectedType, [AllowNull] WireArcBuffer value)
        where TBufferWriter : IBufferWriter<byte>
    {
        using var buffer = value is null ? default : value.TakeBuffer();
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(WireArcBuffer), WireType.LengthPrefixed);
        writer.WriteVarUInt32((uint)buffer.Length);
        foreach (var span in buffer)
        {
            writer.Write(span);
        }
    }

    /// <inheritdoc/>
    public WireArcBuffer ReadValue<TInput>(ref Orleans.Serialization.Buffers.Reader<TInput> reader, Field field)
    {
        ReferenceCodec.MarkValueField(reader.Session);
        field.EnsureWireType(WireType.LengthPrefixed);
        var encodedLength = reader.ReadVarUInt32();
        if (encodedLength == 0)
        {
            return WireArcBuffer.Empty;
        }

        if (encodedLength > int.MaxValue)
        {
            throw new InvalidDataException($"The declared WireArcBuffer length, {encodedLength}, exceeds {int.MaxValue}.");
        }

        var length = (int)encodedLength;
        using var writer = new ArcBufferWriter(length);
        var remaining = length;
        while (remaining > 0)
        {
            var span = writer.GetSpan();
            var chunk = Math.Min(remaining, span.Length);
            reader.ReadBytes(span[..chunk]);
            writer.AdvanceWriter(chunk);
            remaining -= chunk;
        }

        return WireArcBuffer.Take(writer.ConsumeSlice(length));
    }
}

/// <summary>Zero-copy Orleans deep copier for <see cref="WireArcBuffer"/>: retains an independent lease on the pooled pages.</summary>
[RegisterCopier]
internal sealed class WireArcBufferCopier : IDeepCopier<WireArcBuffer>
{
    /// <inheritdoc/>
    public WireArcBuffer DeepCopy(WireArcBuffer? input, CopyContext context) =>
        input is null ? WireArcBuffer.Empty : input.RetainCopy();
}
