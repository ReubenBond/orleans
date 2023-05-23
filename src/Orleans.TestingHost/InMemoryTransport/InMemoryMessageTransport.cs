#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Utilities;
using Orleans.Runtime.Internal;

namespace Orleans.TestingHost.InMemoryTransport;

internal class InMemoryMessageTransport : MessageTransportBase
{
    private const int MinReadSize = 256;
    private Queue<WriteRequest> _writeRequests = new();
    private readonly Queue<ReadRequest> _readRequests = new();
    private readonly SingleWaiterInlineSignal _readSignal = new() { RunContinuationsAsynchronously = false };
    private readonly SingleWaiterInlineSignal _writeSignal = new() { RunContinuationsAsynchronously = true };
    private readonly Action _fireReadSignal;
    private readonly Action _fireWriteSignal;
    private readonly PipeReader _pipeReader;
    private readonly PipeWriter _pipeWriter;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _connectionClosingCts = new();
    private readonly CancellationTokenSource _connectionClosedCts = new();
    private readonly object _shutdownLock = new();
    private readonly object _writesLock = new();
    private readonly object _readsLock = new();
    private bool _readsCompleted;
    private bool _writesCompleted;
    private Task? _processingTask;
    private volatile Exception? _shutdownReason;

    public InMemoryMessageTransport(IDuplexPipe pipe, ILogger logger)
    {
        _pipeReader = pipe.Input;
        _pipeWriter = pipe.Output;
        _logger = logger;
        
        _fireReadSignal = _readSignal.Signal;
        _fireWriteSignal = _writeSignal.Signal;
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
            (var receiveTask, var sendTask) = StartProcessing();

            (Task ReceiveTask, Task SendTask) StartProcessing()
            {
                using (new ExecutionContextSuppressor())
                {
                    var receiveTask = ProcessReads();
                    var sendTask = ProcessWrites();
                    return (receiveTask, sendTask);
                }
            }

            // Wait for both to complete
            try
            {
                await receiveTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, $"Unexpected exception in {nameof(InMemoryMessageTransport)}.{nameof(ProcessReads)}.");
            }

