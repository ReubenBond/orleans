using System;
using System.Buffers;
using System.Threading.Tasks;
using Orleans.Serialization.Buffers;
using System.Buffers.Binary;
using Orleans.Connections.Transport;
using System.Threading.Tasks.Sources;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageWriteRequest : WriteRequest//, IValueTaskSource
    {
        private readonly MessageHandlerShared _shared;
        //private ManualResetValueTaskSourceCore<int> _completion = new();
        private PooledBuffer _buffer = new();

        public MessageWriteRequest(MessageHandlerShared shared)
        {
            _shared = shared;
        }

        public Message Message { get; private set; }

        public void Initialize(Message message)
        {
            Message = message;

            // Reserve space for framing
            var framingBytes = _buffer.GetSpan(Message.LENGTH_HEADER_SIZE);
            _buffer.Advance(Message.LENGTH_HEADER_SIZE);

            // Serialize the message in full
            var messageSerializer = _shared.GetMessageSerializer();
            var (headerLength, bodyLength) = messageSerializer.Write(ref _buffer, message);
            _shared.Return(messageSerializer);

            // Write the framing
            BinaryPrimitives.WriteInt32LittleEndian(framingBytes, headerLength);
            BinaryPrimitives.WriteInt32LittleEndian(framingBytes[sizeof(int)..], bodyLength);
        }

        public override ReadOnlyMemory<byte> Buffer => throw new InvalidOperationException();

        public override ReadOnlySequence<byte> Buffers => _buffer.AsReadOnlySequence();

        //public ValueTask Completed => new(this, _completion.Version);

        public override void SetResult()
        {
            Reset();
        }

        public override void SetException(Exception error)
        {
            _shared.MessagingTrace.LogError(error, "Error sending message {Message}", Message);
            //_completion.SetException(error);
            Reset();
        }

        public void Reset()
        {
            Message = null;
            _buffer.Reset();
            //_completion.Reset();
            _shared.Return(this);
        }

        /*
        void IValueTaskSource.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
        void IValueTaskSource.GetResult(short token) => _completion.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _completion.GetStatus(token);
        */
    }
}
