using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal class SocketConnectionFactory : IConnectionFactory
    {
        private readonly ILoggerFactory loggerFactory;

        public SocketConnectionFactory(ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
        }

        public async Task<ConnectionContext> Connect(string endPoint)
        {
            if (!TryParseEndPoint(endPoint, out var remoteEndPoint))
            {
                throw new ArgumentException($"Unable to parse \"{endPoint}\" as {nameof(IPEndPoint)}");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var completion = new SingleUseSocketAsyncEventArgs
            {
                RemoteEndPoint = remoteEndPoint
            };

            if (!socket.ConnectAsync(completion))
            {
                completion.Complete();
            }

            await completion;

            if (completion.SocketError != SocketError.Success)
            {
                throw new Exception($"Unable to connect to {endPoint}. Error: {completion.SocketError}");
            }

            var connection = new SocketConnection(socket, MemoryPool<byte>.Shared, PipeScheduler.Inline, this.loggerFactory.CreateLogger<SocketConnection>());
            Task.Run(async () =>
            {
                try
                {
                    await connection.StartAsync();
                }
                finally
                {
                    connection.Abort();
                }
            }).Ignore();
            return connection;
        }

        private static bool TryParseEndPoint(string value, out IPEndPoint result)
        {
            if (!Uri.TryCreate($"tcp://{value}", UriKind.Absolute, out var uri) ||
                !IPAddress.TryParse(uri.Host, out var ipAddress) ||
                uri.Port < IPEndPoint.MinPort || uri.Port > IPEndPoint.MaxPort)
            {
                result = default;
                return false;
            }

            result = new IPEndPoint(ipAddress, uri.Port);
            return true;
        }
    }
}
