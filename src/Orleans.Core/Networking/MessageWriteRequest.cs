using System;
using Orleans.Serialization.Buffers;
using System.Buffers.Binary;
using Orleans.Connections.Transport;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageWriteRequest(MessageHandlerShared shared) : WriteRequest//, IValueTaskSource
    {
        private readonly MessageHandlerShared _shared = shared;
        private PooledBuffer _buffer = new();

        public Message Message { get; private set; }

        public void Initialize(Message message)
        {
            Message = message;
            SerializeAndFrameMessage();
        }

        public override ReadOnlyMemory<byte> Buffer => throw new InvalidOperationException();

        public override ref PooledBuffer Buffers => ref _buffer;

        private void SerializeAndFrameMessage()
        {
            // Reserve space for framing
            var framingBytes = _buffer.GetSpan(Message.LENGTH_HEADER_SIZE);
            _buffer.Advance(Message.LENGTH_HEADER_SIZE);

            // Serialize the message in full
            var messageSerializer = _shared.GetMessageSerializer();
            var (headerLength, bodyLength) = messageSerializer.Write(ref _buffer, Message);
            _shared.Return(messageSerializer);

            // Write the framing
            BinaryPrimitives.WriteInt32LittleEndian(framingBytes, headerLength);
            BinaryPrimitives.WriteInt32LittleEndian(framingBytes[sizeof(int)..], bodyLength);
        }

        public override void SetResult()
        {
            Reset();
        }

        public override void SetException(Exception error)
        {
            _shared.MessagingTrace.LogError(error, "Error sending message {Message}", Message);
            Reset();
        }

        public void Reset()
        {
            Message = null;
            _buffer.Reset();
            _shared.Return(this);
        }
    }
}
