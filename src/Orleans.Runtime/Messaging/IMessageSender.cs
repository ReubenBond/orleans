using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
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

    internal interface IConnectionMessageSender
    {
        void Send(Message message);
    }

    public static class ConnectionBuilderExtensions
    {
        public static IConnectionBuilder UseOrleansSiloConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.UseConnectionMessageSender();
        }
        public static IConnectionBuilder UseOrleansGatewayConnectionHandler(this IConnectionBuilder builder)
        {
            return builder.UseConnectionMessageSender();
        }

        private static IConnectionBuilder UseConnectionMessageSender(this IConnectionBuilder builder)
        {
            builder.Use(next =>
            {
                var connectionManager = builder.ApplicationServices.GetRequiredService<IConnectionManager>();
                return async (ConnectionContext connection) =>
                {
                    var sender = ActivatorUtilities.CreateInstance<ConnectionMessageSender>(builder.ApplicationServices, connection);
                    connection.Features.Set(sender);
                    sender.Start();

                    try
                    {
                        var nextTask = next(connection);
                        connectionManager.Add(connection);
                        await nextTask.ConfigureAwait(false);
                    }
                    finally
                    {
                        connectionManager.Remove(connection);
                        sender.Abort();
                    }
                };
            });
            return builder;
        }
    }

    public interface IRemoteEndPointFeature
    {
        IPEndPoint RemoteEndPoint { get; }
    }

    public interface IConnectionDependency
    {
        ValueTask Ready { get; }
    }

    public interface ILocalEndPointFeature
    {
        IPEndPoint LocalEndPoint { get; }
    }

    /// <summary>
    /// Options for inbound silo connections.
    /// </summary>
    public class SiloListenerOptions : ConnectionBuilder, ILocalEndPointFeature
    {
        private readonly EndpointOptions endPointOptions;

        public SiloListenerOptions(IServiceProvider applicationServices, IOptions<EndpointOptions> endPointOptions) : base(applicationServices)
        {
            this.endPointOptions = endPointOptions.Value;
        }

        public IPEndPoint LocalEndPoint => this.endPointOptions.GetListeningSiloEndpoint();
    }

    /// <summary>
    /// Options for inbound client connections.
    /// </summary>
    public class GatewayListenerOptions : ConnectionBuilder, ILocalEndPointFeature
    {
        private readonly EndpointOptions endPointOptions;

        public GatewayListenerOptions(IServiceProvider applicationServices, IOptions<EndpointOptions> endPointOptions) : base(applicationServices)
        {
            this.endPointOptions = endPointOptions.Value;
        }

        public IPEndPoint LocalEndPoint => this.endPointOptions.GetListeningProxyEndpoint();
    }

    /// <summary>
    /// Options for outbound connections.
    /// </summary>
    public class OutboundConnectionOptions : ConnectionBuilder
    {
        public OutboundConnectionOptions(IServiceProvider applicationServices) : base(applicationServices)
        {
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

    public interface IConnectionManager
    {
        void Add(ConnectionContext connection);
        void Remove(ConnectionContext connection);
        Task<ConnectionContext> GetConnection(IPEndPoint endPoint);
    }

    public interface IConnectionFactory
    {
        Task<ConnectionContext> Connect(IPEndPoint endPoint);
    }

    /*internal class KestrelSocketConnectionFactory : IConnectionFactory
    {
        public Task<ConnectionContext> Connect(IPEndPoint endPoint)
        {
            
        }
    }*/
    
    internal sealed class ConnectionManager : IConnectionManager
    {
        private readonly ConcurrentDictionary<IPEndPoint, TaskCompletionSource<ConnectionContext>> connections
            = new ConcurrentDictionary<IPEndPoint, TaskCompletionSource<ConnectionContext>>();

        public void Add(ConnectionContext connection)
        {
            var endPoint = GetEndPoint(connection);
            var updated = new TaskCompletionSource<ConnectionContext>();
            updated.SetResult(connection);

            var c = this.connections;
            TaskCompletionSource<ConnectionContext> existing = default;
            while (!c.TryAdd(endPoint, updated)
                && c.TryGetValue(endPoint, out existing)
                && !c.TryUpdate(endPoint, updated, existing))
            {
            }

            if (existing != null && !ReferenceEquals(existing, updated))
            {
                if (existing.TrySetResult(connection)) return;
                if (existing.Task.Status == TaskStatus.RanToCompletion)
                {
                    var e = existing.Task.GetAwaiter().GetResult();
                    e?.Abort();
                }
            }
        }

        public Task<ConnectionContext> GetConnection(IPEndPoint endPoint)
        {
            if (!this.connections.TryGetValue(endPoint, out var result))
            {
                var tcs = new TaskCompletionSource<ConnectionContext>();
                result = this.connections.GetOrAdd(endPoint, tcs);
                if (ReferenceEquals(result, tcs))
                {
                    Task.Run(() => ConnectAsync(endPoint, tcs));
                }
            }

            return result.Task;

            async Task ConnectAsync(IPEndPoint ep, TaskCompletionSource<ConnectionContext> completion)
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

        private bool TryReplace(IPEndPoint endPoint, TaskCompletionSource<ConnectionContext> replacement)
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

        public void Remove(ConnectionContext connection)
        {
            this.TryReplace(this.GetEndPoint(connection), new TaskCompletionSource<ConnectionContext>());
        }

        private IPEndPoint GetEndPoint(ConnectionContext connection)
        {
            var endPoint = connection.Features.Get<IRemoteEndPointFeature>()?.RemoteEndPoint;
            if (endPoint == null) throw new ArgumentException($"Connection must have {nameof(IRemoteEndPointFeature)}");
            return endPoint;
        }
    }

    internal interface IConnectionSenderManager
    {
        Task<IConnectionMessageSender> GetSender(IPEndPoint endPoint);
    }
}
