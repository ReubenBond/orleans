using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloPreambleSender : ConnectionPreambleSender
    {
        public override Task WritePreamble(ConnectionContext connection) => base.WritePreambleInternal(connection, Constants.SiloDirectConnectionId);
    }
}
