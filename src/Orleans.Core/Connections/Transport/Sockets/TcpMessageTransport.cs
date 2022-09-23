#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Transport.Utilities;
using Orleans.Connections.Sockets;

namespace Orleans.Connections.Transport.Sockets;

public sealed class TcpMessageTransport : MessageTransportBase
{
    private const int MinReadSize = 256;
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private readonly SocketSender _socketSender = new();
    private readonly SocketReceiver _socketReceiver = new();
    private readonly Socket _socket;
    private readonly Queue<WriteRequest> _writeRequests = new();
    private readonly Queue<ReadRequest> _readRequests = new();
    private readonly SingleWaiterInlineSignal _readSignal = new();
    private readonly SingleWaiterInlineSignal _writeSignal = new();
    private readonly Action _fireReadSignal;
    private readonly Action _fireWriteSignal;
    private readonly ILogger _logger;
    private readonly string _connectionId;
    private readonly CancellationTokenSource _connectionClosingCts = new();
    private readonly CancellationTokenSource _connectionClosedCts = new();
    private readonly object _shutdownLock = new();
    private readonly object _writesLock = new();
    private readonly object _readsLock = new();
    private Task? _processingTask;
    private volatile bool _socketDisposed;
    private volatile Exception? _shutdownReason;

    public TcpMessageTransport(Socket socket, ILogger logger)
    {
        _socket = socket;
        _logger = logger;
        _fireReadSignal = _readSignal.Signal;
        _fireWriteSignal = _writeSignal.Signal;
        _connectionId = CorrelationIdGenerator.GetNextId();
    }

    public override CancellationToken Closed => _connectionClosedCts.Token;

    public void Start()
    {
        using var _ = new ExecutionContextSuppressor();
        _processingTask = StartAsync();
    }

    private async Task StartAsync()
    {
        // Return immediately to the synchronous caller.
        await Task.Yield();

        try
        {
            // Spawn send and receive logic
            var receiveTask = ProcessReads();
            var sendTask = ProcessWrites();

            // Now wait for both to complete
            await receiveTask;
            await sendTask;

            _socketReceiver.Dispose();
            _socketSender.Dispose();
        }
        catch (Exception ex)
        {
            _shutdownReason ??= ex;
            _logger.LogError(0, ex, $"Unexpected exception in {nameof(TcpMessageTransport)}.{nameof(StartAsync)}.");
        }
        finally
        {
            if (!_socketDisposed)
            {
                Shutdown();
            }

            _connectionClosedCts.Cancel();
        }
    }

    private void Shutdown()
    {
        lock (_shutdownLock)
        {
            try
            {
                if (_socketDisposed)
                {
                    return;
                }

                // Make sure to close the connection only after the _aborted flag is set.
                // Without this, the RequestsCanBeAbortedMidRead test will sometimes fail when
                // a BadHttpRequestException is thrown instead of a TaskCanceledException.
                _socketDisposed = true;

                // shutdownReason should only be null if the output was completed gracefully, so no one should ever
                // ever observe the nondescript ConnectionAbortedException except for connection middleware attempting
                // to half close the connection which is currently unsupported.
                _shutdownReason ??= new ConnectionAbortedException("The Socket transport's send loop completed gracefully.");
                SocketsLog.ConnectionWriteFin(_logger, this, _shutdownReason.Message);

                try
                {
                    // Try to gracefully close the socket even for aborts to match libuv behavior.
                    _socket.Shutdown(SocketShutdown.Both);
                }
                catch
                {
                    // Ignore any errors from Socket.Shutdown() since we're tearing down the connection anyway.
                }

                _socket.Dispose();
            }
            catch (Exception exception)
            {
                SocketsLog.ConnectionShutdownError(_logger, this, exception);
            }
        }
    }

    public override bool ReadAsync(ReadRequest request)
    {
        if (_connectionClosingCts.IsCancellationRequested)
        {
            return false;
        }

        lock (_readsLock)
        {
            _readRequests.Enqueue(request);
        }

        _readSignal.Signal();
        return true;
    }

    public override bool WriteAsync(WriteRequest request)
    {
        if (_connectionClosingCts.IsCancellationRequested)
        {
            return false;
        }

        lock (_writesLock)
        {
            _writeRequests.Enqueue(request);
        }

        _writeSignal.Signal();
        return true;
    }

