#nullable enable

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Net;

namespace Orleans.Networking.Transport.Sockets;

public class TcpMessageTransportFactory : MessageTransportFactory
{
    public TcpMessageTransportFactory(ILoggerFactory loggerFactory)
    {
        Logger = loggerFactory.CreateLogger("Orleans.Networking.Transport.Sockets");
    }

    protected ILogger Logger { get; }

    public override async ValueTask<MessageTransport> CreateAsync(EndpointInfo endpointInfo, CancellationToken cancellationToken = default)
    {
        var endpoint = endpointInfo.Endpoint;
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            LingerState = new LingerOption(true, 0),
            NoDelay = true
        };

        socket.EnableFastPath();
        var completion = new SingleUseSocketAsyncEventArgs
        {
            RemoteEndPoint = (EndPoint?)endpoint
        };
        
        if (!socket.ConnectAsync(completion))
        {
            completion.Complete();
        }

        await completion;

        if (completion.SocketError != SocketError.Success)
        {
            throw new SocketConnectionException($"Unable to connect to {endpoint}. Error: {completion.SocketError}");
        }

        var connection = new TcpMessageTransport(socket, Logger);
        connection.Start();
        return connection;
    }

    public class SingleUseSocketAsyncEventArgs : SocketAsyncEventArgs, ICriticalNotifyCompletion
    {
        private readonly TaskCompletionSource<SingleUseSocketAsyncEventArgs> _completion = new();

        public TaskAwaiter<SingleUseSocketAsyncEventArgs> GetAwaiter() => _completion.Task.GetAwaiter();

        public void Complete() => _completion.TrySetResult(this);

        public void OnCompleted(Action continuation) => GetAwaiter().OnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation) => GetAwaiter().UnsafeOnCompleted(continuation);

        protected override void OnCompleted(SocketAsyncEventArgs _) => _completion.TrySetResult(this);
    }
}
