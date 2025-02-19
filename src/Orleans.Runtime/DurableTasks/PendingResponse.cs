using System;
using System.Buffers;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Runtime.DurableTasks;

/// <summary>
/// Represents a pending result for a <see cref="DurableTask"/> or <see cref="DurableTask{TResult}"/> method.
/// </summary>
[Immutable, UseActivator, SuppressReferenceTracking]
[Alias("PendingResponse")]
public sealed class PendingResponse : Response
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static PendingResponse Instance { get; } = new();

    /// <inheritdoc/>
    public override object Result { get => null; set => throw new InvalidOperationException($"Type {nameof(PendingResponse)} is read-only."); }

    /// <inheritdoc/>
    public override Exception Exception { get => null; set => throw new InvalidOperationException($"Type {nameof(PendingResponse)} is read-only."); }

    public override bool IsFinal => false;

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override void Dispose() { }

    /// <inheritdoc/>
    public override string ToString() => "[Pending]";
}

/// <summary>
/// Activator for <see cref="PendingResponse"/>.
/// </summary>
[RegisterActivator]
internal sealed class PendingResponseActivator : IActivator<PendingResponse>
{
    /// <inheritdoc/>
    public PendingResponse Create() => PendingResponse.Instance;
}

/// <summary>
/// Represents a result for a <see cref="DurableTask"/> or <see cref="DurableTask{TResult}"/> method.
/// </summary>
[Immutable, UseActivator, SuppressReferenceTracking]
[Alias("SubscribedResponse")]
public sealed class SubscribedResponse : Response
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static SubscribedResponse Instance { get; } = new();

    /// <inheritdoc/>
    public override object Result { get => null; set => throw new InvalidOperationException($"Type {nameof(SubscribedResponse)} is read-only."); }

    /// <inheritdoc/>
    public override Exception Exception { get => null; set => throw new InvalidOperationException($"Type {nameof(SubscribedResponse)} is read-only."); }

    public override bool IsFinal => false;

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override void Dispose() { }

    /// <inheritdoc/>
    public override string ToString() => "[Subscribed]";
}

/// <summary>
/// Activator for <see cref="SubscribedResponse"/>.
/// </summary>
[RegisterActivator]
internal sealed class SubscribedResponseActivator : IActivator<SubscribedResponse>
{
    /// <inheritdoc/>
    public SubscribedResponse Create() => SubscribedResponse.Instance;
}

/// <summary>
/// Represents an unknown task result for a <see cref="DurableTask"/> or <see cref="DurableTask{TResult}"/> method.
/// </summary>
[Immutable, UseActivator, SuppressReferenceTracking]
[Alias("UnknownTaskResponse")]
public sealed class UnknownTaskResponse : Response
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static UnknownTaskResponse Instance { get; } = new();

    /// <inheritdoc/>
    public override object Result
    {
        get => throw new KeyNotFoundException("A task with the specified identifier was not found.");
        set => throw new InvalidOperationException($"Type {nameof(UnknownTaskResponse)} is read-only.");
    }

    /// <inheritdoc/>
    public override Exception Exception
    {
        get => throw new KeyNotFoundException("A task with the specified identifier was not found.");
        set => throw new InvalidOperationException($"Type {nameof(UnknownTaskResponse)} is read-only.");
    }

    public override bool IsFinal => true;

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override void Dispose() { }

    /// <inheritdoc/>
    public override string ToString() => "[UnknownTask]";
}

/// <summary>
/// Activator for <see cref="UnknownTaskResponse"/>.
/// </summary>
[RegisterActivator]
internal sealed class UnknownTaskResponseActivator : IActivator<UnknownTaskResponse>
{
    /// <inheritdoc/>
    public UnknownTaskResponse Create() => UnknownTaskResponse.Instance;
}

[RegisterSerializer, RegisterCopier]
internal sealed class PendingResponseCodec : IFieldCodec<PendingResponse>, IDeepCopier<PendingResponse>, IOptionalDeepCopier
{
    /// <inheritdoc />
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, PendingResponse value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.VarInt);
        writer.WriteByte(1);
    }

    /// <inheritdoc />
    public PendingResponse ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireType(WireType.VarInt);

        ReferenceCodec.MarkValueField(reader.Session);
        var length = reader.ReadVarUInt32();
        if (length != 0) throw new UnexpectedLengthPrefixValueException(nameof(PendingResponse), 0, length);

        return PendingResponse.Instance;
    }

    public bool IsShallowCopyable() => true;
    public object DeepCopy(object input, CopyContext context) => input;
    public PendingResponse DeepCopy(PendingResponse input, CopyContext context) => input;
}

[RegisterSerializer, RegisterCopier]
internal sealed class SubscribedResponseCodec : IFieldCodec<SubscribedResponse>, IDeepCopier<SubscribedResponse>, IOptionalDeepCopier
{
    /// <inheritdoc />
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, SubscribedResponse value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.VarInt);
        writer.WriteByte(1);
    }

    /// <inheritdoc />
    public SubscribedResponse ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireType(WireType.VarInt);

        ReferenceCodec.MarkValueField(reader.Session);
        var length = reader.ReadVarUInt32();
        if (length != 0) throw new UnexpectedLengthPrefixValueException(nameof(SubscribedResponse), 0, length);

        return SubscribedResponse.Instance;
    }

    public bool IsShallowCopyable() => true;
    public object DeepCopy(object input, CopyContext context) => input;
    public SubscribedResponse DeepCopy(SubscribedResponse input, CopyContext context) => input;
}

[RegisterSerializer, RegisterCopier]
internal sealed class UnknownTaskResponseCodec : IFieldCodec<UnknownTaskResponse>, IDeepCopier<UnknownTaskResponse>, IOptionalDeepCopier
{
    /// <inheritdoc />
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, UnknownTaskResponse value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.VarInt);
        writer.WriteByte(1);
    }

    /// <inheritdoc />
    public UnknownTaskResponse ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireType(WireType.VarInt);

        ReferenceCodec.MarkValueField(reader.Session);
        var length = reader.ReadVarUInt32();
        if (length != 0) throw new UnexpectedLengthPrefixValueException(nameof(UnknownTaskResponse), 0, length);

        return UnknownTaskResponse.Instance;
    }

    public bool IsShallowCopyable() => true;
    public object DeepCopy(object input, CopyContext context) => input;
    public UnknownTaskResponse DeepCopy(UnknownTaskResponse input, CopyContext context) => input;
}
