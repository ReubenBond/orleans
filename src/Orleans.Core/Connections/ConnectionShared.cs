using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Connections;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionCommon
    {
        private readonly object _lock = new();
        private MessageHandlerShared _messageHandlerShared;
        public ConnectionCommon(
            IServiceProvider serviceProvider,
            MessageFactory messageFactory,
            MessagingTrace messagingTrace,
            ConnectionTrace networkingTrace)
        {
            this.ServiceProvider = serviceProvider;
            this.MessageFactory = messageFactory;
            this.MessagingTrace = messagingTrace;
            this.ConnectionTrace = networkingTrace;
        }

        public MessageFactory MessageFactory { get; }
        public IServiceProvider ServiceProvider { get; }
        public ConnectionTrace ConnectionTrace { get; }
        public MessagingTrace MessagingTrace { get; }

        public MessageHandlerShared MessageHandlerShared
        {
            get
            {
                if (_messageHandlerShared is { } value) return value;
                lock (_lock)
                {
                    return _messageHandlerShared ??= ServiceProvider.GetRequiredService<MessageHandlerShared>();
                }
            }
        }
    }
}