            try
            {
                await sendTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, $"Unexpected exception in {nameof(InMemoryMessageTransport)}.{nameof(ProcessWrites)}.");
            }
        }
        catch (Exception ex)
        {
            _shutdownReason ??= ex;
            _logger.LogError(0, ex, $"Unexpected exception in {nameof(InMemoryMessageTransport)}.{nameof(StartAsync)}.");
        }
        finally
        {
            await CloseAsync();

            _connectionClosedCts.Cancel();
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
            if (_readsCompleted)
            {
                return false;
            }

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
            if (_writesCompleted)
            {
                return false;
            }

            _writeRequests.Enqueue(request);
        }

        _writeSignal.Signal();
        return true;
    }

    public override async ValueTask CloseAsync(Exception? closeReason = null)
    {
        if (_connectionClosedCts.IsCancellationRequested)
        {
            return;
        }

        _shutdownReason ??= closeReason;
        await _pipeReader.CompleteAsync(_shutdownReason);
        await _pipeWriter.CompleteAsync(_shutdownReason);

        _connectionClosingCts.Cancel();
        _readSignal.Signal();
        _writeSignal.Signal();

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
        bool isGracefulTermination = false;
        Exception? error = null;
        ReadRequest? request = null;
        ReadOnlySequence<byte> readBuffer = default;
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
                        var requestBuffer = request.Buffer;
                        Debug.Assert(requestBuffer.Length > 0);
                        if (readBuffer.Length == 0)
                        {
                            var readResult = await _pipeReader.ReadAsync(_connectionClosingCts.Token);

                            if (readResult.IsCanceled || readResult.IsCompleted)
                            {
                                isGracefulTermination = true;
                                break;
                            }

                            readBuffer = readResult.Buffer;
                        }

                        int transferred;
                        if (readBuffer.Length > requestBuffer.Length)
                        {
                            var sliced = readBuffer.Slice(0, requestBuffer.Length);
                            sliced.CopyTo(requestBuffer.Span);
                            transferred = (int)sliced.Length;
                            readBuffer = readBuffer.Slice(requestBuffer.Length);
                        }
                        else
                        {
                            readBuffer.CopyTo(requestBuffer.Span);
                            transferred = (int)readBuffer.Length;
                            readBuffer = readBuffer.Slice(readBuffer.Length);
                        }

                        _pipeReader.AdvanceTo(readBuffer.Start, readBuffer.End);
                        if (request.OnRead(transferred))
                        {
                            break;
                        }
                    }

                    if (isGracefulTermination)
                    {
                        break;
                    }
                }

                if (isGracefulTermination)
                {
                    break;
                }

                await _readSignal.WaitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            error = exception;
            isGracefulTermination = false;
        }
        finally
        {
            if (isGracefulTermination)
            {
                request?.OnCanceled();
            }
            else if (error is { })
            {
                request?.OnError(error);
            }

            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();
            _writeSignal.Signal();

            lock (_readsLock)
            {
                _readsCompleted = true;
            }

            while (TryDequeue(out request))
            {
                request.OnError(_shutdownReason!);
            }
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
        const int SoftBatchMax = 32;
        await Task.Yield();
        Exception? error = null;
        Queue<WriteRequest> requests = new();
        List<ArraySegment<byte>> buffers = new(capacity: SoftBatchMax);
        List<WriteRequest> processingRequests = new(capacity: SoftBatchMax);

        try
        {
            // Loop until termination.
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                if (requests.Count == 0)
                {
                    // Check for pending messages before waiting.
                    RefreshRequestQueue(ref requests);

                    if (requests.Count == 0)
                    {
                        await _writeSignal.WaitAsync().ConfigureAwait(false);
                        continue;
                    }
                }

                buffers.Clear();
                processingRequests.Clear();

                while (requests.TryDequeue(out var request))
                {
                    if (request.IsSingleBuffer)
                    {
                        var flushResult = await _pipeWriter.WriteAsync(request.Buffer, _connectionClosingCts.Token);
                        if (flushResult.IsCanceled)
                        {
                            error = new OperationCanceledException();
                            break;
                        }

                        if (flushResult.IsCompleted)
                        {
                            break;
                        }
                    }
                    else
                    {
                        foreach (var buffer in request.Buffers)
                        {
                            var flushResult = await _pipeWriter.WriteAsync(buffer);
                            if (flushResult.IsCanceled)
                            {
                                error = new OperationCanceledException();
                                break;
                            }

                            if (flushResult.IsCompleted)
                            {
                                break;
                            }
                        }
                    }
                }

                if (error is not null)
                {
                    // Bubble the error up
                    break;
                }

                // Signal that the requests are completed
                foreach (var request in processingRequests)
                {
                    request.SetResult();
                }
            }
        }
        catch (ObjectDisposedException ex)
        {
            // This exception should always be ignored because _shutdownReason should be set.
            error = ex;
        }
        catch (Exception ex)
        {
            // This is unexpected.
            error = ex;
        }
        finally
        {
            if (error is { })
            {
                foreach (var request in processingRequests)
                {
                    request.SetException(error);
                }
            }

            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();
            _readSignal.Signal();

            lock (_writesLock)
            {
                _writesCompleted = true;
            }

            // Drain requests.
            while (requests.TryDequeue(out var request) || _writeRequests.TryDequeue(out request))
            {
                request.SetException(_shutdownReason!);
            }
        }

        void RefreshRequestQueue(ref Queue<WriteRequest> queue)
        {
            lock (_writesLock)
            {
                queue = Interlocked.Exchange(ref _writeRequests, queue);
            }
        }
    }

    public override string ToString() => $"InMemoryTransport()";
}
