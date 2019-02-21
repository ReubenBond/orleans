using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging.RePlat
{
    internal sealed class SocketConnectionListener : IConnectionListener
    {
        private readonly MemoryPool<byte> memoryPool;
        private readonly ConnectionDelegate connectionDelegate;
    //    private readonly IApplicationLifetime applicationLifetime;
        private readonly ISocketsTrace trace;
        private readonly PipeScheduler scheduler = PipeScheduler.ThreadPool;
        private Socket listenSocket;
        private Task listenTask;
        private Exception listenException;
        private IPEndPoint endPoint;
        private volatile bool unbinding;

        internal SocketConnectionListener(
            string endPoint,
            ConnectionDelegate connectionDelegate,
         //   IApplicationLifetime applicationLifetime,
            ISocketsTrace trace)
        {
            Debug.Assert(endPoint != null);
            Debug.Assert(connectionDelegate != null);
      //      Debug.Assert(applicationLifetime != null);
            Debug.Assert(trace != null);

            if (!IPEndPointUtility.TryParseEndPoint(endPoint, out this.endPoint))
            {
                throw new ArgumentException($"Unable to parse {endPoint} as {(nameof(IPEndPoint))}.");
            }

            this.connectionDelegate = connectionDelegate;
         //   this.applicationLifetime = applicationLifetime;
            this.trace = trace;
            this.memoryPool = MemoryPool<byte>.Shared;
        }

        public Task Bind()
        {
            if (this.listenSocket != null)
            {
                throw new InvalidOperationException($"Address {this.endPoint} already bound.");
            }
            
            var listenSocket = new Socket(this.endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            // Kestrel expects IPv6Any to bind to both IPv6 and IPv4
            if (this.endPoint.Address == IPAddress.IPv6Any)
            {
                listenSocket.DualMode = true;
            }

            try
            {
                listenSocket.Bind(this.endPoint);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                throw new AddressInUseException(e.Message, e);
            }

            // If requested port was "0", replace with assigned dynamic port.
            if (this.endPoint.Port == 0)
            {
                this.endPoint = (IPEndPoint)listenSocket.LocalEndPoint;
            }

            listenSocket.Listen(512);

            this.listenSocket = listenSocket;

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
            this.memoryPool.Dispose();
            return Task.CompletedTask;
        }

        private async Task RunAcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                        try
                        {
                            var acceptSocket = await this.listenSocket.AcceptAsync();
                            acceptSocket.NoDelay = true;

                            var connection = new SocketConnection(acceptSocket, this.memoryPool, this.scheduler, this.trace);

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
                   // this.applicationLifetime.StopApplication();
                }
            }
        }

        private async Task HandleConnectionAsync(SocketConnection connection)
        {
            try
            {
                var pair = DuplexPipe.CreateConnectionPair(GetPipeOptions(PipeScheduler.Inline, connection.InputWriterScheduler), GetPipeOptions(connection.OutputReaderScheduler, PipeScheduler.Inline));
                connection.Application = pair.Application;
                connection.Transport = pair.Transport;

                var middlewareTask = connectionDelegate(connection);
                var transportTask = connection.StartAsync();

                await transportTask;
                await middlewareTask;

                connection.Dispose();
            }
            catch (Exception ex)
            {
                this.trace.LogCritical(ex, $"Unexpected exception in {nameof(SocketConnectionListener)}.{nameof(HandleConnectionAsync)}.");
            }

            PipeOptions GetPipeOptions(PipeScheduler readerScheduler, PipeScheduler writerScheduler) => new PipeOptions(
                pool: this.memoryPool,
                readerScheduler: readerScheduler,
                writerScheduler: writerScheduler,
                pauseWriterThreshold: 0,
                resumeWriterThreshold: 0,
                useSynchronizationContext: false,
                minimumSegmentSize: 4000);
        }
    }
}
