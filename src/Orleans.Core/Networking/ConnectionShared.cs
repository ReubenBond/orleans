#nullable enable
using System;
using Orleans.Placement.Repartitioning;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionCommon(
        IServiceProvider serviceProvider,
        MessageFactory messageFactory,
        MessagingTrace messagingTrace,
        NetworkingTrace networkingTrace,
        IMessageStatisticsSink messageStatisticsSink)
    {
        public MessageFactory MessageFactory { get; } = messageFactory;
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public NetworkingTrace NetworkingTrace { get; } = networkingTrace;
        public Action<Message>? MessageStatisticsSink { get; } = messageStatisticsSink.GetMessageObserver();
        public MessagingTrace MessagingTrace { get; } = messagingTrace;
    }
}
