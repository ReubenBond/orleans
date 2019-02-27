using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloPreambleReceiver : ConnectionPreambleReceiver
    {
        public override async Task ReadPreamble(ConnectionContext connection)
        {
            var grainId = await base.ReadPreambleInternal(connection);

            if (!grainId.Equals(Constants.SiloDirectConnectionId))
            {
                throw new InvalidOperationException("Unexpected non-proxied connection on silo endpoint.");
            }
        }
    }
}
