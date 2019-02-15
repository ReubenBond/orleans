using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Messaging
{
    public static class ConnectionBuilderExtensions
    {
        public static IConnectionBuilder UseOrleansSiloConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.RunOrleansConnectionHandler(registerMessageSender: false);
        }
        public static IConnectionBuilder UseOrleansGatewayConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.RunOrleansConnectionHandler(registerMessageSender: false);
        }

        public static IConnectionBuilder UseOrleansOutgoingConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.RunOrleansConnectionHandler(registerMessageSender: true);
        }

        private static IConnectionBuilder RunOrleansConnectionHandler(this IConnectionBuilder builder, bool registerMessageSender)
        {
            return builder.Use(_ =>
            {
                var connectionManager = builder.ApplicationServices.GetRequiredService<ConnectionMessageSenderManager>();
                return async (ConnectionContext connection) =>
                {
                    ConnectionMessageSender sender = default;
                    ConnectionMessageReceiver receiver = default;

                    try
                    {
                        sender = ActivatorUtilities.CreateInstance<ConnectionMessageSender>(builder.ApplicationServices, connection);
                        connection.Features.Set(sender);
                        if (registerMessageSender)
                        {
                            var endPoint = connection.GetEndPoint();
                            connectionManager.Add(endPoint, sender);
                        }

                        receiver = ActivatorUtilities.CreateInstance<ConnectionMessageReceiver>(builder.ApplicationServices, connection);
                        connection.Features.Set(receiver);

                        var senderTask = sender.Run();
                        var receiverTask = receiver.Run();
                        
                        await Task.WhenAny(senderTask, receiverTask).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (registerMessageSender && sender != null) connectionManager.Remove(connection.GetEndPoint(), sender);
                        sender?.Abort();
                        receiver?.Abort();
                    }
                };
            });
        }

        private static IPEndPoint GetEndPoint(this ConnectionContext connection)
        {
            var feature = connection.Features.Get<IHttpConnectionFeature>();
            if (feature == null) throw new ArgumentException($"Connection must have {nameof(IHttpConnectionFeature)}");
            return new IPEndPoint(feature.RemoteIpAddress, feature.RemotePort);
        }
    }
}
