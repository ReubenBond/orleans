using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orleans.Runtime.Messaging
{
    public static class ConnectionBuilderExtensions
    {
        public static IConnectionBuilder UseOrleansOutboundClientConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.RunOrleansConnectionHandler(outbound: true, siloToSilo: false);
        }

        internal static IConnectionBuilder RunOrleansConnectionHandler(this IConnectionBuilder builder, bool outbound, bool siloToSilo)
        {
            return builder.Use(_ =>
            {
                var serviceProvider = builder.ApplicationServices;
                var lifetime = serviceProvider.GetRequiredService<IApplicationLifetime>();
                var components = serviceProvider.GetRequiredService<ConnectionComponentFactory>();
                var shutDown = lifetime.ApplicationStopped.WhenCancelled();

                return async (ConnectionContext connection) =>
                {
                    ConnectionMessageSender sender = default;
                    ConnectionMessageReceiver receiver = default;
                    Exception error = default;
                    try
                    {
                        sender = GetMessageSender(connection, serviceProvider);

                        ConnectionPreambleSender preambleSender;
                        ConnectionPreambleReceiver preambleReceiver;
                        (preambleSender, preambleReceiver, receiver) = components.GetComponents(outbound, siloToSilo, connection);
                        connection.Features.Set(receiver);

                        if (preambleSender != null) await preambleSender.WritePreamble(connection).ConfigureAwait(false);
                        if (preambleReceiver != null) await preambleReceiver.ReadPreamble(connection).ConfigureAwait(false);

                        // Start the sender/receiver after the handshake has completed.
                        var senderTask = sender.Run(connection);
                        var receiverTask = receiver.Run();

                        await Task.WhenAny(senderTask, receiverTask, shutDown).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        if (!(exception is ThreadAbortException) && !(exception is OperationCanceledException)) error = exception;
                    }
                    finally
                    {
                        sender?.Abort();

                        if (error != null)
                        {
                            connection.Abort(
                                new ConnectionAbortedException("Exception in connection handler. See InnerException for details.",
                                error));
                        }
                        else
                        {
                            connection.Abort();
                        }
                    }
                };
            });
        }

        private static ConnectionMessageSender GetMessageSender(ConnectionContext connection, IServiceProvider serviceProvider)
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
}
