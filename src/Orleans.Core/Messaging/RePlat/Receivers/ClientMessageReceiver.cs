using Microsoft.AspNetCore.Connections;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientMessageReceiver : ConnectionMessageReceiver
    {
        private readonly ClientMessageCenter messageCenter;

        public ClientMessageReceiver(ConnectionContext connection, IMessageSerializer serializer, ClientMessageCenter messageCenter) : base(connection, serializer)
        {
            this.messageCenter = messageCenter;
        }

        protected override void OnReceivedMessage(Message message) => this.messageCenter.OnReceivedMessage(message);
    }
}
