using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization;
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
    ValueTask<Response> ScheduleOrPollAsync(IDurableTaskRequest request, IDurableTaskClient? caller);

    // API used by ScheduledTask/<T> to check for a result for a task.
    // The ScheduledTask does not have access to the original request, so it cannot submit a sensible IDurableTaskRequest.
    ValueTask<Response> SubscribeOrPollAsync(TaskId taskId, IDurableTaskClient? client);
}

public interface IDurableTaskGrainExtension : IGrainExtension, IDurableTaskServer, IDurableTaskClient
{
}

public interface IDurableTaskGrainRuntime : IDurableTaskGrainExtension
{
    // Similar to `ScheduleOrPollAsync`, except that:
    // It is intended for local `DurableTask` methods (steps) versus remotely issued requests
    // The DurableTaskRequest is not serializable to storage.
    // It blocks until the response has been completed, rather than returning a pending result.
    ValueTask EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition);
    ValueTask<TResult> EvaluateStepAsync<TResult>(TaskId taskId, DurableTask<TResult> taskDefinition);
    ValueTask<ScheduledTask> OnScheduleAsync(DurableTaskRequest durableTaskRequest);
    ValueTask<ScheduledTask<TResult>> OnScheduleAsync<TResult>(DurableTaskRequest<TResult> durableTaskRequest);
}

/*
 * Grain activates
 * Grain enumerates stored pending tasks and re-invokes any which are not completed.
 *   * Some tasks will not be directly invokable, since they represent local methods on a grain (not remote requests to the grain)
     * Those tasks do not need to be invoked.
 */

[GenerateSerializer]
public class DurableTaskState
{
    /// <summary>
    /// Gets or sets the result of this task.
    /// </summary>
    [Id(0)]
    public Response? Result { get; set; }

    /// <summary>
    /// Gets or sets the set of clients which are interested in the result of this task.
    /// </summary>
    /// <remarks>
    /// This task cannot be retired until all clients have acknowledged the task's result.
    /// If the task has a parent task (determined using the task's hierarchical identifier), then the result will not be retired until that
    /// In the case of nested tasks (eg, defined by local methods), there will typically be no clients.
    /// In that case, the result will not be 
    /// </remarks>
    [Id(1)]
    public HashSet<IDurableTaskClient>? Clients { get; set; }

    /// <summary>
    /// Gets or sets the invokable request.
    /// </summary>
    [Id(2)]
    public IDurableTaskRequest? Request { get; set; }

    /// <summary>
    /// Gets or sets the time that the task completed.
    /// </summary>
    [Id(3)]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the time that the task was created.
    /// </summary>
    [Id(4)]
    public DateTime CreatedAt { get; set; }
}

// TODO: In designing this interface, perhaps we should model mutations in a finer-grained manner to facilitate efficient log-based storage approach.
// Eg: Separate AddRequest, Add/RemoveClient, SetResponse methods.
internal interface IDurableTaskGrainStorage
{
    IEnumerable<(TaskId Id, DurableTaskState State)> Tasks { get; }
    void AddOrUpdateTask(TaskId taskId, DurableTaskState state);
    bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out DurableTaskState? state);

    // Removes a request and its state
    bool RemoveTask(TaskId taskId);
    
    ValueTask WriteAsync();
    ValueTask ReadAsync();
}

internal class DurableTaskGrainStorage : IDurableTaskGrainStorage
{
    private Dictionary<TaskId, DurableTaskState> _workingCopy = new();
    private Dictionary<TaskId, DurableTaskState> _persistedCopy = new();
    private readonly DeepCopier<Dictionary<TaskId, DurableTaskState>> _storageCopier;
    private readonly DeepCopier<DurableTaskState> _stateCopier;

    public IEnumerable<(TaskId Id, DurableTaskState State)> Tasks => _workingCopy.Select(static pair => (pair.Key, pair.Value));

    public DurableTaskGrainStorage(DeepCopier<Dictionary<TaskId, DurableTaskState>> storageCopier, DeepCopier<DurableTaskState> stateCopier)
    {
        _storageCopier = storageCopier;
        _stateCopier = stateCopier;
    }

