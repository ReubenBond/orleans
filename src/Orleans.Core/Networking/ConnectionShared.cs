using System;
using Orleans.Serialization.Session;
using static Orleans.Runtime.Messaging.Connection;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionCommon
    {
        public ConnectionCommon(
            IServiceProvider serviceProvider,
            MessageFactory messageFactory,
            MessagingTrace messagingTrace,
            NetworkingTrace networkingTrace,
            SerializerSessionPool sessionPool,
            MessageSerializer messageSerializer)
        {
            ServiceProvider = serviceProvider;
            MessageFactory = messageFactory;
            MessagingTrace = messagingTrace;
            NetworkingTrace = networkingTrace;
            IncomingMessageWorkerPool = new IncomingMessageWorkerPool(sessionPool, messageSerializer);
            IncomingMessageWorkerPool.Start();
        }

        public IncomingMessageWorkerPool IncomingMessageWorkerPool { get; } 
        public MessageFactory MessageFactory { get; }
        public IServiceProvider ServiceProvider { get; }
        public NetworkingTrace NetworkingTrace { get; }
        public MessagingTrace MessagingTrace { get; }
    }
}
