using System;
using Orleans.Connections;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionCommon
    {
        public ConnectionCommon(
            IServiceProvider serviceProvider,
            MessageFactory messageFactory,
            MessagingTrace messagingTrace,
            ConnectionTrace networkingTrace,
            MessageHandlerShared messageHandlerPool)
        {
            this.ServiceProvider = serviceProvider;
            this.MessageFactory = messageFactory;
            this.MessagingTrace = messagingTrace;
            this.ConnectionTrace = networkingTrace;
            this.MessageHandlerPool = messageHandlerPool;
        }

        public MessageFactory MessageFactory { get; }
        public IServiceProvider ServiceProvider { get; }
        public ConnectionTrace ConnectionTrace { get; }
        public MessagingTrace MessagingTrace { get; }
        public MessageHandlerShared MessageHandlerPool { get; }
    }
}
