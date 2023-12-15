using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageHandlerShared(
        MessagingTrace messagingTrace,
        IServiceProvider serviceProvider,
        MessageFactory messageFactory,
        IMessageCenter messageCenter)
    {
        private readonly IServiceProvider _serviceProvider = serviceProvider;
        private readonly ConcurrentQueue<MessageReadRequest> _receivePool = new();
        private readonly ConcurrentQueue<MessageWriteRequest> _sendPool = new();
        private readonly ConcurrentQueue<MessageSerializer> _serializerPool = new();

        public MessagingTrace MessagingTrace { get; } = messagingTrace;
        public MessageFactory MessageFactory { get; } = messageFactory;
        public IMessageCenter MessageCenter { get; } = messageCenter;

        internal MessageSerializer GetMessageSerializer()
        {
            if (_serializerPool.TryDequeue(out var result))
            {
                return result;
            }

            return _serviceProvider.GetRequiredService<MessageSerializer>();
        }

        internal void Return(MessageSerializer serializer) => _serializerPool.Enqueue(serializer);

        internal MessageReadRequest GetReceiveMessageHandler()
        {
            if (_receivePool.TryDequeue(out var result))
            {
                return result;
            }

            return new(this);
        }

        internal void Return(MessageReadRequest handler)
        {
            _receivePool.Enqueue(handler);
        }

        internal MessageWriteRequest GetSendMessageHandler()
        {
            if (_sendPool.TryDequeue(out var result))
            {
                return result;
            }

            return new(this);
        }

        internal void Return(MessageWriteRequest handler) => _sendPool.Enqueue(handler);
    }
}
