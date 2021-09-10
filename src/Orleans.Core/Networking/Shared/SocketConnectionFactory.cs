using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.Networking.Shared
{
    internal static class PipeConnectionRegistry
    {
        private static readonly ConcurrentDictionary<EndPoint, TaskCompletionSource<PipeConnectionListener>> Listeners = new();
        private static TaskCompletionSource<PipeConnectionListener> GetListenerCompletion(EndPoint endpoint) => Listeners.GetOrAdd(endpoint, _ => new TaskCompletionSource<PipeConnectionListener>(TaskCreationOptions.RunContinuationsAsynchronously));
        public static Task<PipeConnectionListener> GetListener(EndPoint endpoint) => GetListenerCompletion(endpoint).Task;
        public static void SetListener(PipeConnectionListener listener) => GetListenerCompletion(listener.EndPoint).SetResult(listener);
        public static void RemoveListener(PipeConnectionListener listener) => Listeners.TryRemove(listener.EndPoint, out _);
    }

    public static class PipeConnectionHostingExtensions
    {
        public static IServiceCollection AddPipeConnectionFactory(this IServiceCollection services)
        {
            services.AddSingletonNamedService<IConnectionListenerFactory>("Gateway", (sp, name) => ActivatorUtilities.CreateInstance<PipeConnectionFactory>(sp));
            services.AddSingletonNamedService<IConnectionListenerFactory>("Silo", (sp, name) => ActivatorUtilities.CreateInstance<PipeConnectionFactory>(sp));
            services.AddSingletonNamedService<IConnectionFactory>("Connection", (sp, name) => ActivatorUtilities.CreateInstance<PipeConnectionFactory>(sp));
            return services;
        }
    }

    internal class PipeConnectionFactory : IConnectionFactory, IConnectionListenerFactory
    {
        private readonly IPEndPoint _localEndpoint;
        private readonly MemoryPool<byte> _memoryPool;
        private readonly SocketSchedulers _schedulers;

        public PipeConnectionFactory(SocketSchedulers schedulers, SharedMemoryPool memoryPool)
        {
            _localEndpoint = new IPEndPoint(IPAddress.Loopback, ThreadSafeRandom.Next(ushort.MaxValue - 1));
            _memoryPool = memoryPool.Pool;
            _schedulers = schedulers;
        }

        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            var listener = new PipeConnectionListener { EndPoint = endpoint };
            PipeConnectionRegistry.SetListener(listener);
            return new(listener);
        }

        public async ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            var scheduler1 = _schedulers.GetScheduler();
            var scheduler2 = _schedulers.GetScheduler();
            var inputOptions = new PipeOptions(_memoryPool, scheduler2, scheduler1, 0, 0, useSynchronizationContext: false);
            var outputOptions = new PipeOptions(_memoryPool, scheduler1, scheduler2, 0, 0, useSynchronizationContext: false);
            var pair = DuplexPipe.CreateConnectionPair(inputOptions, outputOptions);

            var localContext = new PipeConnectionContext(pair.Application, pair.Transport, localEndpoint: _localEndpoint, remoteEndpoint: endpoint, _memoryPool);
            var remoteContext = new PipeConnectionContext(pair.Transport, pair.Application, localEndpoint: endpoint, remoteEndpoint: _localEndpoint, _memoryPool);
            var listener = await PipeConnectionRegistry.GetListener(endpoint);
            await listener.ConnectAsync(remoteContext);
            return localContext;
        }
    }

    internal class PipeConnectionContext : TransportConnection, IConnectionCloseFeature
    {
        private readonly CancellationTokenSource _connectionClosedTokenSource = new CancellationTokenSource();

        internal PipeConnectionContext(
            IDuplexPipe application,
            IDuplexPipe transport,
            EndPoint localEndpoint,
            EndPoint remoteEndpoint,
            MemoryPool<byte> memoryPool)
        {
            Debug.Assert(memoryPool != null);

            MemoryPool = memoryPool;

            LocalEndPoint = localEndpoint;
            RemoteEndPoint = remoteEndpoint;
            ConnectionClosed = _connectionClosedTokenSource.Token;
            Application = application;
            Transport = transport;
            this.Features[typeof(IConnectionCloseFeature)] = this;
        }

        public override MemoryPool<byte> MemoryPool { get; }

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            AbortInternal();
            return default;
        }

        private void AbortInternal()
        {
            _connectionClosedTokenSource.Cancel();
        }

        public override ValueTask DisposeAsync()
        {
            AbortInternal();
            _connectionClosedTokenSource.Dispose();
            return default;
        }
    }

    internal class PipeConnectionListener : IConnectionListener
    {
        private readonly Channel<ConnectionContext> _connectionRequests = Channel.CreateUnbounded<ConnectionContext>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false, AllowSynchronousContinuations = false });

        public EndPoint EndPoint { get; init; }

        public ValueTask ConnectAsync(ConnectionContext connection) => _connectionRequests.Writer.WriteAsync(connection);
        public ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default) => _connectionRequests.Reader.ReadAsync(cancellationToken);
        public ValueTask DisposeAsync()
        {
            PipeConnectionRegistry.RemoveListener(this);
            _connectionRequests.Writer.TryComplete();
            return default;
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default) => DisposeAsync();
    }

    internal class SocketConnectionFactory : IConnectionFactory
    {
        private readonly SocketsTrace trace;
        private readonly SocketSchedulers schedulers;
        private readonly MemoryPool<byte> memoryPool;

        public SocketConnectionFactory(ILoggerFactory loggerFactory, SocketSchedulers schedulers, SharedMemoryPool memoryPool)
        {
            var logger = loggerFactory.CreateLogger("Orleans.Sockets");
            this.trace = new SocketsTrace(logger);
            this.schedulers = schedulers;
            this.memoryPool = memoryPool.Pool;
        }

        public async ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken)
        {
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                LingerState = new LingerOption(true, 0),
                NoDelay = true
            };

            socket.EnableFastPath();
            using var completion = new SingleUseSocketAsyncEventArgs
            {
                RemoteEndPoint = endpoint
            };

            if (socket.ConnectAsync(completion))
            {
                using (cancellationToken.Register(s => Socket.CancelConnectAsync((SingleUseSocketAsyncEventArgs)s), completion))
                {
                    await completion.Task;
                }
            }

            if (completion.SocketError != SocketError.Success)
            {
                if (completion.SocketError == SocketError.OperationAborted)
                    cancellationToken.ThrowIfCancellationRequested();
                throw new SocketConnectionException($"Unable to connect to {endpoint}. Error: {completion.SocketError}");
            }

            var scheduler = this.schedulers.GetScheduler();
            var connection = new SocketConnection(socket, this.memoryPool, scheduler, this.trace);
            connection.Start();
            return connection;
        }

        private sealed class SingleUseSocketAsyncEventArgs : SocketAsyncEventArgs
        {
            private readonly TaskCompletionSource<object> completion = new();

            public Task Task => completion.Task;

            protected override void OnCompleted(SocketAsyncEventArgs _) => this.completion.TrySetResult(null);
        }
    }

    [Serializable]
    [GenerateSerializer]
    public sealed class SocketConnectionException : OrleansException
    {
        public SocketConnectionException(string message) : base(message) { }

        public SocketConnectionException(string message, Exception innerException) : base(message, innerException) { }

        public SocketConnectionException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}