using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
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
                var connectionManager = serviceProvider.GetRequiredService<ConnectionManager>();
                var components = serviceProvider.GetRequiredService<ConnectionComponentFactory>();
                
                return async (ConnectionContext connection) =>
                {
                    ConnectionMessageSender sender = default;
                    ConnectionMessageReceiver receiver = default;
                    try
                    {
                        sender = GetOrAddMessageSender(connection, serviceProvider);


                        ConnectionPreambleSender preambleSender;
                        ConnectionPreambleReceiver preambleReceiver;
                        (preambleSender, preambleReceiver, receiver) = components.GetComponents(outbound, siloToSilo, connection);
                        connection.Features.Set(receiver);

                        // Ok to yield execution after this point.
                        if (preambleSender != null) await preambleSender.WritePreamble(connection).ConfigureAwait(false);
                        if (preambleReceiver != null) await preambleReceiver.ReadPreamble(connection).ConfigureAwait(false);

                        // Start the sender/receiver after the handshake has completed.
                        var senderTask = sender.Run(connection);
                        var receiverTask = receiver.Run();
                        
                        await Task.WhenAny(senderTask, receiverTask).ConfigureAwait(false);
                    }
                    finally
                    {
                        sender?.Abort();
                        receiver?.Abort();
                    }
                };
            });
        }

        private static ConnectionMessageSender GetOrAddMessageSender(ConnectionContext connection, IServiceProvider serviceProvider)
        {
            var key = ConnectionMessageSender.ContextItemKey;
            if (connection.Items.TryGetValue(key, out var obj) && obj is ConnectionMessageSender sender)
            {
                return sender;
            }

            connection.Items[key] = sender = ActivatorUtilities.CreateInstance<ConnectionMessageSender>(serviceProvider);

            return sender;
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
            var preambleSender = GetPreambleSender(outbound);
            var receiver = ActivatorUtilities.CreateInstance<ClientMessageReceiver>(serviceProvider, connection);
            return (preambleSender, null, receiver);
        }

        private ConnectionPreambleSender GetPreambleSender(bool outbound)
        {
            if (!outbound)
            {
                return null;
            }

            return ActivatorUtilities.GetServiceOrCreateInstance<ClientPreambleSender>(serviceProvider);
        }
    }
}