    public override async ValueTask CloseAsync(Exception? closeReason)
    {
        if (_connectionClosedCts.IsCancellationRequested)
        {
            return;
        }

        _shutdownReason ??= closeReason;
        Shutdown();

        _connectionClosingCts.Cancel();

        if (_processingTask is null)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectionClosedCts.Token.Register(OnClosed, completion, useSynchronizationContext: false);
        await completion.Task;

        static void OnClosed(object? state)
        {
            if (state is not TaskCompletionSource completion) throw new ArgumentException(nameof(state));
            completion.TrySetResult();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync(null);
    }

    private async Task ProcessReads()
    {
        await Task.Yield();
        Exception? error = null;
        ReadRequest? request = null;
        try
        {
            // Loop until termination.
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                // Handle each request.
                while (TryDequeue(out request))
                {
                    // Process the request to completion.
                    while (true)
                    {
                        await _socketReceiver.ReceiveAsync(_socket, request.Buffer);

                        if (_socketReceiver.HasError)
                        {
                            if (IsConnectionResetError(_socketReceiver.SocketError))
                            {
                                // This could be ignored if _shutdownReason is already set.
                                var ex = _socketReceiver.Error;
                                error = new ConnectionResetException(ex.Message, ex);

                                // There's still a small chance that both DoReceive() and DoSend() can log the same connection reset.
                                // Both logs will have the same ConnectionId. I don't think it's worthwhile to lock just to avoid this.
                                if (!_socketDisposed)
                                {
                                    SocketsLog.ConnectionReset(_logger, this);
                                }
                            }
                            else if (IsConnectionAbortError(_socketReceiver.SocketError))
                            {
                                // This exception should always be ignored because _shutdownReason should be set.
                                error = _socketReceiver.Error;

                                if (!_socketDisposed)
                                {
                                    // This is unexpected if the socket hasn't been disposed yet.
                                    SocketsLog.ConnectionError(_logger, this, error);
                                }
                            }
                            else
                            {
                                // This is unexpected.
                                error = _socketReceiver.Error;
                                SocketsLog.ConnectionError(_logger, this, error);
                            }

                            break;
                        }

                        var transfered = _socketReceiver.BytesTransferred;
                        if (transfered == 0)
                        {
                            // FIN
                            SocketsLog.ConnectionReadFin(_logger, this);
                            error = new ConnectionAbortedException("Connection terminated normally");
                            break;
                        }

                        if (request.OnProgress(transfered))
                        {
                            break;
                        }
                    }

                    if (error is not null)
                    {
                        // Bubble the error up
                        break;
                    }
                }

                if (error is not null)
                {
                    // Bubble the error up
                    break;
                }

                await _readSignal.WaitAsync();
            }
        }
        catch (ObjectDisposedException ex)
        {
            // This exception should always be ignored because _shutdownReason should be set.
            error = ex;

            if (!_socketDisposed)
            {
                // This is unexpected if the socket hasn't been disposed yet.
                SocketsLog.ConnectionError(_logger, this, error);
            }
        }
        catch (Exception ex)
        {
            // This is unexpected.
            error = ex;
            SocketsLog.ConnectionError(_logger, this, error);
        }
        finally
        {
            if (error is { }) request?.OnError(error);

            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();
        }

        bool TryDequeue([NotNullWhen(true)] out ReadRequest? request)
        {
            lock (_readsLock)
            {
                return _readRequests.TryDequeue(out request);
            }
        }
    }

    private async Task ProcessWrites()
    {
        await Task.Yield();
        Exception? error = null;
        WriteRequest? request = null;
        try
        {
            // Loop until termination.
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                // Handle each request.
                while (TryDequeue(out request))
                {
                    if (request.IsSingleBuffer)
                    {
                        await _socketSender.SendAsync(_socket, request.Buffer);
                    }
                    else
                    {
                        await _socketSender.SendAsync(_socket, request.Buffers);
                    }

                    if (_socketSender.HasError)
                    {
                        if (IsConnectionResetError(_socketSender.SocketError))
                        {
                            // This could be ignored if _shutdownReason is alwritey set.
                            var ex = _socketSender.Error;
                            error = new ConnectionResetException(ex.Message, ex);

                            // There's still a small chance that both DoReceive() and DoSend() can log the same connection reset.
                            // Both logs will have the same ConnectionId. I don't think it's worthwhile to lock just to avoid this.
                            if (!_socketDisposed)
                            {
                                SocketsLog.ConnectionReset(_logger, this);
                            }
                        }
                        else if (IsConnectionAbortError(_socketSender.SocketError))
                        {
                            // This exception should always be ignored because _shutdownReason should be set.
                            error = _socketSender.Error;

                            if (!_socketDisposed)
                            {
                                // This is unexpected if the socket hasn't been disposed yet.
                                SocketsLog.ConnectionError(_logger, this, error);
                            }
                        }
                        else
                        {
                            // This is unexpected.
                            error = _socketSender.Error;
                            SocketsLog.ConnectionError(_logger, this, error);
                        }

                        break;
                    }

                    if (error is not null)
                    {
                        // Bubble the error up
                        break;
                    }

                    // Signal that the request is completed
                    request.OnCompleted();
                }

                if (error is not null)
                {
                    // Bubble the error up
                    break;
                }

                await _writeSignal.WaitAsync();
            }
        }
        catch (ObjectDisposedException ex)
        {
            // This exception should always be ignored because _shutdownReason should be set.
            error = ex;

            if (!_socketDisposed)
            {
                // This is unexpected if the socket hasn't been disposed yet.
                SocketsLog.ConnectionError(_logger, this, error);
            }
        }
        catch (Exception ex)
        {
            // This is unexpected.
            error = ex;
            SocketsLog.ConnectionError(_logger, this, error);
        }
        finally
        {
            if (error is { })
            {
                request?.OnError(error);
            }

            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();
        }

        bool TryDequeue([NotNullWhen(true)] out WriteRequest? request)
        {
            lock (_writesLock)
            {
                return _writeRequests.TryDequeue(out request);
            }
        }
    }

    private static bool IsConnectionResetError(SocketError errorCode)
    {
        // A connection reset can be reported as SocketError.ConnectionAborted on Windows.
        // ProtocolType can be removed once https://github.com/dotnet/corefx/issues/31927 is fixed.
        return errorCode == SocketError.ConnectionReset ||
               errorCode == SocketError.Shutdown ||
               errorCode == SocketError.ConnectionAborted && IsWindows ||
               errorCode == SocketError.ProtocolType && IsMacOS;
    }

    private static bool IsConnectionAbortError(SocketError errorCode)
    {
        // Calling Dispose after ReceiveAsync can cause an "InvalidArgument" error on *nix.
        return errorCode == SocketError.OperationAborted ||
               errorCode == SocketError.Interrupted ||
               errorCode == SocketError.InvalidArgument && !IsWindows;
    }

    public override string ToString() => $"[{nameof(TcpMessageTransport)} Id: {_connectionId}, Remote: {_socket.RemoteEndPoint}, Local: {_socket.LocalEndPoint}]";
}
