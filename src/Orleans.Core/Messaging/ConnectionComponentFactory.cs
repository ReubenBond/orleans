using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    internal abstract class ConnectionComponentFactory
    {
        public abstract (ConnectionPreambleSender, ConnectionPreambleReceiver, ConnectionMessageReceiver) GetComponents(bool outbound, bool siloToSilo, ConnectionContext connection);
    }
}
