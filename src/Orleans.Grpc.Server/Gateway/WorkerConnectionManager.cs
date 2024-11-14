// Copyright (c) Microsoft Corporation. All rights reserved.
// WorkerGateway.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using pb = Microsoft.Orleans.ProtocolBuffers;
using System.Threading;
using Microsoft.AutoGen.Runtime;

namespace Orleans.Grpc.Server.Gateway;

internal sealed class WorkerConnectionManager : BackgroundService, IWorkerGateway
{
    private static readonly TimeSpan AgentResponseTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<WorkerConnectionManager> _logger;
    private readonly IAgentWorkerRegistryGrain _gatewayRegistry;
    private readonly IWorkerGateway _reference;

    // The local mapping of agents to worker processes.
    private readonly ConcurrentDictionary<WorkerConnection, WorkerConnection> _workers = new();

    // The agents supported by each worker process.
    private readonly ConcurrentDictionary<string, List<WorkerConnection>> _supportedAgentTypes = [];

    // The mapping from agent id to worker process.
    private readonly ConcurrentDictionary<(string Type, string Key), WorkerConnection> _agentDirectory = new();

    // RPC
    private readonly ConcurrentDictionary<(WorkerConnection, ulong), TaskCompletionSource<pb.Response>> _pendingRequests = new();

    public WorkerConnectionManager(IClusterClient clusterClient, ILogger<WorkerConnectionManager> logger)
    {
        _logger = logger;
        _reference = clusterClient.CreateObjectReference<IWorkerGateway>(this);
        _gatewayRegistry = clusterClient.GetGrain<IAgentWorkerRegistryGrain>(0);
    }

    public async ValueTask<pb.Response> InvokeRequest(pb.Request request)
    {
        (string Type, string Key) agentId = (request.Target.Type, request.Target.Key);
        if (!_agentDirectory.TryGetValue(agentId, out var connection) || connection.Completion.IsCompleted)
        {
            // Activate the agent on a compatible worker process.
            if (_supportedAgentTypes.TryGetValue(request.Target.Type, out var workers))
            {
                connection = workers[Random.Shared.Next(workers.Count)];
                _agentDirectory[agentId] = connection;
            }
            else
            {
                return new(new pb.Response { Body = new pb.ResultOrError { Error = new pb.EncodedValue { TextData = "Agent not found." } } });
            }
        }

        // Proxy the request to the agent.
        var completion = _pendingRequests[(connection, request.RequestId)] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await connection.ResponseStream.WriteAsync(new pb.GatewayMessage { Request = request });

        // Wait for the response and send it back to the caller.
        var response = await completion.Task.WaitAsync(AgentResponseTimeout);
        response.RequestId = request.RequestId;
        return response;
    }

    private void DispatchResponse(WorkerConnection connection, pb.Response response)
    {
        if (!_pendingRequests.TryRemove((connection, response.RequestId), out var completion))
        {
            _logger.LogWarning("Received response for unknown request.");
            return;
        }

        // Complete the request.
        completion.SetResult(response);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _gatewayRegistry.AddWorker(_reference);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Error adding worker to registry.");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }

        try
        {
            await _gatewayRegistry.RemoveWorker(_reference);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Error removing worker from registry.");
        }
    }

    internal async Task OnReceivedMessageAsync(WorkerConnection connection, pb.WorkerMessage message)
    {
        _logger.LogInformation("Received message {Message} from connection {Connection}.", message, connection);
        switch (message.MessageCase)
        {
            case pb.WorkerMessage.MessageOneofCase.Request:
                await DispatchRequestAsync(connection, message.Request);
                break;
            case pb.WorkerMessage.MessageOneofCase.Response:
                DispatchResponse(connection, message.Response);
                break;
            default:
                throw new InvalidOperationException($"Unknown message type for message '{message}'.");
        };
    }

    private async ValueTask DispatchRequestAsync(WorkerConnection connection, pb.Request request)
    {
        var requestId = request.RequestId;
        if (request.Target is null)
        {
            throw new InvalidOperationException($"Request message is missing a target. Message: '{request}'.");
        }

        await InvokeRequestDelegate(connection, request, async request =>
        {
            var (gateway, isPlacement) = await _gatewayRegistry.GetOrPlaceAgent(request.Target);
            if (gateway is null)
            {
                return new pb.Response { Body = new pb.ResultOrError { Error = new pb.EncodedValue { TextData = "Agent not found and no compatible gateways were found." } } };
            }

            if (isPlacement)
            {
                // Activate the worker: load state
                // TODO
            }

            // Forward the message to the gateway and return the result.
            return await gateway.InvokeRequest(request);
        });
    }

    private static async Task InvokeRequestDelegate(WorkerConnection connection, pb.Request request, Func<pb.Request, Task<pb.Response>> func)
    {
        try
        {
            var response = await func(request);
            response.RequestId = request.RequestId;
            await connection.ResponseStream.WriteAsync(new pb.GatewayMessage { Response = response });
        }
        catch (Exception ex)
        {
            await connection.ResponseStream.WriteAsync(new pb.GatewayMessage
            {
                Response = new pb.Response
                {
                    RequestId = request.RequestId,
                    Body = new pb.ResultOrError
                    {
                        Error = new pb.EncodedValue
                        {
                            TextData = ex.Message
                        }
                    }
                }
            });
        }
    }

    internal Task ConnectToWorkerProcess(IAsyncStreamReader<pb.WorkerMessage> requestStream, IServerStreamWriter<pb.GatewayMessage> responseStream, ServerCallContext context)
    {
        _logger.LogInformation("Received new connection from {Peer}.", context.Peer);
        var workerProcess = new WorkerConnection(this, requestStream, responseStream, context);
        _workers[workerProcess] = workerProcess;
        return workerProcess.Completion;
    }

    internal void OnRemoveWorkerProcess(WorkerConnection workerProcess)
    {
        _workers.TryRemove(workerProcess, out _);
        var types = workerProcess.GetSupportedTypes();
        foreach (var type in types)
        {
            if (_supportedAgentTypes.TryGetValue(type, out var supported))
            {
                supported.Remove(workerProcess);
            }
        }

        // Any agents activated on that worker are also gone.
        foreach (var pair in _agentDirectory)
        {
            if (pair.Value == workerProcess)
            {
                ((IDictionary<(string Type, string Key), WorkerConnection>)_agentDirectory).Remove(pair);
            }
        }
    }
}
