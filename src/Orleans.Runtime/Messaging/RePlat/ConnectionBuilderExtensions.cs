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
            return builder.RunOrleansConnectionHandler(outbound: false, siloToSilo: true);
        }

        public static IConnectionBuilder UseOrleansGatewayConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.RunOrleansConnectionHandler(outbound: false, siloToSilo: false);
        }

        public static IConnectionBuilder UseOrleansOutboundSiloConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.RunOrleansConnectionHandler(outbound: true, siloToSilo: true);
        }

        public static IConnectionBuilder UseOrleansOutboundClientConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.RunOrleansConnectionHandler(outbound: true, siloToSilo: false);
        }

        private static IConnectionBuilder RunOrleansConnectionHandler(this IConnectionBuilder builder, bool outbound, bool siloToSilo)
        {
            return builder.Use(_ =>
            {
                var serviceProvider = builder.ApplicationServices;
                var connectionManager = serviceProvider.GetRequiredService<ConnectionMessageSenderManager>();
                var preambleSender = GetPreambleSender(serviceProvider, outbound, siloToSilo);
                var preambleReceiver = GetPreambleReceiver(serviceProvider, outbound, siloToSilo);
                return async (ConnectionContext connection) =>
                {
                    ConnectionMessageSender sender = default;
                    ConnectionMessageReceiver receiver = default;

                    try
                    {
                        sender = ActivatorUtilities.CreateInstance<ConnectionMessageSender>(builder.ApplicationServices, connection);
                        connection.Features.Set(sender);
                        if (outbound)
                        {
                            var endPoint = connection.GetEndPoint();
                            connectionManager.Add(endPoint, sender);
                        }

                        receiver = ActivatorUtilities.CreateInstance<ConnectionMessageReceiver>(builder.ApplicationServices, connection);
                        connection.Features.Set(receiver);

                        // Ok to yield execution after this point.
                        if (preambleSender != null) await preambleSender.WritePreamble(connection);
                        if (preambleReceiver != null) await preambleReceiver.ReadPreamble(connection);

                        // Start the sender/receiver after the handshake has completed.
                        var senderTask = sender.Run();
                        var receiverTask = receiver.Run();
                        
                        await Task.WhenAny(senderTask, receiverTask).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (outbound && sender != null) connectionManager.Remove(connection.GetEndPoint(), sender);
                        sender?.Abort();
                        receiver?.Abort();
                    }
                };
            });
        }

        private static ConnectionPreambleReceiver GetPreambleReceiver(IServiceProvider serviceProvider, bool outbound, bool siloToSilo)
        {
            if (outbound)
            {
                return null;
            }

            if (siloToSilo) return ActivatorUtilities.GetServiceOrCreateInstance<SiloPreambleReceiver>(serviceProvider);
            else return ActivatorUtilities.GetServiceOrCreateInstance<GatewayPreambleReceiver>(serviceProvider);
        }

        private static ConnectionPreambleSender GetPreambleSender(IServiceProvider serviceProvider, bool outbound, bool siloToSilo)
        {
            if (!outbound)
            {
                return null;
            }

            if (siloToSilo) return ActivatorUtilities.GetServiceOrCreateInstance<SiloPreambleSender>(serviceProvider);
            else return ActivatorUtilities.GetServiceOrCreateInstance<ClientPreambleSender>(serviceProvider);
        }

        private static string GetEndPoint(this ConnectionContext connection)
        {
            var feature = connection.Features.Get<IHttpConnectionFeature>();
            if (feature == null) throw new ArgumentException($"Connection must have {nameof(IHttpConnectionFeature)}");
            return new IPEndPoint(feature.RemoteIpAddress, feature.RemotePort).ToString();
        }
    }
}
