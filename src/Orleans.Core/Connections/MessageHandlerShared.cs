using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageHandlerShared
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentQueue<MessageReadRequest> _receivePool = new();
        private readonly ConcurrentQueue<MessageWriteRequest> _sendPool = new();
        private readonly ConcurrentQueue<MessageSerializer> _serializerPool = new();

        public MessageHandlerShared(
            MessagingTrace messagingTrace,
            IServiceProvider serviceProvider,
            MessageFactory messageFactory,
            IMessageCenter messageCenter)
        {
            MessagingTrace = messagingTrace;
            MessageFactory = messageFactory;
            MessageCenter = messageCenter;
            _serviceProvider = serviceProvider;
        }

        public MessagingTrace MessagingTrace { get; }
        public MessageFactory MessageFactory { get; }
        public IMessageCenter MessageCenter { get; }

        internal MessageSerializer GetMessageSerializer()
        {
            if (_serializerPool.TryDequeue(out var result))
            {
                return result;
            }

            return CreateMessageSerializer(this);
            static MessageSerializer CreateMessageSerializer(MessageHandlerShared self) => self._serviceProvider.GetRequiredService<MessageSerializer>();
        }

        internal void Return(MessageSerializer serializer)
        {
            _serializerPool.Enqueue(serializer);

            /*
            CheckPool();
            void CheckPool()
            {
                var uniqueBlocks = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var block in _serializerPool)
                {
                    Debug.Assert(uniqueBlocks.Add(block));
                }
            }
            */
        }

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

            /*
            CheckPool();
            void CheckPool()
            {
                var uniqueBlocks = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var block in _receivePool)
                {
                    Debug.Assert(uniqueBlocks.Add(block));
                }
            }
            */
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

            /*
            CheckPool();
            void CheckPool()
            {
                var uniqueBlocks = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var block in _sendPool)
                {
                    Debug.Assert(uniqueBlocks.Add(block));
                }
            }
            */
        }
    }
}
