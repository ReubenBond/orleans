#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Internal;

namespace Orleans.Networking.Transport;

internal class StreamNetworkTransport : NetworkTransport
{
    private readonly ILogger _logger;
    private readonly Stream _stream;
    private readonly Action _onReadProgress;
    private readonly Action _signalWriteLoop;
    private readonly SingleWaiterInlineSignal _writerSignal = new();
    private readonly Queue<WriteRequest> _pendingWrites = new();
    private readonly Task _processWritesTask;
    private readonly CancellationTokenSource _connectionClosingCts = new();
    private readonly CancellationTokenSource _connectionClosedCts = new();
    private Memory<byte> _readRequestBuffer;
    private ReadRequest? _readRequest;
    private WriteRequest? _writeRequest;
    private ValueTaskAwaiter<int> _outstandingReadAsync;

    public StreamNetworkTransport(Stream stream, ILogger logger)
    {
        _logger = logger;
        _stream = stream;
        _onReadProgress = OnReadProgress;
        _signalWriteLoop = _writerSignal.Signal;

        {
            using var _ = new ExecutionContextSuppressor();
            _processWritesTask = Task.Run(ProcessWrites);
        }
    }

    public override CancellationToken Closed => throw new NotImplementedException();

    public override async ValueTask CloseAsync(Exception? closeException)
    {
        _connectionClosingCts.Cancel();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectionClosedCts.Token.Register(OnClosed, completion, useSynchronizationContext: false);
        await completion.Task;

        static void OnClosed(object? state)
        {
            if (state is not TaskCompletionSource completion) throw new ArgumentException(nameof(state));
            completion.TrySetResult();
        }
    }

    public override ValueTask DisposeAsync() => throw new NotImplementedException();

    public override bool ReadAsync(ReadRequest request)
    {
        if (_connectionClosingCts.IsCancellationRequested)
        {
            return false;
        }

        _readRequest = request;
        _readRequestBuffer = _readRequest.Buffer;
        _outstandingReadAsync = _stream.ReadAsync(request.Buffer).GetAwaiter();
        _outstandingReadAsync.OnCompleted(_onReadProgress);
        return true;
    }

    public override bool WriteAsync(WriteRequest request)
    {
        if (_connectionClosingCts.IsCancellationRequested)
        {
            return false;
        }

        _pendingWrites.Enqueue(request);
        _writerSignal.Signal();
        return true;
    }

    private void OnReadProgress()
    {
        var bytesRead = _outstandingReadAsync.GetResult();
        bool completed;
        if (bytesRead == 0)
        {
            _readRequest!.OnError(new EndOfStreamException());
            _connectionClosingCts.Cancel();
            completed = true;
        }
        else if (_readRequest!.OnProgress(bytesRead))
        {
            completed = true;
        }
        else
        {
            completed = false;
        }

        if (completed)
        {
            ResetReadRequest();
        }
        else
        {
            _readRequestBuffer = _readRequestBuffer.Slice(bytesRead);
            _outstandingReadAsync = _stream.ReadAsync(_readRequest.Buffer).GetAwaiter();
            _outstandingReadAsync.OnCompleted(_onReadProgress);
        }

        void ResetReadRequest()
        {
            _readRequest = default;
            _readRequestBuffer = default;
            _outstandingReadAsync = default;
        }
    }

    private async Task ProcessWrites()
    {
        Exception? error = default;
        try
        {
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                while (_pendingWrites.TryDequeue(out var operation))
                {
                    try
                    {
                        if (operation.IsSingleBuffer)
                        {
                            await _stream.WriteAsync(operation.Buffer);
                        }
                        else
                        {
                            foreach (var buffer in operation.Buffers)
                            {
                                await _stream.WriteAsync(buffer);
                            }
                        }

                        operation.OnCompleted();
                    }
                    catch (Exception exception)
                    {
                        error = exception;
                        operation.OnError(exception);
                        break;
                    }
                }

                if (error is not null)
                {
                    break;
                }

                await _writerSignal.WaitAsync();
            }
        }
        catch (Exception exception)
        {
            error = exception;
        }
        finally
        {
            if (error is not null)
            {
                _logger.LogError(0, error, $"Unexpected exception in {nameof(StreamNetworkTransport)}.{nameof(ProcessWrites)}.");
            }

            _connectionClosingCts.Cancel();

            _connectionClosedCts.Cancel();
        }
    }
}
