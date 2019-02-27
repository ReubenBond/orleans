using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SocketConnectionListener : IConnectionListener
    {
        private readonly ConnectionDelegate connectionDelegate;
        private readonly IApplicationLifetime applicationLifetime;
        private readonly ISocketsTrace trace;
        private readonly SocketSchedulers schedulers;
        private readonly MemoryPool<byte> memoryPool;
        private Socket listenSocket;
        private Task listenTask;
        private Exception listenException;
        private IPEndPoint endPoint;
        private volatile bool unbinding;

        internal SocketConnectionListener(
            string endPoint,
            ConnectionDelegate connectionDelegate,
            IApplicationLifetime applicationLifetime,
            ISocketsTrace trace,
            SocketSchedulers schedulers,
            SharedMemoryPool memoryPool)
        {
            Debug.Assert(endPoint != null);
            Debug.Assert(connectionDelegate != null);
            Debug.Assert(applicationLifetime != null);
            Debug.Assert(trace != null);

            if (!IPEndPointUtility.TryParseEndPoint(endPoint, out this.endPoint))
            {
                throw new ArgumentException($"Unable to parse {endPoint} as {(nameof(IPEndPoint))}.");
            }

            this.connectionDelegate = connectionDelegate;
            this.applicationLifetime = applicationLifetime;
            this.trace = trace;
            this.schedulers = schedulers;
            this.memoryPool = memoryPool.Pool;
        }

        public Task Bind()
        {
            if (this.listenSocket != null)
            {
                throw new InvalidOperationException($"Address {this.endPoint} already bound.");
            }
            
            var newListenSocket = new Socket(this.endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            // Prep the socket so it will reset on close
            newListenSocket.LingerState = new LingerOption(true, 0);
            newListenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            newListenSocket.EnableFastPath();

            // Kestrel expects IPv6Any to bind to both IPv6 and IPv4
            if (this.endPoint.Address == IPAddress.IPv6Any)
            {
                newListenSocket.DualMode = true;
            }

            try
            {
                newListenSocket.Bind(this.endPoint);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                throw new AddressInUseException(e.Message, e);
            }

            // If requested port was "0", replace with assigned dynamic port.
            if (this.endPoint.Port == 0)
            {
                this.endPoint = (IPEndPoint)newListenSocket.LocalEndPoint;
            }

            newListenSocket.Listen(512);

            this.listenSocket = newListenSocket;

            this.listenTask = Task.Run(() => this.RunAcceptLoopAsync());

            return Task.CompletedTask;
        }

        public async Task Unbind()
        {
            if (this.listenSocket != null)
            {
                this.unbinding = true;
                this.listenSocket.Dispose();

                Debug.Assert(this.listenTask != null);
                await this.listenTask.ConfigureAwait(false);

                this.unbinding = false;
                this.listenSocket = null;
                this.listenTask = null;

                if (this.listenException != null)
                {
                    var exInfo = ExceptionDispatchInfo.Capture(this.listenException);
                    this.listenException = null;
                    exInfo.Throw();
                }
            }
        }

        public Task Stop()
        {
            return Task.CompletedTask;
        }

        private async Task RunAcceptLoopAsync()
        {
            try
            {
                while (!this.unbinding)
                {
                    try
                    {
                        var acceptSocket = await this.listenSocket.AcceptAsync();

                        var scheduler = PipeScheduler.ThreadPool;
                        var connection = new SocketConnection(acceptSocket, this.memoryPool, scheduler, this.trace);
                        var pair = DuplexPipe.CreateConnectionPair(
                            GetPipeOptions(
                                PipeScheduler.ThreadPool,
                                connection.InputWriterScheduler,
                                this.memoryPool),
                            GetPipeOptions(
                                connection.OutputReaderScheduler,
                                PipeScheduler.ThreadPool,
                                this.memoryPool));

                        connection.Application = pair.Application;
                        connection.Transport = pair.Transport;

                        // REVIEW: This task should be tracked by the server for graceful shutdown
                        // Today it's handled specifically for http but not for arbitrary middleware
                        _ = this.HandleConnectionAsync(connection);
                    }
                    catch (SocketException) when (!this.unbinding)
                    {
                        this.trace.ConnectionReset(connectionId: "(null)");
                    }
                }
            }
            catch (Exception ex)
            {
                if (this.unbinding)
                {
                    // Means we must be unbinding. Eat the exception.
                }
                else
                {
                    this.trace.LogCritical(ex, $"Unexpected exception in {nameof(SocketConnectionListener)}.{nameof(RunAcceptLoopAsync)}.");
                    this.listenException = ex;

                    // Request shutdown so we can rethrow this exception
                    // in Stop which should be observable.
                    this.applicationLifetime.StopApplication();
                }
            }
        }

        private async Task HandleConnectionAsync(SocketConnection connection)
        {
            try
            {
                var middlewareTask = this.connectionDelegate(connection);
                var transportTask = connection.StartAsync();

                connection.ConnectionClosed.Register(
                    () => connection.Dispose(),
                    useSynchronizationContext: false);

                await transportTask;
                await middlewareTask;
            }
            catch (Exception ex)
            {
                this.trace.LogCritical(ex, $"Unexpected exception in {nameof(SocketConnectionListener)}.{nameof(HandleConnectionAsync)}.");
            }
        }

        private static PipeOptions GetPipeOptions(PipeScheduler readerScheduler, PipeScheduler writerScheduler, MemoryPool<byte> memoryPool) =>
            new PipeOptions(
                pool: memoryPool,
                readerScheduler: readerScheduler,
                writerScheduler: writerScheduler,
                pauseWriterThreshold: 0,
                resumeWriterThreshold: 0,
                useSynchronizationContext: false,
                minimumSegmentSize: 4096);
    }
}
