using System;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionCommon
    {
        public ConnectionCommon(
            IServiceProvider serviceProvider,
            MessageFactory messageFactory,
            MessagingTrace messagingTrace,
            NetworkingTrace networkingTrace,
            MessageHandlerShared messageHandlerPool)
        {
            this.ServiceProvider = serviceProvider;
            this.MessageFactory = messageFactory;
            this.MessagingTrace = messagingTrace;
            this.NetworkingTrace = networkingTrace;
            this.MessageHandlerPool = messageHandlerPool;
        }

        public MessageFactory MessageFactory { get; }
        public IServiceProvider ServiceProvider { get; }
        public NetworkingTrace NetworkingTrace { get; }
        public MessagingTrace MessagingTrace { get; }
        public MessageHandlerShared MessageHandlerPool { get; }
    }
}
