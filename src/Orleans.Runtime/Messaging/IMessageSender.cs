using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Orleans.Runtime.Messaging
{
    internal interface IMessageSerializer
    {
        void Write<TBufferWriter>(ref TBufferWriter writer, Message message) where TBufferWriter : IBufferWriter<byte>;
        
        /// <returns>
        /// The minimum number of bytes in <paramref name="input"/> before trying again, or 0 if a message was successfully read.
        /// </returns>
        int TryRead(ref ReadOnlySequence<byte> input, out Message message);
    }

    public static class ConnectionBuilderExtensions
    {
        public static IConnectionBuilder UseOrleansSiloConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.UseConnectionMessageSender(registerMessageSender: false);
        }
        public static IConnectionBuilder UseOrleansGatewayConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.UseConnectionMessageSender(registerMessageSender: false);
        }

        public static IConnectionBuilder UseOrleansOutgoingConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.UseConnectionMessageSender(registerMessageSender: true);
        }

        private static IConnectionBuilder UseConnectionMessageSender(this IConnectionBuilder builder, bool registerMessageSender)
        {
            return builder.Use(next =>
            {
                var connectionManager = builder.ApplicationServices.GetRequiredService<ConnectionMessageSenderManager>();
                return async (ConnectionContext connection) =>
                {
                    var sender = ActivatorUtilities.CreateInstance<ConnectionMessageSender>(builder.ApplicationServices, connection);
                    connection.Features.Set(sender);
                    sender.Start();

                    try
                    {
                        var nextTask = next(connection);

                        if (registerMessageSender)
                        {
                            var endPoint = connection.GetEndPoint();
                            connectionManager.Add(endPoint, sender);
                        }

                        await nextTask.ConfigureAwait(false);
                    }
                    finally
                    {
                        if (registerMessageSender) connectionManager.Remove(connection.GetEndPoint());
                        sender.Abort();
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

    internal sealed class ConnectionMessageSender : IDisposable
    {
        private static readonly UnboundedChannelOptions ChannelOptions = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        };

        private readonly Channel<Message> messages;
        private readonly ChannelWriter<Message> writer;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly IMessageCenter messageCenter;
        private readonly ConnectionContext connection;
        private readonly IMessageSerializer serializer;

        public ConnectionMessageSender(IMessageCenter messageCenter, ConnectionContext connection)
        {
            this.messages = Channel.CreateUnbounded<Message>(ChannelOptions);
            this.writer = this.messages.Writer;
            this.messageCenter = messageCenter;
            this.connection = connection;
            this.serializer = connection.Features.Get<IMessageSerializer>();
        }
            
        public void Start() => Task.Run(this.Process);

        public void Dispose() => this.Abort();

        public void Abort()
        {
            if (this.writer.TryComplete())
            {
                ThreadPool.UnsafeQueueUserWorkItem(cts => ((CancellationTokenSource)cts).Cancel(), this.cancellation);
            }
        }

        public void Send(Message message)
        {
            if (!this.writer.TryWrite(message))
            {
                this.RerouteMessage(message);
            }
        }
        private async Task Process()
        {
            var output = this.connection.Transport.Output;
            var reader = this.messages.Reader;
            try
            {
                while (!this.cancellation.IsCancellationRequested)
                {
                    var moreTask = reader.WaitToReadAsync();
                    var more = moreTask.IsCompleted ? moreTask.GetAwaiter().GetResult() : await moreTask.ConfigureAwait(false);
                    if (!more)
                    {
                        break;
                    }

                    while (reader.TryRead(out var message))
                    {
                        this.serializer.Write(ref output, message);
                    }

                    var flushTask = output.FlushAsync();
                    var flushResult = flushTask.IsCompleted ? flushTask.GetAwaiter().GetResult() : await flushTask.ConfigureAwait(false);
                    if (flushResult.IsCompleted || flushResult.IsCanceled) break;
                }
            }
            finally
            {
                while (reader.TryRead(out var message))
                {
                    this.RerouteMessage(message);
                }

                this.Abort();
                this.connection.Abort();
            }
        }

        private void RerouteMessage(Message message)
        {
            //TODO: is this correct?
            ThreadPool.UnsafeQueueUserWorkItem(msg => this.messageCenter.SendMessage((Message)msg), message);
        }
    }

    /*
    public interface IConnectionManager
    {
        void Add(ConnectionContext connection);
        void Remove(ConnectionContext connection);
        Task<ConnectionContext> GetConnection(IPEndPoint endPoint);
    }*/

    public interface IOutboundConnectionFactory
    {
        Task Connect(IPEndPoint endPoint, IConnectionDispatcher dispatcher);
    }

    public interface IConnectionDispatcher
    {
        Task OnConnected(ConnectionContext connection);
    }

    internal class SocketConnectionFactory : IOutboundConnectionFactory
    {
        private readonly ILoggerFactory loggerFactory;

        public SocketConnectionFactory(ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
        }

        public async Task Connect(IPEndPoint endPoint, IConnectionDispatcher dispatcher)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var completion = new SingleUseSocketAsyncEventArgs
            {
                RemoteEndPoint = endPoint
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
            var middlewareTask = dispatcher.OnConnected(connection);
            await connection.StartAsync();
            await middlewareTask;
        }
    }

    internal sealed class ConnectionMessageSenderManager
    {
        private readonly ConcurrentDictionary<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>> connections
            = new ConcurrentDictionary<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>>();
        
        public void Add(IPEndPoint endPoint, ConnectionMessageSender sender)
    {
            var updated = new TaskCompletionSource<ConnectionMessageSender>();
            updated.SetResult(sender);

            var c = this.connections;
            TaskCompletionSource<ConnectionMessageSender> existing = default;
            while (!c.TryAdd(endPoint, updated)
                && c.TryGetValue(endPoint, out existing)
                && !c.TryUpdate(endPoint, updated, existing))
            {
            }

            if (existing != null && !ReferenceEquals(existing, updated))
            {
                if (existing.TrySetResult(sender)) return;
                if (existing.Task.Status == TaskStatus.RanToCompletion)
                {
                    var e = existing.Task.GetAwaiter().GetResult();
                    e?.Abort();
                }
            }
        }

        public Task<ConnectionMessageSender> GetConnection(IPEndPoint endPoint)
    {
            if (!this.connections.TryGetValue(endPoint, out var result))
            {
                var tcs = new TaskCompletionSource<ConnectionMessageSender>();
                result = this.connections.GetOrAdd(endPoint, tcs);
                if (ReferenceEquals(result, tcs))
                {
                    Task.Run(() => ConnectAsync(endPoint, tcs));
                }
            }

            return result.Task;

            async Task ConnectAsync(IPEndPoint ep, TaskCompletionSource<ConnectionMessageSender> completion)
            {
                try
                {
                    await Task.CompletedTask;
                    completion.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    completion.TrySetCanceled();
                }
            }
        }

        private bool TryReplace(IPEndPoint endPoint, TaskCompletionSource<ConnectionMessageSender> replacement)
        {
            if (this.connections.TryGetValue(endPoint, out var tcs))
            {
                if (this.connections.TryUpdate(endPoint, replacement, tcs))
                {
                    return true;
                }
            }

            return false;
        }

        public void Remove(IPEndPoint endPoint)
        {
            this.TryReplace(endPoint, new TaskCompletionSource<ConnectionMessageSender>());
        }
    }
}
