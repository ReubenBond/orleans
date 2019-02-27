using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientPreambleSender : ConnectionPreambleSender
    {
        private readonly ClientMessageCenter clientMessageCenter;

        public ClientPreambleSender(ClientMessageCenter clientMessageCenter)
        {
            this.clientMessageCenter = clientMessageCenter;
        }

        public override Task WritePreamble(ConnectionContext connection) => base.WritePreambleInternal(connection, this.clientMessageCenter.ClientId);
    }
}
