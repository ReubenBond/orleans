using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal class SocketConnectionFactory : IConnectionFactory
    {
        private readonly SocketsTrace trace;

        public SocketConnectionFactory(ILoggerFactory loggerFactory)
        {
            var logger = loggerFactory.CreateLogger("Orleans.Sockets");
            this.trace = new SocketsTrace(logger);
        }

        public async Task<ConnectionContext> Connect(string endPoint)
        {
            if (!IPEndPointUtility.TryParseEndPoint(endPoint, out var remoteEndPoint))
            {
                throw new ArgumentException($"Unable to parse \"{endPoint}\" as {nameof(IPEndPoint)}");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.EnableRecommendedOptions();
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

            var connection = new SocketConnection(socket, MemoryPool<byte>.Shared, PipeScheduler.ThreadPool, this.trace);
            var pair = DuplexPipe.CreateConnectionPair(GetPipeOptions(PipeScheduler.Inline, connection.InputWriterScheduler), GetPipeOptions(connection.OutputReaderScheduler, PipeScheduler.Inline));
            connection.Application = pair.Application;
            connection.Transport = pair.Transport;
            Task.Run(async () =>
            {
                try
                {
                    await connection.StartAsync().ConfigureAwait(false);
                }
                finally
                {
                    connection.Abort();
                }
            }).Ignore();
            return connection;
        }

        internal static PipeOptions GetPipeOptions(PipeScheduler readerScheduler, PipeScheduler writerScheduler) => new PipeOptions(
            pool: MemoryPool<byte>.Shared,
            readerScheduler: readerScheduler,
            writerScheduler: writerScheduler,
            pauseWriterThreshold: 0,
            resumeWriterThreshold: 0,
            useSynchronizationContext: false,
            minimumSegmentSize: 4000);

        public class SingleUseSocketAsyncEventArgs : SocketAsyncEventArgs, ICriticalNotifyCompletion
        {
            private readonly TaskCompletionSource<SingleUseSocketAsyncEventArgs> completion
                = new TaskCompletionSource<SingleUseSocketAsyncEventArgs>();

            public TaskAwaiter<SingleUseSocketAsyncEventArgs> GetAwaiter() => this.completion.Task.GetAwaiter();

            public void Complete() => this.completion.TrySetResult(this);

            public void OnCompleted(Action continuation) => this.GetAwaiter().OnCompleted(continuation);

            public void UnsafeOnCompleted(Action continuation) => this.GetAwaiter().UnsafeOnCompleted(continuation);

            protected override void OnCompleted(SocketAsyncEventArgs _) => this.completion.TrySetResult(this);
        }
    }
}
