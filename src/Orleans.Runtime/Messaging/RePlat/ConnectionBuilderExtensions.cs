using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
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
            return builder.RunOrleansConnectionHandler(outbound: false);
        }
        public static IConnectionBuilder UseOrleansGatewayConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.RunOrleansConnectionHandler(outbound: false);
        }

        public static IConnectionBuilder UseOrleansOutboundConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.RunOrleansConnectionHandler(outbound: true);
        }

        private static IConnectionBuilder RunOrleansConnectionHandler(this IConnectionBuilder builder, bool outbound)
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
                        if (outbound)
                        {
                            var endPoint = connection.GetEndPoint();
                            connectionManager.Add(endPoint, sender);
                        }

                        receiver = ActivatorUtilities.CreateInstance<ConnectionMessageReceiver>(builder.ApplicationServices, connection);
                        connection.Features.Set(receiver);

                        // Ok to yield execution after this point.

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

        private static string GetEndPoint(this ConnectionContext connection)
        {
            var feature = connection.Features.Get<IHttpConnectionFeature>();
            if (feature == null) throw new ArgumentException($"Connection must have {nameof(IHttpConnectionFeature)}");
            return new IPEndPoint(feature.RemoteIpAddress, feature.RemotePort).ToString();
        }
    }

    internal class ConnectionPreambleSender
    {
        private readonly GrainId id;

        public ConnectionPreambleSender(GrainId grainId)
        {
            this.id = grainId;
        }

        public Task SendPreamble(ConnectionContext connection)
        {
            var output = connection.Transport.Output;
            var grainIdByteArray = this.id.ToByteArray();

            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, grainIdByteArray.Length);
            var buffer = output.GetSpan(bytes.Length + grainIdByteArray.Length);
            bytes.CopyTo(buffer);
            new ReadOnlySpan<byte>(grainIdByteArray).CopyTo(buffer.Slice(sizeof(int)));
            output.Advance(buffer.Length);
            var flushTask = output.FlushAsync();

            if (flushTask.IsCompletedSuccessfully) return Task.CompletedTask;
            return FlushAsync(flushTask);

            async Task FlushAsync(ValueTask<FlushResult> task)
            {
                await task;
            }
        }
    }

    internal class ConnectionPreambleReceiver
    {
        private const int MaxPreambleLength = 1024;
        private readonly bool isProxy;
        public ConnectionPreambleReceiver(bool isProxy)
        {
            this.isProxy = isProxy;
        }

        public async Task ReceivePreamble(ConnectionContext context)
        {
            var input = context.Transport.Input;

            var readResult = await input.ReadAsync();
            var buffer = readResult.Buffer;
            while (buffer.Length < 4)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                readResult = await input.ReadAsync();
                buffer = readResult.Buffer;
            }

            int ReadLength(ref ReadOnlySequence<byte> b)
            {
                Span<byte> lengthBytes = stackalloc byte[4];
                b.Slice(0, 4).CopyTo(lengthBytes);
                b = b.Slice(4);
                return BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            }

            var length = ReadLength(ref buffer);
            if (buffer.Length > MaxPreambleLength)
            {
                throw new InvalidOperationException($"Remote connection sent preamble length of {length}, which is greater than maximum allowed size of {MaxPreambleLength}");
            }

            while (buffer.Length < length)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                readResult = await input.ReadAsync();
                buffer = readResult.Buffer;
            }

            var grainIdBytes = new byte[Math.Min(length, 1024)];

            buffer.Slice(0, length).CopyTo(grainIdBytes);
            var grainId = GrainIdExtensions.FromByteArray(grainIdBytes);

#warning validate grainId
        }
    }
}
