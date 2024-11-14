// Copyright (c) Microsoft Corporation. All rights reserved.
// WorkerProcessConnection.cs

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using pb = Microsoft.Orleans.ProtocolBuffers;

namespace Orleans.Grpc.Server.Gateway;

// TODO: eliminate?
public interface IWorkerGateway : IGrainObserver
{
    ValueTask<pb.Response> InvokeRequest(pb.Request request);
}

internal sealed class WorkerConnection : IAsyncDisposable
{
    private static long _nextConnectionId;
    private readonly Task _readTask;
    private readonly Task _writeTask;
    private readonly string _connectionId = Interlocked.Increment(ref _nextConnectionId).ToString();
    private readonly object _lock = new();
    private readonly HashSet<string> _supportedTypes = [];
    private readonly WorkerConnectionManager _gateway;
    private readonly CancellationTokenSource _shutdownCancellationToken = new();

    public WorkerConnection(WorkerConnectionManager agentWorker, IAsyncStreamReader<pb.WorkerMessage> requestStream, IServerStreamWriter<pb.GatewayMessage> responseStream, ServerCallContext context)
    {
        _gateway = agentWorker;
        RequestStream = requestStream;
        ResponseStream = responseStream;
        ServerCallContext = context;
        _outboundMessages = Channel.CreateUnbounded<pb.GatewayMessage>(new UnboundedChannelOptions { AllowSynchronousContinuations = true, SingleReader = true, SingleWriter = false });

        var didSuppress = false;
        if (!ExecutionContext.IsFlowSuppressed())
        {
            didSuppress = true;
            ExecutionContext.SuppressFlow();
        }

        try
        {
            _readTask = Task.Run(RunReadPump);
            _writeTask = Task.Run(RunWritePump);
        }
        finally
        {
            if (didSuppress)
            {
                ExecutionContext.RestoreFlow();
            }
        }

        Completion = Task.WhenAll(_readTask, _writeTask);
    }

    public IAsyncStreamReader<pb.WorkerMessage> RequestStream { get; }
    public IServerStreamWriter<pb.GatewayMessage> ResponseStream { get; }
    public ServerCallContext ServerCallContext { get; }

    private readonly Channel<pb.GatewayMessage> _outboundMessages;

    public void AddSupportedType(string type)
    {
        lock (_lock)
        {
            _supportedTypes.Add(type);
        }
    }

    public HashSet<string> GetSupportedTypes()
    {
        lock (_lock)
        {
            return new HashSet<string>(_supportedTypes);
        }
    }

    public async Task SendMessage(pb.GatewayMessage message)
    {
        await _outboundMessages.Writer.WriteAsync(message).ConfigureAwait(false);
    }

    public Task Completion { get; }

    public async Task RunReadPump()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await foreach (var message in RequestStream.ReadAllAsync(_shutdownCancellationToken.Token))
            {

                // Fire and forget
                _gateway.OnReceivedMessageAsync(this, message).Ignore();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdownCancellationToken.Cancel();
            _gateway.OnRemoveWorkerProcess(this);
        }
    }

    public async Task RunWritePump()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await foreach (var message in _outboundMessages.Reader.ReadAllAsync(_shutdownCancellationToken.Token))
            {
                await ResponseStream.WriteAsync(message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdownCancellationToken.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCancellationToken.Cancel();
        await Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public override string ToString() => $"Connection-{_connectionId}";
}
