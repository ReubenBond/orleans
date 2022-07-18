#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Internal;

namespace Orleans.Networking.Transport;

internal class TlsNetworkTransport : NetworkTransport
{
    private const int MinReadSize = 256;
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private readonly NetworkTransport _transport;
    private readonly Queue<WriteRequest> _writeRequests = new();
    private readonly SingleWaiterInlineSignal _readSignal = new();
    private readonly SingleWaiterInlineSignal _writeSignal = new();
    private readonly Action _fireReadSignal;
    private readonly Action _fireWriteSignal;
    private readonly ILogger _logger;
    private readonly string _connectionId;
    private readonly CancellationTokenSource _connectionClosingCts = new();
    private readonly CancellationTokenSource _connectionClosedCts = new();
    private readonly object _shutdownLock = new object();
    private readonly MemoryStream _innerStream;
    private readonly SslStream _stream;
    private ReadRequest? _readRequest;
    private Task? _processingTask;
    private volatile bool _disposed;
    private volatile Exception? _shutdownReason;

    public TlsNetworkTransport(NetworkTransport innerTransport, ILogger logger)
    {
        _innerStream = new MemoryStream();
        _stream = new SslStream(_innerStream);
        _transport = innerTransport;
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
        }
        catch (Exception ex)
        {
            _shutdownReason ??= ex;
            _logger.LogError(0, ex, $"Unexpected exception in {nameof(TlsNetworkTransport)}.{nameof(StartAsync)}.");
        }
        finally
        {
            if (!_disposed)
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
                if (_disposed)
                {
                    return;
                }

                // Make sure to close the connection only after the _aborted flag is set.
                // Without this, the RequestsCanBeAbortedMidRead test will sometimes fail when
                // a BadHttpRequestException is thrown instead of a TaskCanceledException.
                _disposed = true;

                // shutdownReason should only be null if the output was completed gracefully, so no one should ever
                // ever observe the nondescript ConnectionAbortedException except for connection middleware attempting
                // to half close the connection which is currently unsupported.
                _shutdownReason ??= new ConnectionAbortedException("The transport's send loop completed gracefully.");
                //SocketsLog.ConnectionWriteFin(_logger, this, _shutdownReason.Message);

            }
            catch (Exception exception)
            {
                //SocketsLog.ConnectionShutdownError(_logger, this, exception);
                _ = exception;
            }
        }
    }

    public override bool ReadAsync(ReadRequest request)
    {
        if (_connectionClosingCts.IsCancellationRequested)
        {
            return false;
        }

        _readRequest = request;
        _readSignal.Signal();
        return true;
    }

    public override bool WriteAsync(WriteRequest request)
    {
        if (_connectionClosingCts.IsCancellationRequested)
        {
            return false;
        }

        _writeRequests.Enqueue(request);
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
        try
        {
            // Loop until termination.
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                // Handle each request.
                while (_readRequest is { } request)
                {
                    // Process the request to completion.
                    while (true)
                    {
                        var resultTask = _socketReceiver.ReceiveAsync(_socket, request.Buffer);

                        // Wait until the result completes or the operation is canceled
                        if (!_socketReceiver.IsCompleted)
                        {
                            resultTask.GetAwaiter().UnsafeOnCompleted(_fireReadSignal);
                            await _readSignal.WaitAsync();
                            _connectionClosingCts.Token.ThrowIfCancellationRequested();
                        }

                        if (_socketReceiver.HasError)
                        {
                            if (IsConnectionResetError(_socketReceiver.SocketError))
                            {
                                // This could be ignored if _shutdownReason is already set.
                                var ex = _socketReceiver.Error;
                                error = new ConnectionResetException(ex.Message, ex);

                                // There's still a small chance that both DoReceive() and DoSend() can log the same connection reset.
                                // Both logs will have the same ConnectionId. I don't think it's worthwhile to lock just to avoid this.
                                if (!_disposed)
                                {
                                    SocketsLog.ConnectionReset(_logger, this);
                                }
                            }
                            else if (IsConnectionAbortError(_socketReceiver.SocketError))
                            {
                                // This exception should always be ignored because _shutdownReason should be set.
                                error = _socketReceiver.Error;

                                if (!_disposed)
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

            if (!_disposed)
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
            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();
        }
    }

    private async Task ProcessWrites()
    {
        await Task.Yield();
        Exception? error = null;
        try
        {
            // Loop until termination.
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                // Handle each request.
                while (_writeRequests.TryDequeue(out var request))
                {
                    var resultTask = _socketSender.SendAsync(_socket, request.Buffers);

                    // Wait until the result completes or the operation is canceled
                    if (!_socketSender.IsCompleted)
                    {
                        resultTask.GetAwaiter().UnsafeOnCompleted(_fireWriteSignal);
                        await _writeSignal.WaitAsync();
                        _connectionClosingCts.Token.ThrowIfCancellationRequested();
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
                            if (!_disposed)
                            {
                                SocketsLog.ConnectionReset(_logger, this);
                            }
                        }
                        else if (IsConnectionAbortError(_socketSender.SocketError))
                        {
                            // This exception should always be ignored because _shutdownReason should be set.
                            error = _socketSender.Error;

                            if (!_disposed)
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

            if (!_disposed)
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
            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();
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

    public override string ToString() => $"[{nameof(TlsNetworkTransport)} Id: {_connectionId}, Remote: {_socket.RemoteEndPoint}, Local: {_socket.LocalEndPoint}]";

}
