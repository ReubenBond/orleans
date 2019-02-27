using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SocketConnection : TransportConnection, IDisposable
    {
        private static readonly int MinAllocBufferSize = KestrelMemoryPool.MinimumSegmentSize / 2;
        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        private readonly Socket socket;
        private readonly PipeScheduler scheduler;
        private readonly ISocketsTrace trace;
        private readonly SocketReceiver receiver;
        private readonly SocketSender sender;
        private readonly CancellationTokenSource connectionClosedTokenSource = new CancellationTokenSource();

        private readonly object shutdownLock = new object();
        private volatile bool socketDisposed;
        private volatile Exception shutdownReason;

        internal SocketConnection(Socket socket, MemoryPool<byte> memoryPool, PipeScheduler scheduler, ISocketsTrace trace)
        {
            Debug.Assert(socket != null);
            Debug.Assert(memoryPool != null);
            Debug.Assert(trace != null);

            this.socket = socket;
            this.MemoryPool = memoryPool;
            this.scheduler = scheduler;
            this.trace = trace;

            var localEndPoint = (IPEndPoint)this.socket.LocalEndPoint;
            var remoteEndPoint = (IPEndPoint)this.socket.RemoteEndPoint;

            this.LocalAddress = localEndPoint.Address;
            this.LocalPort = localEndPoint.Port;

            this.RemoteAddress = remoteEndPoint.Address;
            this.RemotePort = remoteEndPoint.Port;

            this.ConnectionClosed = this.connectionClosedTokenSource.Token;

            // On *nix platforms, Sockets already dispatches to the ThreadPool.
            // Yes, the IOQueues are still used for the PipeSchedulers. This is intentional.
            // https://github.com/aspnet/KestrelHttpServer/issues/2573
            var awaiterScheduler = IsWindows ? this.scheduler : PipeScheduler.Inline;

            this.receiver = new SocketReceiver(this.socket, awaiterScheduler);
            this.sender = new SocketSender(this.socket, awaiterScheduler);
        }

        public override MemoryPool<byte> MemoryPool { get; }
        public override PipeScheduler InputWriterScheduler => this.scheduler;
        public override PipeScheduler OutputReaderScheduler => this.scheduler;

        public async Task StartAsync()
        {
            try
            {
                // Spawn send and receive logic
                var receiveTask = this.DoReceive();
                var sendTask = this.DoSend();

                // Now wait for both to complete
                await receiveTask;
                await sendTask;

                this.receiver.Dispose();
                this.sender.Dispose();
                ThreadPool.UnsafeQueueUserWorkItem(state => ((SocketConnection)state).CancelConnectionClosedToken(), this);
            }
            catch (Exception ex)
            {
                this.trace.LogError(0, ex, $"Unexpected exception in {nameof(SocketConnection)}.{nameof(StartAsync)}.");
            }
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            // Try to gracefully close the socket to match libuv behavior.
            this.Shutdown(abortReason);

            // Cancel ProcessSends loop after calling shutdown to ensure the correct _shutdownReason gets set.
            this.Output.CancelPendingRead();
        }

        // Only called after connection middleware is complete which means the ConnectionClosed token has fired.
        public void Dispose()
        {
            this.connectionClosedTokenSource.Dispose();
            this._connectionClosingCts.Dispose();
        }

        private async Task DoReceive()
        {
            Exception error = null;

            try
            {
                await this.ProcessReceives();
            }
            catch (SocketException ex) when (IsConnectionResetError(ex.SocketErrorCode))
            {
                // This could be ignored if _shutdownReason is already set.
                error = new ConnectionResetException(ex.Message, ex);

                // There's still a small chance that both DoReceive() and DoSend() can log the same connection reset.
                // Both logs will have the same ConnectionId. I don't think it's worthwhile to lock just to avoid this.
                if (!this.socketDisposed)
                {
                    this.trace.ConnectionReset(this.ConnectionId);
                }
            }
            catch (Exception ex)
                when ((ex is SocketException socketEx && IsConnectionAbortError(socketEx.SocketErrorCode)) ||
                       ex is ObjectDisposedException)
            {
                // This exception should always be ignored because _shutdownReason should be set.
                error = ex;

                if (!this.socketDisposed)
                {
                    // This is unexpected if the socket hasn't been disposed yet.
                    this.trace.ConnectionError(this.ConnectionId, error);
                }
            }
            catch (Exception ex)
            {
                // This is unexpected.
                error = ex;
                this.trace.ConnectionError(this.ConnectionId, error);
            }
            finally
            {
                // If Shutdown() has already bee called, assume that was the reason ProcessReceives() exited.
                this.Input.Complete(this.shutdownReason ?? error);
            }
        }

        private async Task ProcessReceives()
        {
            // Resolve `input` PipeWriter via the IDuplexPipe interface prior to loop start for performance.
            var input = this.Input;
            while (true)
            {
                // Wait for data before allocating a buffer.
                await this.receiver.WaitForDataAsync();

                // Ensure we have some reasonable amount of buffer space
                var buffer = input.GetMemory(MinAllocBufferSize);

                var bytesReceived = await this.receiver.ReceiveAsync(buffer);

                if (bytesReceived == 0)
                {
                    // FIN
                    this.trace.ConnectionReadFin(this.ConnectionId);
                    break;
                }

                input.Advance(bytesReceived);

                var flushTask = input.FlushAsync();

                var paused = !flushTask.IsCompleted;

                if (paused)
                {
                    this.trace.ConnectionPause(this.ConnectionId);
                }

                var result = await flushTask;

                if (paused)
                {
                    this.trace.ConnectionResume(this.ConnectionId);
                }

                if (result.IsCompleted || result.IsCanceled)
                {
                    // Pipe consumer is shut down, do we stop writing
                    break;
                }
            }
        }

        private async Task DoSend()
        {
            Exception shutdownReason = null;
            Exception unexpectedError = null;

            try
            {
                await this.ProcessSends();
            }
            catch (SocketException ex) when (IsConnectionResetError(ex.SocketErrorCode))
            {
                shutdownReason = new ConnectionResetException(ex.Message, ex); ;
                this.trace.ConnectionReset(this.ConnectionId);
            }
            catch (Exception ex)
                when ((ex is SocketException socketEx && IsConnectionAbortError(socketEx.SocketErrorCode)) ||
                       ex is ObjectDisposedException)
            {
                // This should always be ignored since Shutdown() must have already been called by Abort().
                shutdownReason = ex;
            }
            catch (Exception ex)
            {
                shutdownReason = ex;
                unexpectedError = ex;
                this.trace.ConnectionError(this.ConnectionId, unexpectedError);
            }
            finally
            {
                this.Shutdown(shutdownReason);

                // Complete the output after disposing the socket
                this.Output.Complete(unexpectedError);

                // Cancel any pending flushes so that the input loop is un-paused
                this.Input.CancelPendingFlush();
            }
        }

        private async Task ProcessSends()
        {
            // Resolve `output` PipeReader via the IDuplexPipe interface prior to loop start for performance.
            var output = this.Output;
            while (true)
            {
                var result = await output.ReadAsync();

                if (result.IsCanceled)
                {
                    break;
                }

                var buffer = result.Buffer;

                var end = buffer.End;
                var isCompleted = result.IsCompleted;
                if (!buffer.IsEmpty)
                {
                    await this.sender.SendAsync(buffer);
                }

                output.AdvanceTo(end);

                if (isCompleted)
                {
                    break;
                }
            }
        }

        private void Shutdown(Exception shutdownReason)
        {
            lock (this.shutdownLock)
            {
                if (this.socketDisposed)
                {
                    return;
                }

                // Make sure to close the connection only after the _aborted flag is set.
                // Without this, the RequestsCanBeAbortedMidRead test will sometimes fail when
                // a BadHttpRequestException is thrown instead of a TaskCanceledException.
                this.socketDisposed = true;

                // shutdownReason should only be null if the output was completed gracefully, so no one should ever
                // ever observe the nondescript ConnectionAbortedException except for connection middleware attempting
                // to half close the connection which is currently unsupported.
                this.shutdownReason = shutdownReason ?? new ConnectionAbortedException("The Socket transport's send loop completed gracefully.");

                this.trace.ConnectionWriteFin(this.ConnectionId, this.shutdownReason.Message);

                try
                {
                    // Try to gracefully close the socket even for aborts to match libuv behavior.
                    this.socket.Shutdown(SocketShutdown.Both);
                }
                catch
                {
                    // Ignore any errors from Socket.Shutdown() since we're tearing down the connection anyway.
                }

                this.socket.Dispose();
            }
        }

        private void CancelConnectionClosedToken()
        {
            try
            {
                this.connectionClosedTokenSource.Cancel();
            }
            catch (Exception ex)
            {
                this.trace.LogError(0, ex, $"Unexpected exception in {nameof(SocketConnection)}.{nameof(CancelConnectionClosedToken)}.");
            }
        }

        private static bool IsConnectionResetError(SocketError errorCode)
        {
            // A connection reset can be reported as SocketError.ConnectionAborted on Windows.
            // ProtocolType can be removed once https://github.com/dotnet/corefx/issues/31927 is fixed.
            return errorCode == SocketError.ConnectionReset ||
                   errorCode == SocketError.Shutdown ||
                   (errorCode == SocketError.ConnectionAborted && IsWindows) ||
                   (errorCode == SocketError.ProtocolType && IsMacOS);
        }

        private static bool IsConnectionAbortError(SocketError errorCode)
        {
            // Calling Dispose after ReceiveAsync can cause an "InvalidArgument" error on *nix.
            return errorCode == SocketError.OperationAborted ||
                   errorCode == SocketError.Interrupted ||
                   (errorCode == SocketError.InvalidArgument && !IsWindows);
        }
    }
}
