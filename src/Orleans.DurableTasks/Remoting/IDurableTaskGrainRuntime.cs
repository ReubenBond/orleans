using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks.Remoting;

public interface IDurableTaskClient
{
    // Called when a remotely scheduled request completes
    ValueTask OnResponse(TaskId taskId, Response response);
}

public interface IDurableTaskServer 
{
    // Called by DurableTaskRequest.Invoke to ensure that a task is scheduled
    ValueTask ScheduleAsync(IDurableTaskRequest request);

    // API used by ScheduledTask/<T> to check for a result for a task.
    // The ScheduledTask does not have access to the original request, so it cannot submit a sensible IDurableTaskRequest.
    ValueTask<Response> SubscribeOrPollAsync(TaskId taskId, IDurableTaskClient? client);
}

public interface IDurableTaskGrainExtension : IGrainExtension, IDurableTaskServer, IDurableTaskClient
{
}

public interface IDurableTaskGrainRuntime
{
    // Similar to `ScheduleOrPollAsync`, except that:
    // It is intended for local `DurableTask` methods (steps) versus remotely issued requests
    // The DurableTaskRequest is not serializable to storage.
    // It blocks until the response has been completed, rather than returning a pending result.
    ValueTask EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition);
    ValueTask<TResult> EvaluateStepAsync<TResult>(TaskId taskId, DurableTask<TResult> taskDefinition);
    ValueTask<ScheduledTask> ScheduleLocallyAsync(DurableTaskRequest durableTaskRequest);
    ValueTask<ScheduledTask<TResult>> ScheduleLocallyAsync<TResult>(DurableTaskRequest<TResult> durableTaskRequest);
}

internal class DurableTaskGrainExtension : IDurableTaskGrainRuntime, IDurableTaskGrainExtension
{
    private readonly Dictionary<TaskId, DurableTaskExecutionContext> _pendingTasks = new();
    private readonly Dictionary<TaskId, Task> _runningTasks = new();
    private readonly ILogger<DurableTaskGrainExtension> _logger;
    private readonly IDurableTaskGrainStorage _storage;
    private readonly ISystemClock _systemClock;
    private readonly CleanupPolicy _defaultCleanupPolicy = new CleanupPolicy { CleanupAge = TimeSpan.FromDays(1) };

    public DurableTaskGrainExtension(ILogger<DurableTaskGrainExtension> logger, IDurableTaskGrainStorage storage, ISystemClock systemClock)
    {
        _logger = logger;
        _storage = storage;
        _systemClock = systemClock;
    }

    private DurableTaskExecutionContext CreateExecutionContext(TaskId taskId, DurableTaskState state) => _pendingTasks[taskId] = new DurableTaskExecutionContext(taskId, this, state);

    private bool TryGetExecutionContext(TaskId taskId, [NotNullWhen(true)] out DurableTaskExecutionContext? executionContext)
    {
        // Is an active method already waiting for this?
        if (_pendingTasks.TryGetValue(taskId, out executionContext))
        {
            return true;
        }

        if (_storage.TryGetTask(taskId, out var state))
        {
            // Rehydrate the execution context from its persisted state.
            executionContext = new DurableTaskExecutionContext(taskId, this, state);

            // If the task has completed, set the result now.
            if (state.Result is { } response)
            {
                executionContext.SetResponse(response);
            }

            // Move the task into the list of active tasks.
            _pendingTasks[taskId] = executionContext;
            return true;
        }

        return false;
    }

    public async ValueTask OnResponse(TaskId taskId, Response response)
    {
        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            // No such task. This may be because this client has already received a response for this task and removed its entry for it.
            // TODO: Perhaps this should log at a lower level since it is likely not the symptom of a bug or exceptional condition.
            _logger.LogWarning("Received response for unknown task {TaskId}: {Response}", taskId, response);
            return;
        }

        // Persist the response before responding to the caller.
        // TODO: If this write (or just about any state write) fails, then we need to undo the update to the task state.
        // The most straightforward way to do that might be to take a copy before mutating it.
        executionContext.State.Result = response;
        executionContext.State.CompletedAt = _systemClock.GetUtcNow();
        _storage.AddOrUpdateTask(taskId, executionContext.State);
        await _storage.WriteAsync();

