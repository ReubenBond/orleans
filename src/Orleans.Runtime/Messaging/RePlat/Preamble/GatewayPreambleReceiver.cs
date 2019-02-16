using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal sealed class GatewayPreambleReceiver : ConnectionPreambleReceiver
    {
        private readonly Gateway gateway;
        private readonly ILocalSiloDetails siloDetails;
        private readonly MultiClusterOptions multiClusterOptions;
        private readonly ILogger<GatewayPreambleReceiver> log;

        public GatewayPreambleReceiver(MessageCenter messageCenter, ILocalSiloDetails siloDetails, ILogger<GatewayPreambleReceiver> log, IOptions<MultiClusterOptions> multiClusterOptions)
        {
            this.gateway = messageCenter.Gateway;
            this.siloDetails = siloDetails;
            this.log = log;
            this.multiClusterOptions = multiClusterOptions.Value;
        }

        public override async Task ReadPreamble(ConnectionContext connection)
        {
            connection.GetLifetime().ConnectionClosed.Register(() => this.gateway.RecordClosedConnection(connection));

            var grainId = await base.ReadPreambleInternal(connection).ConfigureAwait(false);

            if (grainId.Equals(Constants.SiloDirectConnectionId))
            {
                throw new InvalidOperationException("Unexpected direct silo connection on proxy endpoint.");
            }

            // refuse clients that are connecting to the wrong cluster
            if (grainId.Category == UniqueKey.Category.GeoClient)
            {
                if (grainId.Key.ClusterId != this.siloDetails.ClusterId)
                {
                    var message = string.Format(
                            "Refusing connection by client {0} because of cluster id mismatch: client={1} silo={2}",
                            grainId, grainId.Key.ClusterId, this.siloDetails.ClusterId);
                    this.log.Error(ErrorCode.GatewayAcceptor_WrongClusterId, message);
                    throw new InvalidOperationException(message);
                }
            }
            else
            {
                //convert handshake cliendId to a GeoClient ID 
                if (this.multiClusterOptions.HasMultiClusterNetwork)
                {
                    grainId = GrainId.NewClientId(grainId.PrimaryKey, this.siloDetails.ClusterId);
                }
            }

            this.gateway.RecordOpenedConnection(connection, grainId);            
        }
    }
}
