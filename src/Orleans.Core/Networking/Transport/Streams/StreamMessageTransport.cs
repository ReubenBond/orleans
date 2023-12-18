#nullable enable

using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Internal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Connections.Transport.Streams;

public abstract class StreamMessageTransport : MessageTransportBase
{
    private readonly ILogger _logger;
    private readonly SingleWaiterAutoResetEvent _writerSignal = new();
    private readonly SingleWaiterAutoResetEvent _readerSignal = new();
    private readonly Queue<WriteRequest> _pendingWrites = new();
    private readonly Queue<ReadRequest> _pendingReads = new();
    private readonly CancellationTokenSource _connectionClosingCts = new();
    private readonly CancellationTokenSource _connectionClosedCts = new();
    private readonly object _writesLock = new();
    private readonly object _readsLock = new();
    private Task? _runTask;
    private Exception? _shutdownReason;

    protected StreamMessageTransport(ILogger logger)
    {
        _logger = logger;
    }

    protected abstract Stream Stream { get; }

    public virtual void Start()
    {
        using var _ = new ExecutionContextSuppressor();
        _runTask = Task.Run(RunAsync);
    }

    public override CancellationToken Closed => _connectionClosedCts.Token;

    public override async ValueTask CloseAsync(Exception? closeException)
    {
        _shutdownReason ??= closeException;
        _connectionClosingCts.Cancel();
        _readerSignal.Signal();
        _writerSignal.Signal();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectionClosedCts.Token.Register(OnClosed, completion, useSynchronizationContext: false);
        await completion.Task;

        static void OnClosed(object? state)
        {
            if (state is not TaskCompletionSource completion) throw new ArgumentException($"State must be a {nameof(TaskCompletionSource)}", nameof(state));
            completion.TrySetResult();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    public override bool ReadAsync(ReadRequest request)
    {
        if (_connectionClosingCts.IsCancellationRequested)
        {
            return false;
        }

        lock (_readsLock)
        {
            _pendingReads.Enqueue(request);
        }

        _readerSignal.Signal();
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
            _pendingWrites.Enqueue(request);
        }

        _writerSignal.Signal();
        return true;
    }

    private async Task RunAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        try
        {
            await RunAsyncCore();
        }
        finally
        {
            await CloseAsync(null);
        }
    }

    protected virtual async Task RunAsyncCore()
    {
        try
        {
            var readsTask = ProcessReads();
            var writesTask = ProcessWrites();
            await readsTask;
            await writesTask;
        }
        catch (Exception exception)
        {
            _shutdownReason ??= exception;
        }
        finally
        {
            _connectionClosedCts.Cancel();
        }
    }

    private async Task ProcessReads()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Exception? error = default;
        ReadRequest? operation = default;
        bool isGracefulTermination = false;
        try
        {
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                while (TryDequeue(out operation))
                {
                    while (true)
                    {
                        var bytesRead = await Stream.ReadAsync(operation.Buffer, _connectionClosingCts.Token);
                        if (bytesRead == 0 && operation.Buffer.Length > 0)
                        {
                            goto gracefulTermination;
                        }

                        if (operation.OnRead(bytesRead))
                        {
                            break;
                        }
                    }
                }

                await _readerSignal.WaitAsync();
            }

gracefulTermination:
            isGracefulTermination = true;
        }
        catch (Exception exception)
        {
            if (_connectionClosingCts.IsCancellationRequested)
            {
                isGracefulTermination = true;
            }
            else
            {
                error ??= exception;
                isGracefulTermination = false;
            }
        }
        finally
        {
            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();
            if (isGracefulTermination)
            {
                operation?.OnCanceled();
            }
            else
            {
                Debug.Assert(error is not null);
                operation?.OnError(error);
            }

            while (TryDequeue(out operation))
            {
                if (isGracefulTermination)
                {
                    operation.OnCanceled();
                }
                else
                {
                    Debug.Assert(error is not null);
                    operation.OnError(error);
                }
            }

            _writerSignal.Signal();

            if (error is not null)
            {
                _logger.LogError(0, error, $"Unexpected exception in {nameof(StreamMessageTransport)}.{nameof(ProcessReads)}.");
            }
        }

        bool TryDequeue([NotNullWhen(true)] out ReadRequest? operation)
        {
            lock (_readsLock)
            {
                return _pendingReads.TryDequeue(out operation);
            }
        }
    }

    private async Task ProcessWrites()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Exception? error = default;
        WriteRequest? operation = default;
        try
        {
            while (!_connectionClosingCts.IsCancellationRequested)
            {
                while (TryDequeue(out operation))
                {
                    foreach (var buffer in operation.Buffers)
                    {
                        await Stream.WriteAsync(buffer, _connectionClosingCts.Token);
                    }

                    operation.SetResult();
                }

                await _writerSignal.WaitAsync();
            }
        }
        catch (Exception exception)
        {
            error ??= exception;
        }
        finally
        {
            _shutdownReason ??= error;
            _connectionClosingCts.Cancel();
            var requestError = _shutdownReason ?? new ConnectionClosedException();
            operation?.SetException(requestError);

            if (error is not null)
            {
                _logger.LogError(0, error, $"Unexpected exception in {nameof(StreamMessageTransport)}.{nameof(ProcessWrites)}.");
            }

            lock (_writesLock)
            {
                while (_pendingWrites.TryDequeue(out operation))
                {
                    operation.SetException(requestError);
                }
            }
        }

        bool TryDequeue([NotNullWhen(true)] out WriteRequest? operation)
        {
            lock (_writesLock)
            {
                return _pendingWrites.TryDequeue(out operation);
            }
        }
    }
}
