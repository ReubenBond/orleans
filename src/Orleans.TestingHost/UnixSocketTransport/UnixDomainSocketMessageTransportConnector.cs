#nullable enable
using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Sockets;

namespace Orleans.TestingHost.UnixSocketTransport;

internal class UnixDomainSocketMessageTransportConnector : MessageTransportConnector
{
    public const string PathPropertyName = "path";
    private readonly ILogger _logger;

    public UnixDomainSocketMessageTransportConnector(string name, ILoggerFactory loggerFactory)
    {
        EndpointName = name;
        _logger = loggerFactory.CreateLogger("Orleans.Connections.Transport.Sockets");
    }

    /// <inheritdoc/>
    public override IFeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc/>
    public override bool IsValid => true;

    public override string EndpointName { get; }

    /// <inheritdoc/>
    public override async ValueTask<MessageTransport> CreateAsync(EndpointInfo endpointInfo, CancellationToken cancellationToken = default)
    {
        if (!endpointInfo.TryGetValue(PathPropertyName, out var path))
        {
            throw new ConnectionAbortedException($"Endpoint {endpointInfo.Name} is configured for Unix Domain Sockets but no destination path was specified");
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var unixEndpoint = new UnixDomainSocketEndPoint(path);
        await socket.ConnectAsync(unixEndpoint);

        var completion = new SingleUseAwaitableSocketAsyncEventArgs
        {
            RemoteEndPoint = unixEndpoint,
        };

        try
        {
            using var _ = cancellationToken.Register(static state => ((SingleUseAwaitableSocketAsyncEventArgs)state!).Cancel(), completion);

            if (!socket.ConnectAsync(completion))
            {
                completion.Complete();
            }

            if (!await completion)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (completion.SocketError != SocketError.Success)
            {
                throw new SocketConnectionException($"Unable to connect to {path}. Error: {completion.SocketError}");
            }

            var connection = new SocketMessageTransport(socket, _logger);
            connection.Start();
            return connection;
        }
        catch
        {
            socket.Dispose();
            completion.Dispose();
            throw;
        }
    }

    private class SingleUseAwaitableSocketAsyncEventArgs : SocketAsyncEventArgs, ICriticalNotifyCompletion
    {
        private readonly TaskCompletionSource<bool> _completion = new();

        public TaskAwaiter<bool> GetAwaiter() => _completion.Task.GetAwaiter();

        public void Cancel()
        {
            _completion.TrySetResult(false);
        }

        public void Complete() => _completion.TrySetResult(true);

        public void OnCompleted(Action continuation) => GetAwaiter().OnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation) => GetAwaiter().UnsafeOnCompleted(continuation);

        protected override void OnCompleted(SocketAsyncEventArgs _) => _completion.TrySetResult(true);
    }
}
