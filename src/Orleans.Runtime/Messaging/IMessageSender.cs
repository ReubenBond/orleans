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
            
        public Task Run() => Task.Run(this.Process);

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

    internal sealed class ConnectionMessageReceiver
    {
        private readonly ConnectionContext connection;
        private readonly IMessageCenter messageCenter;
        private readonly IMessageSerializer serializer;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

        public ConnectionMessageReceiver(ConnectionContext connection, IMessageCenter messageCenter, IMessageSerializer serializer)
        {
            this.connection = connection;
            this.messageCenter = messageCenter;
            this.serializer = serializer;
        }

        public void Abort()
        {
            ThreadPool.UnsafeQueueUserWorkItem(cts => ((CancellationTokenSource)cts).Cancel(), this.cancellation);
        }

        public Task Run() => Task.Run(this.Process);

        public async Task Process()
        {
            var input = this.connection.Transport.Input;
            var error = default(Exception);
            try
            {
                var requiredBytes = 0;
                while (!this.cancellation.IsCancellationRequested)
                {
                    if (!input.TryRead(out var readResult)) readResult = await input.ReadAsync(this.cancellation.Token);
                    
                    var buffer = readResult.Buffer;
                    
                    var start = buffer.Start;
                    if (buffer.Length >= requiredBytes)
                    {
                        do
                        {
                            requiredBytes = this.serializer.TryRead(ref buffer, out var message);
                            if (requiredBytes == 0)
                            {
                                this.messageCenter.OnReceivedMessage(message);
                            }
                        } while (requiredBytes == 0);
                    }

                    if (readResult.IsCanceled || readResult.IsCompleted) break;
                    input.AdvanceTo(start, buffer.End);
                }
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                this.Abort();

                if (error != null)
                {
                    this.connection.Abort(new ConnectionAbortedException($"Exception in {nameof(ConnectionMessageReceiver)}, see {nameof(Exception.InnerException)}.", error));
                }
                else
                {
                    this.connection.Abort();
                }
            }
        }
    }
    
    public interface IOutboundConnectionFactory
    {
        Task<ConnectionContext> Connect(IPEndPoint endPoint);
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

        public async Task<ConnectionContext> Connect(IPEndPoint endPoint)
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
    }

    internal sealed class OutboundConnectionBuilder : ConnectionBuilder
    {
        public OutboundConnectionBuilder(IServiceProvider applicationServices) : base(applicationServices)
        {
        }
    }

    internal sealed class ConnectionMessageSenderManager
    {
        private readonly ConcurrentDictionary<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>> connections
            = new ConcurrentDictionary<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>>();
        private readonly IOutboundConnectionFactory connectionFactory;
        private readonly ConnectionDelegate connectionDelegate;

        public ConnectionMessageSenderManager(IOutboundConnectionFactory connectionFactory, IServiceProvider serviceProvider, IOptions<EndpointOptions> endpointOptions)
        {
            this.connectionFactory = connectionFactory;

            // Configure the connection builder using the user-defined options.
            var connectionBuilder = ActivatorUtilities.CreateInstance<OutboundConnectionBuilder>(serviceProvider);
            connectionBuilder.UseOrleansOutgoingConnectionHandler();
            endpointOptions.Value.ConfigureOutboundConnectionBuilder(connectionBuilder);
            this.connectionDelegate = connectionBuilder.Build();
        }

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
            this.connections.TryGetValue(endPoint, out var result);

            // Clean up defunct connections.
            if (result != null && result.Task.IsCompleted)
            {
                var status = result.Task.Status;
                if (status == TaskStatus.Canceled || status == TaskStatus.Faulted)
                {
                    var item = new KeyValuePair<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>>(endPoint, result);
                    ((IDictionary<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>>)this.connections).Remove(item);
                    result = default;
                }
            }

            if (result == null)
            {
                var tcs = new TaskCompletionSource<ConnectionMessageSender>();
                result = this.connections.GetOrAdd(endPoint, tcs);
                if (ReferenceEquals(result, tcs))
                {
                    Task.Run(() => ConnectAsync(endPoint, tcs));
                }
            }

            return result.Task;
        }

        public void Remove(IPEndPoint endPoint, ConnectionMessageSender connection = null)
        {
            if (this.connections.TryGetValue(endPoint, out var tcs))
            {
                var status = tcs.Task.Status;

                if (status == TaskStatus.RanToCompletion && ReferenceEquals(tcs.Task.GetAwaiter().GetResult(), connection)
                    || (status == TaskStatus.Canceled || status == TaskStatus.Faulted))
                {
                    var item = new KeyValuePair<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>>(endPoint, tcs);
                    ((IDictionary<IPEndPoint, TaskCompletionSource<ConnectionMessageSender>>)this.connections).Remove(item);
                }
            }
        }

        private async Task ConnectAsync(IPEndPoint endPoint, TaskCompletionSource<ConnectionMessageSender> completion)
        {
            try
            {
                var context = await this.connectionFactory.Connect(endPoint);
                var middlewareTask = this.connectionDelegate(context);
                var sender = context.Features.Get<ConnectionMessageSender>();
                if (sender == null)
                {
                    var exception = new ConnectionAbortedException($"Connection does not have the required {nameof(ConnectionMessageSender)} feature");
                    context.Abort(exception);
                    throw exception;
                }

                Task.Run(async () =>
                {
                    try
                    {
                        await middlewareTask.ConfigureAwait(false);
                    }
                    finally
                    {
                        // Remove the defunct connection.
                        context.Abort();
                        this.connections.TryUpdate(endPoint, new TaskCompletionSource<ConnectionMessageSender>(), completion);
                    }
                }).Ignore();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                this.Remove(endPoint, null);
            }
            finally
            {
                completion.TrySetCanceled();
            }
        }
    }
}
