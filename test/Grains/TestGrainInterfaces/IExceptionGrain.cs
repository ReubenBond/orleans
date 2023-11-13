using System.Buffers;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;

namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// The ExceptionGrain interface.
    /// </summary>
    [Alias("UnitTests.GrainInterfaces.IExceptionGrain")]
    public interface IExceptionGrain : IGrainWithIntegerKey
    {
        [Alias("Canceled")]
        Task Canceled();

        [Alias("ThrowsInvalidOperationException")]
        Task ThrowsInvalidOperationException();

        [Alias("ThrowsNullReferenceException")]
        Task ThrowsNullReferenceException();

        [Alias("ThrowsAggregateExceptionWrappingInvalidOperationException")]
        Task ThrowsAggregateExceptionWrappingInvalidOperationException();

        [Alias("ThrowsNestedAggregateExceptionsWrappingInvalidOperationException")]
        Task ThrowsNestedAggregateExceptionsWrappingInvalidOperationException();

        [Alias("GrainCallToThrowsInvalidOperationException")]
        Task GrainCallToThrowsInvalidOperationException(long otherGrainId);

        [Alias("GrainCallToThrowsAggregateExceptionWrappingInvalidOperationException")]
        Task GrainCallToThrowsAggregateExceptionWrappingInvalidOperationException(long otherGrainId);

        [Alias("ThrowsSynchronousInvalidOperationException")]
        Task ThrowsSynchronousInvalidOperationException();

        [Alias("ThrowsSynchronousExceptionObjectTask")]
        Task<object> ThrowsSynchronousExceptionObjectTask();

        [Alias("ThrowsMultipleExceptionsAggregatedInFaultedTask")]
        Task ThrowsMultipleExceptionsAggregatedInFaultedTask();

        [Alias("ThrowsSynchronousAggregateExceptionWithMultipleInnerExceptions")]
        Task ThrowsSynchronousAggregateExceptionWithMultipleInnerExceptions();
    }

    [Alias("UnitTests.GrainInterfaces.IMessageSerializationGrain")]
    public interface IMessageSerializationGrain : IGrainWithIntegerKey
    {
        [Alias("SendUnserializable")]
        Task SendUnserializable(UnserializableType input);
        [Alias("SendUndeserializable")]
        Task SendUndeserializable(UndeserializableType input);
        [Alias("GetUnserializable")]
        Task<UnserializableType> GetUnserializable();
        [Alias("GetUndeserializable")]
        Task<UndeserializableType> GetUndeserializable();

        [Alias("SendUnserializableToOtherSilo")]
        Task SendUnserializableToOtherSilo();
        [Alias("SendUndeserializableToOtherSilo")]
        Task SendUndeserializableToOtherSilo();
        [Alias("GetUnserializableFromOtherSilo")]
        Task GetUnserializableFromOtherSilo();
        [Alias("GetUndeserializableFromOtherSilo")]
        Task GetUndeserializableFromOtherSilo();

        [Alias("SendUnserializableToClient")]
        Task SendUnserializableToClient(IMessageSerializationClientObject obj);
        [Alias("SendUndeserializableToClient")]
        Task SendUndeserializableToClient(IMessageSerializationClientObject obj);
        [Alias("GetUnserializableFromClient")]
        Task GetUnserializableFromClient(IMessageSerializationClientObject obj);
        [Alias("GetUndeserializableFromClient")]
        Task GetUndeserializableFromClient(IMessageSerializationClientObject obj);

        [Alias("GetSiloIdentity")]
        Task<string> GetSiloIdentity();
    }

    [Alias("UnitTests.GrainInterfaces.IMessageSerializationClientObject")]
    public interface IMessageSerializationClientObject : IAddressable
    {
        [Alias("SendUnserializable")]
        Task SendUnserializable(UnserializableType input);
        [Alias("SendUndeserializable")]
        Task SendUndeserializable(UndeserializableType input);
        [Alias("GetUnserializable")]
        Task<UnserializableType> GetUnserializable();
        [Alias("GetUndeserializable")]
        Task<UndeserializableType> GetUndeserializable();
    }

    public struct UndeserializableType
    {
        public const string FailureMessage = "Can't do it, sorry.";

        public UndeserializableType(int num)
        {
            this.Number = num;
        }

        public int Number { get; }
    }

    public class UnserializableType
    {
    }

    [RegisterSerializer]
    [RegisterCopier]
    public sealed class UndeserializableTypeCodec : IFieldCodec<UndeserializableType>, IDeepCopier<UndeserializableType>
    {
        public UndeserializableType DeepCopy(UndeserializableType input, CopyContext context) => input;

        public UndeserializableType ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException(UndeserializableType.FailureMessage);
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, UndeserializableType value) where TBufferWriter : IBufferWriter<byte>
        {
            Int32Codec.WriteField(ref writer, fieldIdDelta, value.Number);
        }
    }

    [RegisterSerializer]
    [RegisterCopier]
    public sealed class UnserializableTypeCodec : IFieldCodec<UnserializableType>, IDeepCopier<UnserializableType>
    {
        public UnserializableType DeepCopy(UnserializableType input, CopyContext context) => input;

        public UnserializableType ReadValue<TInput>(ref Reader<TInput> reader, Field field) => default;
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, UnserializableType value) where TBufferWriter : IBufferWriter<byte>
        {
            throw new NotSupportedException(UndeserializableType.FailureMessage);
        }
    }
}