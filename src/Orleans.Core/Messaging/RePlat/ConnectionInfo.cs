using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionInfo
    {
        public ConnectionInfo(ConnectionContext context)
        {
            this.Context = context;
            this.RemoteEndPoint = this.Context.GetRemoteEndPoint();
            this.Sender = this.Context.GetMessageSender();
        }

        public ConnectionContext Context { get; }

        public string RemoteEndPoint { get; private set; }
        public ConnectionMessageSender Sender { get; private set; }
        public IConnectionLifetimeFeature Lifetime => this.Context.GetLifetime();
        public ConnectionMessageReceiver Receiver => this.Context.GetRequiredFeature<ConnectionMessageReceiver>();
    }
}