    public void AddOrUpdateTask(TaskId taskId, DurableTaskState state) => _workingCopy[taskId] = _stateCopier.Copy(state);
    public bool RemoveTask(TaskId taskId) => _workingCopy.Remove(taskId);
    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out DurableTaskState? state)
    {
        if (_workingCopy.TryGetValue(taskId, out var internalState))
        {
            state = _stateCopier.Copy(internalState);
            return true;
        }

        state = null;
        return false;
    }

    public ValueTask ReadAsync()
    {
        _workingCopy = _storageCopier.Copy(_persistedCopy);
        return default;
    }

    public ValueTask WriteAsync()
    {
        _persistedCopy = _storageCopier.Copy(_workingCopy);
        return default;
    }
}

internal class DurableTaskGrainExtension : IDurableTaskGrainRuntime
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

    public ValueTask<Response> ScheduleOrPollAsync(IDurableTaskRequest request, IDurableTaskClient? client)
    {
        if (request.Context is not { } requestContext)
        {
            throw new InvalidOperationException($"No context for durable task request {request}");
        }

        if (TryGetExecutionContext(requestContext.TaskId, out var executionContext))
        {
            return SubscribeAsync(requestContext.TaskId, executionContext, client);
        }

        // Schedule the task
        return ScheduleNewTaskRequestAsync(request, client);
    }

    private ValueTask<Response> SubscribeAsync(TaskId taskId, DurableTaskExecutionContext executionContext, IDurableTaskClient? client)
    {
        // If the task is not yet completed, return.
        var responseTask = executionContext.AsValueTask();
        if (responseTask.IsCompleted)
        {
            return responseTask;
        }

        if (client is not null)
        {
            var existingClients = executionContext.State.Clients;
            if (existingClients is null || !existingClients.Contains(client))
            {
                return AddClientAsync(taskId, executionContext, client);
            }
        }

        return new ValueTask<Response>(PendingResponse.Instance);
    }

    private async ValueTask<Response> AddClientAsync(TaskId taskId, DurableTaskExecutionContext executionContext, IDurableTaskClient client)
    {
        var state = executionContext.State;

        // Add the client to the persisted task state.
        state.Clients ??= new();
        state.Clients.Add(client);
        _storage.AddOrUpdateTask(taskId, state);
        await _storage.WriteAsync();

        return PendingResponse.Instance;
    }

    private async ValueTask<Response> ScheduleNewTaskRequestAsync(IDurableTaskRequest request, IDurableTaskClient? client)
    {
        var taskId = request.Context!.TaskId;
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
        InvokeExistingAsync(taskId, request, executionContext);

        return PendingResponse.Instance;
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

    private void InvokeExistingAsync(TaskId taskId, IDurableTaskRequest request, DurableTaskExecutionContext context)
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

    public async ValueTask<ScheduledTask> OnScheduleAsync(DurableTaskRequest durableTaskRequest)
    {
        var context = durableTaskRequest.Context;
        Debug.Assert(context is not null);
        var taskId = context.TaskId;

        // Create a context locally, returning if it is already completed.
        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            executionContext = await CreateExecutionContextAsync(taskId);
        }

        var responseTask = executionContext.AsValueTask();

        // If the task has already completed, do not start it again.
        if (!responseTask.IsCompleted)
        {
            // Submit the request to the remote service.
            // Submit the request to the remote service.
            // Submit the request to the remote service.
            // Submit the request to the remote service.
            // Submit the request to the remote service.
            // Submit the request to the remote service.
            // Submit the request to the remote service.
            throw new NotImplementedException();
        }

        return new UntypedScheduledTask(executionContext);
    }

    public ValueTask<ScheduledTask<TResult>> OnScheduleAsync<TResult>(DurableTaskRequest<TResult> durableTaskRequest) => throw new NotImplementedException();

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

        if (client is not null)
        {
            // Subscribe the client and return
            return SubscribeAsync(taskId, executionContext, client);
        }

        return new(PendingResponse.Instance);
    }
}
