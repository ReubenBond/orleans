using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Buffers;
using System.Buffers.Binary;
using Orleans.Connections.Transport;
using System.Threading.Tasks.Sources;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageReadRequest : ReadRequest, IThreadPoolWorkItem, IValueTaskSource<bool>
    {
        internal readonly MessageHandlerShared Shared;

        private ManualResetValueTaskSourceCore<bool> _completion = new();
        private PooledBuffer _buffer = new();
        private Connection _connection;
        private (int HeaderLength, int BodyLength) _messageLength;

        public MessageReadRequest(MessageHandlerShared shared)
        {
            Shared = shared;
        }

        public ValueTask<bool> Completed => new(this, _completion.Version);
        public override Memory<byte> Buffer => _buffer.GetMemory();

        public int FramedLength => Message.LENGTH_HEADER_SIZE + _messageLength.HeaderLength + _messageLength.BodyLength;
        public int UnconsumedLength => _buffer.Length > FramedLength ? _buffer.Length - FramedLength : 0;

        public PooledBuffer.BufferSlice Payload => _buffer.Slice(Message.LENGTH_HEADER_SIZE, _messageLength.HeaderLength + _messageLength.BodyLength);
        public PooledBuffer.BufferSlice Unconsumed => _buffer.Slice(FramedLength);
        public PooledBuffer.BufferSlice Body => _buffer.Slice(Message.LENGTH_HEADER_SIZE + _messageLength.HeaderLength, _messageLength.BodyLength);
        public int BodyLength => _messageLength.BodyLength;

        public void SetConnection(Connection connection) => _connection = connection;

        public void SetBuffer(PooledBuffer buffer) => _buffer = buffer;

        public void Reset()
        {
            _messageLength = default;
            _connection = default;
            _completion.Reset();
            _buffer.Reset();
            Shared.Return(this);
        }

        public override void OnError(Exception error)
        {
            _completion.SetException(error);
        }

        public override void OnCanceled()
        {
            _completion.SetResult(false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

            _completion.SetResult(true);
            return true;
        }

        public override bool OnRead(int bytesRead)
        {
            if (bytesRead > 0)
            {
                _buffer.Advance(bytesRead);
            }

            return TryDeframeMessage();
        }

        void IThreadPoolWorkItem.Execute()
        {
            Message message = null;
            var shouldReset = true;
            var messageSerializer = Shared.GetMessageSerializer();
            try
            {
                messageSerializer.ReadHeaders(Payload, _messageLength.HeaderLength, _messageLength.BodyLength, out message);

                // Body deserialization is more likely to fail than header deserialization.
                // Separating the two allows for these kinds of errors to be propagated back to the caller.
                if (_messageLength.BodyLength > 0)
                {
                    // This instance is owned by the message now, so it will not be reset immediately.
                    message.SetMessageReadRequest(this);
                    shouldReset = false;
                }
                else
                {
                    // Otherwise, return this instance to the pool on exiting this method.
                }

                _connection.OnReceivedMessage(message);
            }
            catch (Exception exception)
            {
                if (!HandleReceiveMessageFailure(message, exception))
                {
                    throw;
                }
            }
            finally
            {
                if (shouldReset)
                {
                    Reset();
                }

                Shared.Return(messageSerializer);
            }

            bool HandleReceiveMessageFailure(Message message, Exception exception)
            {
                // If deserialization completely failed, rethrow the exception so that it can be handled at another level.
                if (message is null)
                {
                    Shared.MessagingTrace.LogWarning(
                        exception,
                        "Exception reading message from connection {Connection}",
                        _connection);

                    // Returning false here informs the caller that the exception should not be caught.
                    return false;
                }

                Shared.MessagingTrace.LogWarning(
                    exception,
                    "Exception reading message {Message} from connection {Connection}",
                    message,
                    _connection);

                // The message body was not successfully decoded, but the headers were.
                MessagingInstruments.OnRejectedMessage(message);

                if (message.HasDirection)
                {
                    if (message.Direction == Message.Directions.Request)
                    {
                        // Send a fast fail to the caller.
                        var response = Shared.MessageFactory.CreateResponseMessage(message);
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
                        Shared.MessageCenter.DispatchLocalMessage(message);
                    }
                }

                // The exception has been handled by propagating it onwards.
                return true;
            }
        }

        void IValueTaskSource<bool>.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
        bool IValueTaskSource<bool>.GetResult(short token) => _completion.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _completion.GetStatus(token);
    }
}
