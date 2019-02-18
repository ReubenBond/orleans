using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal class DuplexPipe : IDuplexPipe
    {
        public DuplexPipe(PipeReader reader, PipeWriter writer)
        {
            Input = reader;
            Output = writer;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }

        public static DuplexPipePair CreateConnectionPair(PipeOptions inputOptions, PipeOptions outputOptions)
        {
            var input = new Pipe(inputOptions);
            var output = new Pipe(outputOptions);

            var transportToApplication = new DuplexPipe(output.Reader, input.Writer);
            var applicationToTransport = new DuplexPipe(input.Reader, output.Writer);

            return new DuplexPipePair(applicationToTransport, transportToApplication);
        }

        // This class exists to work around issues with value tuple on .NET Framework
        public readonly struct DuplexPipePair
        {
            public IDuplexPipe Transport { get; }
            public IDuplexPipe Application { get; }

            public DuplexPipePair(IDuplexPipe transport, IDuplexPipe application)
            {
                Transport = transport;
                Application = application;
            }
        }
    }

    internal class SocketTransportFactory : IOutboundTransportFactory
    {
        private readonly ILoggerFactory loggerFactory;

        public SocketTransportFactory(ILoggerFactory loggerFactory)
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
            SetRecommendedClientOptions(socket);
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
            var pair = DuplexPipe.CreateConnectionPair(GetPipeOptions(PipeScheduler.ThreadPool, connection.InputWriterScheduler), GetPipeOptions(connection.OutputReaderScheduler, PipeScheduler.ThreadPool));
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

        /// <param name="socket">The socket to set options against</param>
        public static void SetRecommendedClientOptions(Socket socket)
        {
            if (socket.AddressFamily == AddressFamily.Unix) return;

            try { socket.NoDelay = true; } catch { }

            try { SetFastLoopbackOption(socket); } catch { }
            void SetFastLoopbackOption(Socket s)
            {
                // SIO_LOOPBACK_FAST_PATH (https://msdn.microsoft.com/en-us/library/windows/desktop/jj841212%28v=vs.85%29.aspx)
                // Speeds up localhost operations significantly. OK to apply to a socket that will not be hooked up to localhost,
                // or will be subject to WFP filtering.
                const int SIO_LOOPBACK_FAST_PATH = -1744830448;

                // windows only
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    // Win8/Server2012+ only
                    var osVersion = Environment.OSVersion.Version;
                    if (osVersion.Major > 6 || (osVersion.Major == 6 && osVersion.Minor >= 2))
                    {
                        byte[] optionInValue = BitConverter.GetBytes(1);
                        s.IOControl(SIO_LOOPBACK_FAST_PATH, optionInValue, null);
                    }
                }
            }
        }

        /// <summary>
        /// Set recommended socket options for server sockets
        /// </summary>
        /// <param name="socket">The socket to set options against</param>
        public static void SetRecommendedServerOptions(Socket socket)
        {
            if (socket.AddressFamily == AddressFamily.Unix) return;

            try { socket.NoDelay = true; } catch { }
        }

        internal static PipeOptions GetPipeOptions(PipeScheduler readerScheduler, PipeScheduler writerScheduler) => new PipeOptions(
            pool: MemoryPool<byte>.Shared,
            readerScheduler: readerScheduler,
            writerScheduler: writerScheduler,
            pauseWriterThreshold: 0,
            resumeWriterThreshold: 0,
            useSynchronizationContext: false,
            minimumSegmentSize: 4000);
        

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
