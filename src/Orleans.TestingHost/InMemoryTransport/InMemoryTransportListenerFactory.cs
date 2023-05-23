#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Transport;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.TestingHost.InMemoryTransport;

internal static class InMemoryTransportExtensions
{
    public static IListenerBuilder UseInMemoryConnectionTransport(this IListenerBuilder builder, InMemoryTransportConnectionHub hub)
    {
        // Add a listener and a factory
        builder.Services.AddSingletonNamedService<MessageTransportListener>(
            builder.EndpointName,
            (sp, name) => new InMemoryTransportListener(
                name,
                sp.GetRequiredService<ILocalSiloDetails>().SiloAddress.Endpoint.ToString(),
                hub,
                sp.GetRequiredService<ILoggerFactory>()));

        return builder;
    }

    public static IConnectorBuilder UseInMemoryConnectionTransport(this IConnectorBuilder builder, InMemoryTransportConnectionHub hub)
    {
        builder.Services.AddSingletonNamedService<MessageTransportConnector>(builder.EndpointName, (sp, name) => new InMemoryTransportConnector(name, hub, sp.GetRequiredService<ILoggerFactory>()));
        return builder;
    }
}

internal class InMemoryTransportListener : MessageTransportListener
{
    private readonly Channel<(InMemoryMessageTransport Connection, TaskCompletionSource<bool> ConnectionAcceptedTcs)> _acceptQueue = Channel.CreateUnbounded<(InMemoryMessageTransport, TaskCompletionSource<bool>)>();
    private readonly string _endpointValue;
    private readonly InMemoryTransportConnectionHub _hub;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CancellationTokenSource _disposedCts = new();

    public InMemoryTransportListener(string endpointName, string endpointValue, InMemoryTransportConnectionHub hub, ILoggerFactory loggerFactory)
    {
        EndpointName = endpointName;
        _endpointValue = endpointValue;
        _hub = hub;
        _loggerFactory = loggerFactory;
    }

    public CancellationToken OnDisposed => _disposedCts.Token;

    public override bool IsValid => true;
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override string EndpointName { get; }

    public async Task AddConnection(InMemoryMessageTransport connection)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_acceptQueue.Writer.TryWrite((connection, completion)))
        {
            var connected = await completion.Task;
            if (connected)
            {
                return;
            }
        }

        throw new ConnectionFailedException($"Unable to connect to endpoint because its listener has terminated.");
    }

    public override async ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (await _acceptQueue.Reader.WaitToReadAsync(cancellationToken))
        {
            if (_acceptQueue.Reader.TryRead(out var item))
            {
                // Set the result to true to indicate that the connection was accepted.
                item.ConnectionAcceptedTcs.TrySetResult(true);

                return item.Connection;
            }
        }

        return null;
    }

    public override ValueTask<EndpointInfo> BindAsync(CancellationToken cancellationToken = default)
    {
        _hub.RegisterConnectionListenerFactory(_endpointValue, this);
        var info = new EndpointInfo(EndpointName)
        {
            ["ep"] = _endpointValue
        };
        return new (info);
    }

    public override ValueTask DisposeAsync()
    {
        return UnbindAsync(default);
    }

    public override ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        _acceptQueue.Writer.TryComplete();
        while (_acceptQueue.Reader.TryRead(out var item))
        {
            // Set the result to false to indicate that the listener has terminated.
            item.ConnectionAcceptedTcs.TrySetResult(false);
        }

        _disposedCts.Cancel();
        return default;
    }
}

internal class InMemoryTransportConnectionHub
{
    private readonly ConcurrentDictionary<string, InMemoryTransportListener> _listeners = new();

    public static InMemoryTransportConnectionHub Instance { get; } = new();

    public void RegisterConnectionListenerFactory(string endpoint, InMemoryTransportListener listener)
    {
        _listeners[endpoint] = listener;
        listener.OnDisposed.Register(() =>
        {
            ((IDictionary<string, InMemoryTransportListener>)_listeners).Remove(new KeyValuePair<string, InMemoryTransportListener>(endpoint, listener));
        });
    }

    public InMemoryTransportListener? GetConnectionListenerFactory(string endpoint)
    {
        _listeners.TryGetValue(endpoint, out var listener);
        return listener;
    }
}

internal class InMemoryTransportConnector : MessageTransportConnector
{
    private readonly InMemoryTransportConnectionHub _hub;
    private readonly ILogger<InMemoryMessageTransport> _connectionLogger;

    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override bool IsValid => true;
    public override string EndpointName { get; }

    public InMemoryTransportConnector(string name, InMemoryTransportConnectionHub hub, ILoggerFactory loggerFactory)
    {
        EndpointName = name;
        _hub = hub;
        _connectionLogger = loggerFactory.CreateLogger<InMemoryMessageTransport>();
    }

    public override async ValueTask<MessageTransport> CreateAsync(EndpointInfo endpoint, CancellationToken cancellationToken = default)
    {
        if (!endpoint.TryGetValue("ep", out var endpointValue))
        {
            throw new KeyNotFoundException($"Endpoint missing \"ep\" entry");
        }

        var listener = _hub.GetConnectionListenerFactory(endpointValue)!;

        var pipePair = DuplexPipe.CreatePair();
        var local = new InMemoryMessageTransport(pipePair.Left, _connectionLogger);
        var remote = new InMemoryMessageTransport(pipePair.Right, _connectionLogger);
        await listener.AddConnection(remote);
        return local;
    }

    private class DuplexPipe : IDuplexPipe
    {
        public required PipeReader Input { get; init; }
        public required PipeWriter Output { get; init; }

        public static (DuplexPipe Left, DuplexPipe Right) CreatePair()
        {
            var pipeOptions = new PipeOptions(readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline, useSynchronizationContext: false);
            var one = new Pipe(pipeOptions);
            var two = new Pipe(pipeOptions);
            var left = new DuplexPipe { Input = one.Reader, Output = two.Writer };
            var right = new DuplexPipe { Input = two.Reader, Output = one.Writer };
            return (left, right);
        }
    }
}
