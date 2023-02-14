using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Buffers;
using System.Buffers.Binary;
using Orleans.Connections.Transport;
using System.Threading.Tasks.Sources;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageReadRequest : ReadRequest, IThreadPoolWorkItem, IValueTaskSource
    {
        private readonly MessageHandlerShared _shared;

        private ManualResetValueTaskSourceCore<int> _completion = new();
        private PooledBuffer _buffer = new();
        private Connection _connection;
        private (int HeaderLength, int BodyLength) _messageLength;

        public MessageReadRequest(MessageHandlerShared shared)
        {
            _shared = shared;
        }

        public ValueTask Completed => new(this, _completion.Version);
        public override Memory<byte> Buffer => _buffer.GetMemory();

        public int FramedLength => Message.LENGTH_HEADER_SIZE + _messageLength.HeaderLength + _messageLength.BodyLength;
        public int UnconsumedLength => _buffer.Length > FramedLength ? _buffer.Length - FramedLength : 0;

        public PooledBuffer.BufferSlice Payload => _buffer.Slice(Message.LENGTH_HEADER_SIZE, _messageLength.HeaderLength + _messageLength.BodyLength);
        public PooledBuffer.BufferSlice Unconsumed => _buffer.Slice(FramedLength);

        public void SetConnection(Connection connection)
        {
            _connection = connection;
        }

        public void SetBuffer(in PooledBuffer buffer)
        {
            _buffer = buffer;
        }

        public void Reset()
        {
            _messageLength = default;
            _connection = default;
            _completion.Reset();
            _buffer.Reset();
            _shared.Return(this);
        }

        public override void OnError(Exception error)
        {
            _completion.SetException(error);
        }

        public bool TryDeframeMessage()
        {
            if (_buffer.Length < Message.LENGTH_HEADER_SIZE)
            {
                return false;
            }

            if (_messageLength.HeaderLength == 0)
            {
                Span<byte> lengthBytes = stackalloc byte[Message.LENGTH_HEADER_SIZE];
                _buffer.CopyTo(lengthBytes);
                _messageLength = (BinaryPrimitives.ReadInt32LittleEndian(lengthBytes), BinaryPrimitives.ReadInt32LittleEndian(lengthBytes[sizeof(int)..]));
            }

            if (_buffer.Length < FramedLength)
            {
                return false;
            }

            _completion.SetResult(0);
            return true;
        }

        public override bool OnProgress(int bytesRead)
        {
            if (bytesRead > 0)
            {
                _buffer.Advance(bytesRead);
            }

            return TryDeframeMessage();
        }

        internal Message TestReadMessage()
        {
            var messageSerializer = _shared.GetMessageSerializer();
            try
            {
                var buffer = Payload;
                messageSerializer.Read(in buffer, _messageLength.HeaderLength, _messageLength.BodyLength, out var message);
                return message;
            }
            finally
            {
                _shared.Return(messageSerializer);
                Reset();
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            Message message = null;
            var messageSerializer = _shared.GetMessageSerializer();
            try
            {
                var buffer = Payload;
                messageSerializer.Read(in buffer, _messageLength.HeaderLength, _messageLength.BodyLength, out message);
                _connection.OnReceivedMessage(message);
            }
            catch (Exception exception) when (HandleReceiveMessageFailure(message, exception))
            {
            }
            finally
            {
                _shared.Return(messageSerializer);
                Reset();
            }

            bool HandleReceiveMessageFailure(Message message, Exception exception)
            {
                _shared.MessagingTrace.LogWarning(
                    exception,
                    "Exception reading message {Message} from remote endpoint {Remote} to local endpoint {Local}",
                    message,
                    _connection.RemoteEndpoint,
                    _connection.LocalEndpoint);

                // If deserialization completely failed, rethrow the exception so that it can be handled at another level.
                if (message is null)
                {
                    // Returning false here informs the caller that the exception should not be caught.
                    return false;
                }

                // The message body was not successfully decoded, but the headers were.
                MessagingInstruments.OnRejectedMessage(message);

                if (message.HasDirection)
                {
                    if (message.Direction == Message.Directions.Request)
                    {
                        // Send a fast fail to the caller.
                        var response = _shared.MessageFactory.CreateResponseMessage(message);
                        response.Result = Message.ResponseTypes.Error;
                        response.BodyObject = Response.FromException(exception);

                        // Send the error response and continue processing the next message.
                        _connection.Send(response);
                    }
                    else if (message.Direction == Message.Directions.Response)
                    {
                        // If the message was a response, propagate the exception to the intended recipient.
                        message.Result = Message.ResponseTypes.Error;
                        message.BodyObject = Response.FromException(exception);
                        _shared.MessageCenter.DispatchLocalMessage(message);
                    }
                }

                // The exception has been handled by propagating it onwards.
                return true;
            }
        }

        void IValueTaskSource.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
        void IValueTaskSource.GetResult(short token) => _completion.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _completion.GetStatus(token);
    }
}
