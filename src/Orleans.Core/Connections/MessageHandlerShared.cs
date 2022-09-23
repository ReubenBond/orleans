using System.Collections.Concurrent;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageHandlerShared
    {
        private ConcurrentQueue<MessageReadRequest> _receivePool = new();
        private ConcurrentQueue<MessageWriteRequest> _sendPool = new();

        public MessageHandlerShared(
            MessagingTrace messagingTrace,
            MessageSerializer messageSerializer,
            MessageFactory messageFactory,
            IMessageCenter messageCenter)
        {
            MessagingTrace = messagingTrace;
            MessageSerializer = messageSerializer;
            MessageFactory = messageFactory;
            MessageCenter = messageCenter;
        }

        public MessagingTrace MessagingTrace { get; }
        public MessageSerializer MessageSerializer { get; }
        public MessageFactory MessageFactory { get; }
        public IMessageCenter MessageCenter { get; }

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

        internal void Return(MessageWriteRequest handler)
        {
            _sendPool.Enqueue(handler);
        }
    }
}
