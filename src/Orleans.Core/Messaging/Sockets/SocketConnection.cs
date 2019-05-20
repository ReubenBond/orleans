using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    public partial class TransportConnection : IFeatureCollection
    {
        private static readonly Type IHttpConnectionFeatureType = typeof(IHttpConnectionFeature);
        private static readonly Type IConnectionIdFeatureType = typeof(IConnectionIdFeature);
        private static readonly Type IConnectionTransportFeatureType = typeof(IConnectionTransportFeature);
        private static readonly Type IConnectionItemsFeatureType = typeof(IConnectionItemsFeature);
        private static readonly Type IMemoryPoolFeatureType = typeof(IMemoryPoolFeature);
        private static readonly Type IConnectionLifetimeFeatureType = typeof(IConnectionLifetimeFeature);
        private static readonly Type IConnectionHeartbeatFeatureType = typeof(IConnectionHeartbeatFeature);
        private static readonly Type IConnectionLifetimeNotificationFeatureType = typeof(IConnectionLifetimeNotificationFeature);

        private object _currentIHttpConnectionFeature;
        private object _currentIConnectionIdFeature;
        private object _currentIConnectionTransportFeature;
        private object _currentIConnectionItemsFeature;
        private object _currentIMemoryPoolFeature;
        private object _currentIApplicationTransportFeature;
        private object _currentITransportSchedulerFeature;
        private object _currentIConnectionLifetimeFeature;
        private object _currentIConnectionHeartbeatFeature;
        private object _currentIConnectionLifetimeNotificationFeature;

        private int _featureRevision;

        private List<KeyValuePair<Type, object>> MaybeExtra;

        private void FastReset()
        {
            _currentIHttpConnectionFeature = this;
            _currentIConnectionIdFeature = this;
            _currentIConnectionTransportFeature = this;
            _currentIConnectionItemsFeature = this;
            _currentIMemoryPoolFeature = this;
            _currentIApplicationTransportFeature = this;
            _currentITransportSchedulerFeature = this;
            _currentIConnectionLifetimeFeature = this;
            _currentIConnectionHeartbeatFeature = this;
            _currentIConnectionLifetimeNotificationFeature = this;

        }

        // Internal for testing
        internal void ResetFeatureCollection()
        {
            FastReset();
            MaybeExtra?.Clear();
            _featureRevision++;
        }

        private object ExtraFeatureGet(Type key)
        {
            if (MaybeExtra == null)
            {
                return null;
            }
            for (var i = 0; i < MaybeExtra.Count; i++)
            {
                var kv = MaybeExtra[i];
                if (kv.Key == key)
                {
                    return kv.Value;
                }
            }
            return null;
        }

        private void ExtraFeatureSet(Type key, object value)
        {
            if (MaybeExtra == null)
            {
                MaybeExtra = new List<KeyValuePair<Type, object>>(2);
            }

            for (var i = 0; i < MaybeExtra.Count; i++)
            {
                if (MaybeExtra[i].Key == key)
                {
                    MaybeExtra[i] = new KeyValuePair<Type, object>(key, value);
                    return;
                }
            }
            MaybeExtra.Add(new KeyValuePair<Type, object>(key, value));
        }

        bool IFeatureCollection.IsReadOnly => false;

        int IFeatureCollection.Revision => _featureRevision;

        object IFeatureCollection.this[Type key]
        {
            get
            {
                object feature = null;
                if (key == IHttpConnectionFeatureType)
                {
                    feature = _currentIHttpConnectionFeature;
                }
                else if (key == IConnectionIdFeatureType)
                {
                    feature = _currentIConnectionIdFeature;
                }
                else if (key == IConnectionTransportFeatureType)
                {
                    feature = _currentIConnectionTransportFeature;
                }
                else if (key == IConnectionItemsFeatureType)
                {
                    feature = _currentIConnectionItemsFeature;
                }
                else if (key == IMemoryPoolFeatureType)
                {
                    feature = _currentIMemoryPoolFeature;
                }
                else if (key == IConnectionLifetimeFeatureType)
                {
                    feature = _currentIConnectionLifetimeFeature;
                }
                else if (key == IConnectionHeartbeatFeatureType)
                {
                    feature = _currentIConnectionHeartbeatFeature;
                }
                else if (key == IConnectionLifetimeNotificationFeatureType)
                {
                    feature = _currentIConnectionLifetimeNotificationFeature;
                }
                else if (MaybeExtra != null)
                {
                    feature = ExtraFeatureGet(key);
                }

                return feature;
            }

            set
            {
                _featureRevision++;

                if (key == IHttpConnectionFeatureType)
                {
                    _currentIHttpConnectionFeature = value;
                }
                else if (key == IConnectionIdFeatureType)
                {
                    _currentIConnectionIdFeature = value;
                }
                else if (key == IConnectionTransportFeatureType)
                {
                    _currentIConnectionTransportFeature = value;
                }
                else if (key == IConnectionItemsFeatureType)
                {
                    _currentIConnectionItemsFeature = value;
                }
                else if (key == IMemoryPoolFeatureType)
                {
                    _currentIMemoryPoolFeature = value;
                }
                else if (key == IConnectionLifetimeFeatureType)
                {
                    _currentIConnectionLifetimeFeature = value;
                }
                else if (key == IConnectionHeartbeatFeatureType)
                {
                    _currentIConnectionHeartbeatFeature = value;
                }
                else if (key == IConnectionLifetimeNotificationFeatureType)
                {
                    _currentIConnectionLifetimeNotificationFeature = value;
                }
                else
                {
                    ExtraFeatureSet(key, value);
                }
            }
        }

        TFeature IFeatureCollection.Get<TFeature>()
        {
            TFeature feature = default;
            if (typeof(TFeature) == typeof(IHttpConnectionFeature))
            {
                feature = (TFeature)_currentIHttpConnectionFeature;
            }
            else if (typeof(TFeature) == typeof(IConnectionIdFeature))
            {
                feature = (TFeature)_currentIConnectionIdFeature;
            }
            else if (typeof(TFeature) == typeof(IConnectionTransportFeature))
            {
                feature = (TFeature)_currentIConnectionTransportFeature;
            }
            else if (typeof(TFeature) == typeof(IConnectionItemsFeature))
            {
                feature = (TFeature)_currentIConnectionItemsFeature;
            }
            else if (typeof(TFeature) == typeof(IMemoryPoolFeature))
            {
                feature = (TFeature)_currentIMemoryPoolFeature;
            }
            else if (typeof(TFeature) == typeof(IConnectionLifetimeFeature))
            {
                feature = (TFeature)_currentIConnectionLifetimeFeature;
            }
            else if (typeof(TFeature) == typeof(IConnectionHeartbeatFeature))
            {
                feature = (TFeature)_currentIConnectionHeartbeatFeature;
            }
            else if (typeof(TFeature) == typeof(IConnectionLifetimeNotificationFeature))
            {
                feature = (TFeature)_currentIConnectionLifetimeNotificationFeature;
            }
            else if (MaybeExtra != null)
            {
                feature = (TFeature)(ExtraFeatureGet(typeof(TFeature)));
            }

            return feature;
        }

        void IFeatureCollection.Set<TFeature>(TFeature feature)
        {
            _featureRevision++;
            if (typeof(TFeature) == typeof(IHttpConnectionFeature))
            {
                _currentIHttpConnectionFeature = feature;
            }
            else if (typeof(TFeature) == typeof(IConnectionIdFeature))
            {
                _currentIConnectionIdFeature = feature;
            }
            else if (typeof(TFeature) == typeof(IConnectionTransportFeature))
            {
                _currentIConnectionTransportFeature = feature;
            }
            else if (typeof(TFeature) == typeof(IConnectionItemsFeature))
            {
                _currentIConnectionItemsFeature = feature;
            }
            else if (typeof(TFeature) == typeof(IMemoryPoolFeature))
            {
                _currentIMemoryPoolFeature = feature;
            }
            else if (typeof(TFeature) == typeof(IConnectionLifetimeFeature))
            {
                _currentIConnectionLifetimeFeature = feature;
            }
            else if (typeof(TFeature) == typeof(IConnectionHeartbeatFeature))
            {
                _currentIConnectionHeartbeatFeature = feature;
            }
            else if (typeof(TFeature) == typeof(IConnectionLifetimeNotificationFeature))
            {
                _currentIConnectionLifetimeNotificationFeature = feature;
            }
            else
            {
                ExtraFeatureSet(typeof(TFeature), feature);
            }
        }

        private IEnumerable<KeyValuePair<Type, object>> FastEnumerable()
        {
            if (_currentIHttpConnectionFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IHttpConnectionFeatureType, _currentIHttpConnectionFeature);
            }
            if (_currentIConnectionIdFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IConnectionIdFeatureType, _currentIConnectionIdFeature);
            }
            if (_currentIConnectionTransportFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IConnectionTransportFeatureType, _currentIConnectionTransportFeature);
            }
            if (_currentIConnectionItemsFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IConnectionItemsFeatureType, _currentIConnectionItemsFeature);
            }
            if (_currentIMemoryPoolFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IMemoryPoolFeatureType, _currentIMemoryPoolFeature);
            }
            if (_currentIConnectionLifetimeFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IConnectionLifetimeFeatureType, _currentIConnectionLifetimeFeature);
            }
            if (_currentIConnectionHeartbeatFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IConnectionHeartbeatFeatureType, _currentIConnectionHeartbeatFeature);
            }
            if (_currentIConnectionLifetimeNotificationFeature != null)
            {
                yield return new KeyValuePair<Type, object>(IConnectionLifetimeNotificationFeatureType, _currentIConnectionLifetimeNotificationFeature);
            }

            if (MaybeExtra != null)
            {
                foreach (var item in MaybeExtra)
                {
                    yield return item;
                }
            }
        }

        IEnumerator<KeyValuePair<Type, object>> IEnumerable<KeyValuePair<Type, object>>.GetEnumerator() => FastEnumerable().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => FastEnumerable().GetEnumerator();
    }

    public abstract partial class TransportConnection : ConnectionContext
    {
        private IDictionary<object, object> _items;
        private List<(Action<object> handler, object state)> _heartbeatHandlers;
        private readonly object _heartbeatLock = new object();
        protected readonly CancellationTokenSource _connectionClosingCts = new CancellationTokenSource();

        public TransportConnection()
        {
            FastReset();

            ConnectionClosedRequested = _connectionClosingCts.Token;
        }

        public IPAddress RemoteAddress { get; set; }
        public int RemotePort { get; set; }
        public IPAddress LocalAddress { get; set; }
        public int LocalPort { get; set; }

        public override string ConnectionId { get; set; }

        public override IFeatureCollection Features => this;

        public virtual MemoryPool<byte> MemoryPool { get; }
        public virtual PipeScheduler InputWriterScheduler { get; }
        public virtual PipeScheduler OutputReaderScheduler { get; }

        public override IDuplexPipe Transport { get; set; }
        public IDuplexPipe Application { get; set; }

        public override IDictionary<object, object> Items
        {
            get
            {
                // Lazily allocate connection metadata
                return _items ?? (_items = new ConnectionItems());
            }
            set
            {
                _items = value;
            }
        }

        public PipeWriter Input => Application.Output;
        public PipeReader Output => Application.Input;

        public CancellationToken ConnectionClosed { get; set; }

        public CancellationToken ConnectionClosedRequested { get; set; }

        public void TickHeartbeat()
        {
            lock (_heartbeatLock)
            {
                if (_heartbeatHandlers == null)
                {
                    return;
                }

                foreach (var (handler, state) in _heartbeatHandlers)
                {
                    handler(state);
                }
            }
        }

        public void OnHeartbeat(Action<object> action, object state)
        {
            lock (_heartbeatLock)
            {
                if (_heartbeatHandlers == null)
                {
                    _heartbeatHandlers = new List<(Action<object> handler, object state)>();
                }

                _heartbeatHandlers.Add((action, state));
            }
        }

        // DO NOT remove this override to ConnectionContext.Abort. Doing so would cause
        // any TransportConnection that does not override Abort or calls base.Abort
        // to stack overflow when IConnectionLifetimeFeature.Abort() is called.
        // That said, all derived types should override this method should override
        // this implementation of Abort because canceling pending output reads is not
        // sufficient to abort the connection if there is backpressure.
        public override void Abort(ConnectionAbortedException abortReason)
        {
            Output.CancelPendingRead();
        }

        public void RequestClose()
        {
            try
            {
                _connectionClosingCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // There's a race where the token could be disposed
                // swallow the exception and no-op
            }
        }
    }

    internal sealed class SocketConnection : TransportConnection, IDisposable
    {
        private static readonly int MinAllocBufferSize = 2048;
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

                var result = await flushTask.ConfigureAwait(false);

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
