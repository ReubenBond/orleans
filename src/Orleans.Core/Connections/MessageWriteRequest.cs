using System;
using System.Buffers;
using System.Threading.Tasks;
using Orleans.Serialization.Buffers;
using System.Buffers.Binary;
using Orleans.Connections.Transport;
using System.Threading.Tasks.Sources;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageWriteRequest : WriteRequest, IValueTaskSource
    {
        private readonly MessageHandlerShared _shared;
        private ManualResetValueTaskSourceCore<int> _completion = new();
        private PooledBuffer _buffer = new();

        public MessageWriteRequest(MessageHandlerShared shared)
        {
            _shared = shared;
        }

        public Message Message { get; private set; }

        public void Initialize(Message message)
        {
            Message = message;

            // Reserve some space for framing
            var framingBytes = _buffer.GetSpan(sizeof(int));
            _buffer.Advance(4);

            // Serialize the message in full
            var messageSerializer = _shared.GetMessageSerializer();
            messageSerializer.Write(ref _buffer, message);
            _shared.Return(messageSerializer);

            // Write the framing
            var length = _buffer.Length - sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(framingBytes, length);
        }

        public override ReadOnlyMemory<byte> Buffer => throw new InvalidOperationException();

        public override ReadOnlySequence<byte> Buffers => _buffer.AsReadOnlySequence();

        public ValueTask Completed => new(this, _completion.Version);

        public override void OnCompleted()
        {
            _completion.SetResult(0);
        }

        public override void OnError(Exception error)
        {
            _completion.SetException(error);
        }

        public void Reset()
        {
            Message = null;
            _buffer.Reset();
            _completion.Reset();
            _shared.Return(this);
        }

        void IValueTaskSource.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
        void IValueTaskSource.GetResult(short token) => _completion.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _completion.GetStatus(token);
    }
}
