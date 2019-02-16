using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Serialization;

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
                        sender = ActivatorUtilities.CreateInstance<ConnectionMessageSender>(serviceProvider, connection);
                        connection.Features.Set(sender);
                        if (outbound)
                        {
                            var endPoint = connection.GetEndPoint();
                            connectionManager.Add(endPoint, sender);
                        }

                        receiver = GetReceiver(serviceProvider, outbound, siloToSilo, connection);
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

        private static ConnectionMessageReceiver GetReceiver(IServiceProvider serviceProvider, bool outbound, bool siloToSilo, ConnectionContext connection)
        {
            if (siloToSilo) return ActivatorUtilities.CreateInstance<SiloMessageReceiver>(serviceProvider, connection);
            else if (outbound) return ActivatorUtilities.CreateInstance<ClientMessageReceiver>(serviceProvider, connection);
            else return ActivatorUtilities.CreateInstance<GatewayMessageReceiver>(serviceProvider, connection);
        }

        private static string GetEndPoint(this ConnectionContext connection)
        {
            var feature = connection.Features.Get<IHttpConnectionFeature>();
            if (feature == null) throw new ArgumentException($"Connection must have {nameof(IHttpConnectionFeature)}");
            return new IPEndPoint(feature.RemoteIpAddress, feature.RemotePort).ToString();
        }
    }

    internal sealed class MessageSerializer : IMessageSerializer
    {
        private readonly SerializationManager serializationManager;

        public MessageSerializer(SerializationManager serializationManager)
        {
            this.serializationManager = serializationManager;
        }

        public int TryRead(ref ReadOnlySequence<byte> input, out Message message)
        {
            if (input.Length < 8)
            {
                message = default;
                return 8;
            }

            (int, int) ReadLengths(ReadOnlySequence<byte> b)
            {
                Span<byte> lengthBytes = stackalloc byte[8];
                b.Slice(0, 8).CopyTo(lengthBytes);
                return (BinaryPrimitives.ReadInt32LittleEndian(lengthBytes), BinaryPrimitives.ReadInt32LittleEndian(lengthBytes.Slice(4)));
            }

            var (headerLength, bodyLength) = ReadLengths(input);

            var requiredBytes = headerLength + bodyLength;
            if (input.Length < requiredBytes)
            {
                message = default;
                return requiredBytes;
            }

            // decode header
            int headerOffset = Message.LENGTH_HEADER_SIZE;
            var header = ByteArrayBuilder.BuildSegmentListWithLengthLimit(input, headerOffset, headerLength);

            // decode body
            int bodyOffset = headerOffset + headerLength;
            var body = ByteArrayBuilder.BuildSegmentListWithLengthLimit(input, bodyOffset, bodyLength);

            // build message
            var deserializationContext = new DeserializationContext(this.serializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(header)
            };

            message = new Message
            {
                Headers = SerializationManager.DeserializeMessageHeaders(deserializationContext)
            };
            message.DeserializeBodyObject(this.serializationManager, body);

            input = input.Slice(Message.LENGTH_HEADER_SIZE + requiredBytes);
            return 0;
        }

        public void Write<TBufferWriter>(ref TBufferWriter writer, Message message) where TBufferWriter : IBufferWriter<byte>
        {
            List<ArraySegment<byte>> data = message.Serialize(this.serializationManager, out var headerLength, out var bodyLength);
            foreach (var seg in data)
            {
                writer.Write(seg);
            }
        }
    }
}