        // Propagate the response to the application.
        executionContext.SetResponse(response);
    }

    async ValueTask IDurableTaskServer.ScheduleAsync(IDurableTaskRequest request)
    {
        if (request.Context is not { } requestContext)
        {
            throw new InvalidOperationException($"No context for durable task request {request}");
        }

        var taskId = requestContext.TaskId;
        var client = requestContext.Caller?.Cast<IDurableTaskGrainExtension>();
        if (TryGetExecutionContext(taskId, out var executionContext))
        {
            // Ensure the client is subscribed to the existing task.
            await SubscribeClientAsync(taskId, executionContext, client);
            return;
        }

        // Schedule the task
        await ScheduleNewTaskRequestAsync(request, client);
    }

    private async ValueTask SubscribeClientAsync(TaskId taskId, DurableTaskExecutionContext executionContext, IDurableTaskClient? client)
    {
        if (client is not null)
        {
            var existingClients = executionContext.State.Clients;
            if (existingClients is null || !existingClients.Contains(client))
            {
                var state = executionContext.State;

                // Add the client to the persisted task state.
                state.Clients ??= new();
                state.Clients.Add(client);
                _storage.AddOrUpdateTask(taskId, state);
                await _storage.WriteAsync();
            }
        }
    }

    private async ValueTask ScheduleNewTaskRequestAsync(IDurableTaskRequest request, IDurableTaskClient? client)
    {
        var taskId = request.Context!.TaskId;
        Debug.Assert(!_pendingTasks.ContainsKey(taskId));
        var newTaskState = new DurableTaskState
        {
            Request = request,
            CreatedAt = _systemClock.GetUtcNow()
        };

        if (client is not null)
        {
            newTaskState.Clients = new HashSet<IDurableTaskClient> { client };
        }

        _storage.AddOrUpdateTask(taskId, newTaskState);
        await _storage.WriteAsync();

        // Schedule the task with the runtime.
        var executionContext = CreateExecutionContext(taskId, newTaskState);
        InvokeRequestMethod(taskId, request, executionContext);
    }

    public async ValueTask EvaluateStepAsync(TaskId taskId, DurableTask durableTask)
    {
        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            executionContext = await CreateExecutionContextAsync(taskId);
        }

        var responseTask = executionContext.AsValueTask();

        // If the task has already completed, do not start it again.
        if (!responseTask.IsCompleted)
        {
            try
            {
                // Invoke the method immediately.
                await durableTask.InvokeAsyncUntypedCore(executionContext);
                await CompleteRequestWithResponse(taskId, Response.Completed, executionContext);
            }
            catch (Exception exception)
            {
                await CompleteRequestWithResponse(taskId, Response.FromException(exception), executionContext);
            }
        }

        var response = await responseTask;
        _ = response.Result;
    }

    public async ValueTask<TResult> EvaluateStepAsync<TResult>(TaskId taskId, DurableTask<TResult> durableTask)
    {
        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            executionContext = await CreateExecutionContextAsync(taskId);
        }

        var responseTask = executionContext.AsValueTask();
        if (!responseTask.IsCompleted)
        {
            try
            {
                var newResponse = await durableTask.InvokeAsyncTypedCore(executionContext);
                await CompleteRequestWithResponse(taskId, Response.FromResult(newResponse), executionContext);
            }
            catch (Exception exception)
            {
                await CompleteRequestWithResponse(taskId, Response.FromException(exception), executionContext);
            }
        }

        var response = await executionContext.AsValueTask();
        return response.GetResult<TResult>();
    }

    private async Task<DurableTaskExecutionContext> CreateExecutionContextAsync(TaskId taskId)
    {
        var newTaskState = new DurableTaskState
        {
            // The request is not propagated to the task state because it is not serializable.
            // It represents a local method call (a lambda, local function, class method, etc).
            CreatedAt = _systemClock.GetUtcNow()
        };

        _storage.AddOrUpdateTask(taskId, newTaskState);
        await _storage.WriteAsync();

        return CreateExecutionContext(taskId, newTaskState);
    }

    private void InvokeRequestMethod(TaskId taskId, IDurableTaskRequest request, DurableTaskExecutionContext context)
    {
        _runningTasks.Add(taskId, InvokeTaskAsyncInternal(taskId, request, context));
    }

    private async Task InvokeTaskAsyncInternal(TaskId taskId, IDurableTaskRequest request, DurableTaskExecutionContext context)
    {
        await Task.Yield();

        try
        {
            var response = await request.InvokeImplementation(context);
            await CompleteRequestWithResponse(taskId, response, context);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error invoking durable task request {Request}", request);
            await CompleteRequestWithResponse(taskId, Response.FromException(exception), context);
        }
    }

    private async Task CompleteRequestWithResponse(TaskId taskId, Response response, DurableTaskExecutionContext executionContext)
    {
        var state = executionContext.State;
        if (state.Result is null)
        {
            Debug.Assert(state.Result is null);

            // Store the result.
            // Note that this and the next call to notify callers may result in two writes in quick succession.
            // That is ok: we want to ensure that every client always sees the same result for a task, so it is important to persist the task before notifying the first client.
            state.Result = response;
            state.CompletedAt = _systemClock.GetUtcNow();
            _storage.AddOrUpdateTask(taskId, state);
            await _storage.WriteAsync();
        }

        await NotifyClientsAndCleanupTask(taskId, executionContext);
    }

    private async Task NotifyClientsAndCleanupTask(TaskId taskId, DurableTaskExecutionContext executionContext)
    {
        Debug.Assert(executionContext.State.Result is not null);
        while (true)
        {
            try
            {
                var clientTasks = new List<Task>();
                var clientCount = 0;
                
                var state = executionContext.State;
                if (state.Clients is { } clients)
                {
                    clientCount = clients.Count;
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("Notifying {ClientsCount} clients for completion of task {TaskId}", clientCount, taskId);
                    }

                    var response = state.Result;
                    foreach (var client in clients)
                    {
                        clientTasks.Add(client.OnResponse(taskId, response).AsTask());
                    }
                }

                await Task.WhenAll(clientTasks);

                state.Clients = null;
                _storage.AddOrUpdateTask(taskId, state);

                PruneCompletedTasks();
                await _storage.WriteAsync();
                _logger.LogTrace("Notified {ClientsCount} clients for completion of task {TaskId}", clientCount, taskId);

                // Success, no more work to be done right now.
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Exception while notifying clients of completion for durable task {TaskId}", taskId);
            }

            // TODO: Make this configurable and probably use exponential backoff, potentially with some coordination with other tasks.
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }

    private bool PruneCompletedTasks()
    {
        // Prune all tasks which:
        // * Have a response
        // * Have no remaining clients to notify
        // * Have no parents waiting on them within this context
        // * Have been completed for more than a configured period of time
        var allTasks = _storage.Tasks.ToDictionary(static task => task.Id, static task => task.State);
        HashSet<TaskId>? completedTaskIds = default;
        Dictionary<TaskId, HashSet<TaskId>>? waitingOnParent = default;
        var now = _systemClock.GetUtcNow();
        foreach (var (taskId, state) in allTasks)
        {
            if (state.Result is null)
            {
                // The task is incomplete.
                continue;
            }

            if (state.Clients is { Count: > 0 })
            {
                // There are still unacknowledged clients.
                continue;
            }

            if (state.CompletedAt is not { } completedAt || now.Subtract(completedAt) < _defaultCleanupPolicy.CleanupAge)
            {
                // The task is being retained for at least the specified period of time.
                continue;
            }

            if (taskId.Parent() is { } parent && parent != TaskId.None && allTasks.ContainsKey(parent))
            {
                // There is a local parent task which this task is waiting on, and that is the last thing keeping this task alive.
                waitingOnParent ??= new();
                ref var waiters = ref CollectionsMarshal.GetValueRefOrAddDefault(waitingOnParent, parent, out var exists);
                waiters ??= new();
                waiters.Add(taskId);
                continue;
            }

            completedTaskIds ??= new();
            completedTaskIds.Add(taskId);
        }

        if (completedTaskIds is not null)
        {
            foreach (var taskId in completedTaskIds)
            {
                // Prune all otherwise-completed children.
                if (waitingOnParent is not null && waitingOnParent.TryGetValue(taskId, out var childTaskIds))
                {
                    foreach (var childTaskId in childTaskIds)
                    {
                        _storage.RemoveTask(childTaskId);
                    }
                }

                // Prune the task.
                _storage.RemoveTask(taskId);
            }
        }

        return completedTaskIds is not null;
    }

    public async ValueTask<ScheduledTask> ScheduleLocallyAsync(DurableTaskRequest durableTaskRequest) => new UntypedScheduledTask(await ScheduleLocallyAsyncCore(durableTaskRequest));

    public async ValueTask<ScheduledTask<TResult>> ScheduleLocallyAsync<TResult>(DurableTaskRequest<TResult> durableTaskRequest) => new ScheduledTask<TResult>(await ScheduleLocallyAsyncCore(durableTaskRequest));

    private async ValueTask<DurableTaskExecutionContext> ScheduleLocallyAsyncCore(IDurableTaskRequest durableTaskRequest)
    {
        var context = durableTaskRequest.Context;
        Debug.Assert(context is not null);
        var taskId = context.TaskId;

        // Create a context locally, returning if it is already completed.
        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            executionContext = await CreateExecutionContextAsync(taskId);
        }

        // Invoke the task to submit it to the remote host.
        // If the task has already been submitted, then this will submit it again, which is an idempotent operation if:
        // * The task is semantically identical (same implementation and arguments).
        // * The task did not complete already and was subsequently cleaned up.
        // We can be sure that the task was not already cleaned up if we are calling from a grain which has a stable identifier, since
        // the caller must acknowledge completion before the task is eligible for garbage collection.
        // For the first point (identical implementation and arguments), we could store the task locally and verify it against its already-stored copy.
        // This check can also be performed remotely instead, since the remote host must have stored a copy of the request in order to be able to execute it.
        await durableTaskRequest.ScheduleRemoteAsync();

        // Return a scheduled task, which can be awaited to retrieve the result once it has been locally persisted.
        // For non-persistent contexts (such as an external client or hosted client), this can be implemented via polling instead, for example.
        return executionContext;
    }

    public ValueTask<Response> SubscribeOrPollAsync(TaskId taskId, IDurableTaskClient? client)
    {
        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            return new(UnknownTaskResponse.Instance);
        }

        var response = executionContext.State.Result;
        if (response is not null)
        {
            return new(response);
        }

        var subscribeTask = SubscribeClientAsync(taskId, executionContext, client);
        if (!subscribeTask.IsCompleted)
        {
            // Subscribe the client and return
            return AwaitSubscribeClientAsync(subscribeTask);
        }

        return new(PendingResponse.Instance);

        static async ValueTask<Response> AwaitSubscribeClientAsync(ValueTask subscribeTask)
        {
            await subscribeTask;
            return PendingResponse.Instance;
        }
    }
}
