using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;

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
                var connectionManager = serviceProvider.GetRequiredService<ConnectionManager>();
                var components = serviceProvider.GetRequiredService<ConnectionComponentFactory>();
                
                return async (ConnectionContext connection) =>
                {
                    ConnectionMessageSender sender = default;
                    ConnectionMessageReceiver receiver = default;

                    try
                    {
                        sender = ActivatorUtilities.CreateInstance<ConnectionMessageSender>(serviceProvider, connection);
                        connection.Features.Set(sender);
                        if (outbound)
                        {
                            var endPoint = connection.GetRemoteEndPoint();
                            connectionManager.Add(endPoint, sender);
                        }

                        ConnectionPreambleSender preambleSender;
                        ConnectionPreambleReceiver preambleReceiver;
                        (preambleSender, preambleReceiver, receiver) = components.GetComponents(outbound, siloToSilo, connection);
                        connection.Features.Set(receiver);

                        // Ok to yield execution after this point.
                        if (preambleSender != null) await preambleSender.WritePreamble(connection).ConfigureAwait(false);
                        if (preambleReceiver != null) await preambleReceiver.ReadPreamble(connection).ConfigureAwait(false);

                        // Start the sender/receiver after the handshake has completed.
                        var senderTask = sender.Run();
                        var receiverTask = receiver.Run();
                        
                        await Task.WhenAny(senderTask, receiverTask).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (outbound && sender != null) connectionManager.Remove(connection.GetRemoteEndPoint(), sender);
                        sender?.Abort();
                        receiver?.Abort();
                    }
                };
            });
        }

    }

    internal abstract class ConnectionComponentFactory
    {
        public abstract (ConnectionPreambleSender, ConnectionPreambleReceiver, ConnectionMessageReceiver) GetComponents(bool outbound, bool siloToSilo, ConnectionContext connection);
    }

    internal sealed class ClientConnectionComponentFactory : ConnectionComponentFactory
    {
        private readonly IServiceProvider serviceProvider;

        public ClientConnectionComponentFactory(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public override (ConnectionPreambleSender, ConnectionPreambleReceiver, ConnectionMessageReceiver) GetComponents(bool outbound, bool siloToSilo, ConnectionContext connection)
        {
            var preambleSender = GetPreambleSender(outbound, siloToSilo);
            var preambleReceiver = GetPreambleReceiver(outbound, siloToSilo);
            var receiver = GetReceiver(outbound, siloToSilo, connection);
            return (preambleSender, preambleReceiver, receiver);
        }

        private ConnectionPreambleReceiver GetPreambleReceiver(bool outbound, bool siloToSilo)
        {
            return null;
        }

        private ConnectionPreambleSender GetPreambleSender(bool outbound, bool siloToSilo)
        {
            if (!outbound)
            {
                return null;
            }

            return ActivatorUtilities.GetServiceOrCreateInstance<ClientPreambleSender>(serviceProvider);
        }

        private ConnectionMessageReceiver GetReceiver(bool outbound, bool siloToSilo, ConnectionContext connection)
        {
            return ActivatorUtilities.CreateInstance<ClientMessageReceiver>(serviceProvider, connection);
        }
    }
}
