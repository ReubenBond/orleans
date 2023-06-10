using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks.Remoting;

public interface IDurableTaskClient
{
    // Called when a remotely scheduled request completes
    [AlwaysInterleave]
    ValueTask OnResponse(TaskId taskId, Response response);
}

public interface IDurableTaskServer 
{
    // Called by DurableTaskRequest.Invoke to ensure that a task is scheduled
    ValueTask<Response> ScheduleAsync(IDurableTaskRequest request);

    // API used by ScheduledTask/<T> to check for a result for a task.
    // The ScheduledTask does not have access to the original request, so it cannot submit a sensible IDurableTaskRequest.
    ValueTask<Response> SubscribeOrPollAsync(TaskId taskId, IDurableTaskClient? client);
}

public interface IDurableTaskGrainExtension : IGrainExtension, IDurableTaskServer, IDurableTaskClient
{
}

public interface IDurableTaskGrainRuntime
{
    ValueTask<DurableTaskExecutionContext> EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition);
}

internal class DurableTaskGrainExtensionShared
{
    public IGrainContextAccessor GrainContextAccessor { get; }
    public ISystemClock SystemClock { get; }
    public ILogger<DurableTaskGrainExtension> Logger { get; }
    public PlacementStrategyResolver PlacementStrategyResolver { get; }
    public CleanupPolicy DefaultCleanupPolicy { get; } = new CleanupPolicy { CleanupAge = TimeSpan.FromDays(1) };

    public DurableTaskGrainExtensionShared(IGrainContextAccessor grainContextAccessor,
        ISystemClock systemClock,
        PlacementStrategyResolver placementStrategyResolver,
        ILogger<DurableTaskGrainExtension> logger)
    {
        GrainContextAccessor = grainContextAccessor;
        SystemClock = systemClock;
        PlacementStrategyResolver = placementStrategyResolver;
        Logger = logger;
    }
}

internal class DurableTaskGrainExtension : IDurableTaskGrainRuntime, IDurableTaskGrainExtension
{
    private readonly Dictionary<TaskId, GrainDurableTaskExecutionContext> _pendingTasks = new();
    private readonly Dictionary<TaskId, Task> _runningTasks = new();
    private readonly DurableTaskGrainExtensionShared _shared;
    private readonly IDurableTaskGrainStorage _storage;

    public DurableTaskGrainExtension(
        IDurableTaskGrainStorage storage,
        DurableTaskGrainExtensionShared shared)
    {
        _shared = shared;
        _storage = storage;
    }

    private GrainId GrainId => _shared.GrainContextAccessor.GrainContext.GrainId;

    /// <summary>
    /// Creates a new execution context, registering it in the local collection of execution contexts.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="state">The task state.</param>
    /// <returns>The new execution context.</returns>
    private GrainDurableTaskExecutionContext CreateExecutionContext(TaskId taskId, DurableTaskState state) => _pendingTasks[taskId] = new GrainDurableTaskExecutionContext(taskId, this, state);

    /// <summary>
    /// Gets the execution context corresponding to the provided task, if it exists, and returns it.
    /// </summary>
    /// <param name="taskId">The task to get an execution context from.</param>
    /// <param name="executionContext">The execution context.</param>
    /// <returns><see langword="true"/> if the execution context was found, <see langword="false"/> otherwise.</returns>
    private bool TryGetExecutionContext(TaskId taskId, [NotNullWhen(true)] out GrainDurableTaskExecutionContext? executionContext)
    {
        // Is an active method already waiting for this?
        if (_pendingTasks.TryGetValue(taskId, out executionContext))
        {
            return true;
        }

        if (_storage.TryGetTask(taskId, out var state))
        {
            // Rehydrate the execution context from its persisted state.
            executionContext = new GrainDurableTaskExecutionContext(taskId, this, state);

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

    /// <summary>
    /// Gets a reference to the caller if the caller supports durable task notification callbacks.
    /// </summary>
    /// <param name="requestContext">The request context.</param>
    /// <returns>A reference to the caller if the caller supports notifications callbacks, otherwise <see langword="null"/>.</returns>
    private IDurableTaskGrainExtension? GetCallerClientReference(DurableTaskRequestContext requestContext)
    {
        var caller = requestContext.Caller;
        if (caller is null)
        {
            return null;
        }

        var type = caller.GetGrainId().Type;

        // TODO: Consider using (cleaner?) grain manifest lookup instead. Placement can configure manifest (eg, see StatelessWorkerPlacement)
        var placement = _shared.PlacementStrategyResolver.GetPlacementStrategy(type);
        if (placement.IsGrain)
        {
            return caller.Cast<IDurableTaskGrainExtension>();
        }

        return null;
    }

    /// <summary>
    /// Called upon completion of a task. The receiver must persist consume the response as the caller may clear task state after this method returns.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="response">The task result.</param>
    /// <returns>A <see cref="ValueTask"/> representing the work performed.</returns>
    async ValueTask IDurableTaskClient.OnResponse(TaskId taskId, Response response)
    {
        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            // No such task. This may be because this client has already received a response for this task and removed its entry for it.
            // TODO: Perhaps this should log at a lower level since it is likely not the symptom of a bug or exceptional condition.
            _shared.Logger.LogWarning("Received response for unknown task {TaskId}: {Response}", taskId, response);
            return;
        }

        // Persist the response before responding to the caller.
        // TODO: If this write (or just about any state write) fails, then we need to undo the update to the task state.
        // The most straightforward way to do that might be to take a copy before mutating it.
        executionContext.State.Result = response;
        executionContext.State.CompletedAt = _shared.SystemClock.GetUtcNow();
        _storage.AddOrUpdateTask(taskId, executionContext.State);
        await _storage.WriteAsync();

        // Propagate the response to the application.
        executionContext.SetResponse(response);
    }

    /// <summary>
    /// Durably schedules a request for invocation against this instance.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>A <see cref="Response"/> indicating the status of the request. A response of type <see cref="PendingResponse"/> indicates that the caller can call this method again to poll for completion.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    async ValueTask<Response> IDurableTaskServer.ScheduleAsync(IDurableTaskRequest request)
    {
        if (request.Context is not { } requestContext)
        {
            throw new InvalidOperationException($"No context for durable task request {request}");
        }

        var taskId = requestContext.TaskId;
        var client = GetCallerClientReference(requestContext);

        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            var clientId = client?.GetGrainId().ToString() ?? "[none]";
            _shared.Logger.LogTrace("{Id} received scheduling request for task {TaskId} from client {Client}", GrainId, taskId, clientId);
        }

        if (TryGetExecutionContext(taskId, out var executionContext))
        {
/*
            // Checking equivalence like this is fraught with danger. It might be better to compare serialized or stringified versions of the requests instead of
            // Using object.Equals(left, right) on the arguments
            // Alternatively/optionally, we could support configurable equality comparer implementations per argument type.
            var existingRequest = executionContext.State.Request;
            if (!AreRequestsEquivalent(existingRequest!, request))
            {
                var message = $"Attempt to schedule a duplicate task, non-equivalent tasks with id {taskId}.\nExisting: {existingRequest?.ToMethodCallString()}.\nIncoming: {request.ToMethodCallString()}";
                throw new InvalidOperationException(message);
            }
*/

            // This is not a new request, so either poll it or subscribe the client to receive a notification once it has completed.
            var responseTask = executionContext.AsValueTask();
            if (client is not null)
            {
                // The client will receive a callback with the response, rather than receiving an immediate response.
                await SubscribeClientAsync(taskId, executionContext, client);
            }
            else if (responseTask.IsCompleted)
            {
                // There is no addressable client, so poll the task and return the result if it has completed.
                // This is for cases where the client does not have a stable identity (for example, it is not a true grain).
                return await responseTask;
            }
        }
        else
        {
            // Schedule the new request.
            Debug.Assert(!_pendingTasks.ContainsKey(taskId));
            var newTaskState = new DurableTaskState
            {
                Request = request,
                CreatedAt = _shared.SystemClock.GetUtcNow()
            };

            if (client is not null)
            {
                newTaskState.Clients = new HashSet<IDurableTaskClient> { client };
            }

            _storage.AddOrUpdateTask(taskId, newTaskState);
            await _storage.WriteAsync();

            // Schedule the task with the runtime.
            executionContext = CreateExecutionContext(taskId, newTaskState);
            InvokeRequestMethod(taskId, request, executionContext);
        }

        // The result indicates whether the caller will receive a callback (subscribed) or whether they must poll for a result.
        return client switch
        {
            { } => SubscribedResponse.Instance,
            _ => PendingResponse.Instance
        };
    }

    private async ValueTask SubscribeClientAsync(TaskId taskId, GrainDurableTaskExecutionContext executionContext, IDurableTaskClient? client)
    {
        if (client is not null)
        {
            var existingClients = executionContext.State.Clients;
            if (existingClients is null || !existingClients.Contains(client))
            {
                var state = executionContext.State;

                if (state.Result is { } response)
                {
                    // The client has already completed, so notify the client immediately instead of performing a storage write.
                    // TODO: Would it be better/simpler to convert such calls into polling responses?
                    // This implementation means that the call to SubscribeAsync returns a 'SubscribedResponse' even though the client
                    // has already received the final response via 'OnResponse'.
                    await client.OnResponse(taskId, response);
                }
                else
                {
                    // Add the client to the persisted task state.
                    state.Clients ??= new();
                    state.Clients.Add(client);
                    _storage.AddOrUpdateTask(taskId, state);
                    await _storage.WriteAsync();
                }
            }
        }
    }

    public async ValueTask<DurableTaskExecutionContext> EvaluateStepAsync(TaskId taskId, DurableTask durableTask)
    {
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} evaluating task {TaskId}", GrainId, taskId);
        }

        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            executionContext = await CreateExecutionContextAsync(taskId);
        }

        var storedResponse = executionContext.AsValueTask();

        // If the task has already completed, there is no need to start it again.
        if (!storedResponse.IsCompleted)
        {
            try
            {
                // Invoke the method immediately.
                var immediateResponse = await durableTask.InvokeAsync(executionContext);

                if (immediateResponse is PendingResponse)
                {
                    if (_shared.Logger.IsEnabled(LogLevel.Trace))
                    {
                        _shared.Logger.LogTrace("{Id} polling task {TaskId}", GrainId, taskId);
                    }

                    // Ensure that the request is being polled in the background so that the response can be propagated to the caller.
                    _ = Task.Run(async () =>
                    {
                        var pollable = durableTask as IPollableTask;
                        while (true)
                        {
                            await Task.Delay(1_000);
                            try
                            {
                                Response response;
                                if (pollable is not null)
                                {
                                    // Poll the task, which is cheaper than sending the initial request again.
                                    response = await pollable.PollAsync();
                                }
                                else
                                {
                                    // Resubmit the request, relying on idempotency.
                                    response = await durableTask.InvokeAsync(executionContext);
                                }

                                if (response is not PendingResponse)
                                {
                                    await CompleteRequestWithResponse(taskId, response, executionContext);
                                    break;
                                }
                            }
                            catch (Exception exception)
                            {
                                _shared.Logger.LogError(exception, "{Id} error polling task {TaskId}", GrainId, taskId);
                            }
                        }
                    });
                }
                else if (immediateResponse is SubscribedResponse)
                {
                    // The response will be propagated to the caller asynchronously via a callback.
                    if (_shared.Logger.IsEnabled(LogLevel.Trace))
                    {
                        _shared.Logger.LogTrace("{Id} subscribed for completion notifications for task {TaskId}", GrainId, taskId);
                    }
                }
                else
                {
                    // If the response is still pending, do not propagate it to the execution context yet.
                    await CompleteRequestWithResponse(taskId, immediateResponse, executionContext);
                }
            }
            catch (Exception exception)
            {
                // TODO: apply an internal retry policy here. If only the implementation of the task failed, 
                await CompleteRequestWithResponse(taskId, Response.FromException(exception), executionContext);
            }
        }

        return executionContext;
    }

    private async Task<GrainDurableTaskExecutionContext> CreateExecutionContextAsync(TaskId taskId)
    {
        var newTaskState = new DurableTaskState
        {
            CreatedAt = _shared.SystemClock.GetUtcNow()
        };

        _storage.AddOrUpdateTask(taskId, newTaskState);
        await _storage.WriteAsync();

        return CreateExecutionContext(taskId, newTaskState);
    }

    private void InvokeRequestMethod(TaskId taskId, IDurableTaskRequest request, GrainDurableTaskExecutionContext context)
    {
        _runningTasks.Add(taskId, InvokeRequestMethodCore(taskId, request, context));
    }

    private async Task InvokeRequestMethodCore(TaskId taskId, IDurableTaskRequest request, GrainDurableTaskExecutionContext context)
    {
        await Task.Yield();

        try
        {
            request.SetTarget(_shared.GrainContextAccessor.GrainContext);
            var response = await request.InvokeImplementation(context);
            await CompleteRequestWithResponse(taskId, response, context);
        }
        catch (Exception exception)
        {
            _shared.Logger.LogError(exception, "{Id} error invoking durable task request {Request}", GrainId, request);
            await CompleteRequestWithResponse(taskId, Response.FromException(exception), context);
        }
    }

    private async Task CompleteRequestWithResponse(TaskId taskId, Response response, GrainDurableTaskExecutionContext executionContext)
    {
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} task {TaskId} completed with result {Result}", GrainId, taskId, response);
        }

        // Only update the result if an existing result has not been set. If this were to overwrite an already-persisted result,
        // that could cause the result to appear to change after it has already been observed.
        // This condition guards against the case where a scheduling call fails after the response has already been received via an OnResponse callback,
        // which could occur due to a recovery retry or concurrency (multiple clients scheduling the same workflow).
        var state = executionContext.State;
        if (state.Result is null)
        {
            Debug.Assert(state.Result is null);

            // Store the result.
            // Note that this and the next call to notify callers may result in two writes in quick succession.
            // That is ok: we want to ensure that every client always sees the same result for a task, so it is important to persist the task before notifying the first client.
            state.Result = response;
            state.CompletedAt = _shared.SystemClock.GetUtcNow();
            _storage.AddOrUpdateTask(taskId, state);
            await _storage.WriteAsync();
            executionContext.SetResponse(response);
        }

        await NotifyClientsAndCleanupTask(taskId, executionContext);
    }

    /// <summary>
    /// Notifies all subscribed clients that the task has completed and performs any necessary cleanup operations.
    /// </summary>
    /// <param name="taskId">The task which has completed.</param>
    /// <param name="executionContext">The task execution context, containing the result.</param>
    /// <returns>A <see cref="Task"/> representing the work performed.</returns>
    private async Task NotifyClientsAndCleanupTask(TaskId taskId, GrainDurableTaskExecutionContext executionContext)
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
                    if (_shared.Logger.IsEnabled(LogLevel.Trace))
                    {
                        _shared.Logger.LogTrace("{Id} notifying {ClientsCount} clients for completion of task {TaskId}", GrainId, clientCount, taskId);
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

                if (_shared.Logger.IsEnabled(LogLevel.Trace))
                {
                    _shared.Logger.LogTrace("{Id} notified {ClientsCount} clients for completion of task {TaskId}", GrainId, clientCount, taskId);
                }

                // Success, no more work to be done right now.
                break;
            }
            catch (Exception exception)
            {
                _shared.Logger.LogWarning(exception, "{Id} exception while notifying clients of completion for durable task {TaskId}", GrainId, taskId);
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
        var now = _shared.SystemClock.GetUtcNow();
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

            if (state.CompletedAt is not { } completedAt || now.Subtract(completedAt) < _shared.DefaultCleanupPolicy.CleanupAge)
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
                        if (_shared.Logger.IsEnabled(LogLevel.Trace))
                        {
                            _shared.Logger.LogTrace("{Id} pruning completed child task {TaskId}", GrainId, childTaskId);
                        }

                        _storage.RemoveTask(childTaskId);
                    }
                }

                // Prune the task.
                if (_shared.Logger.IsEnabled(LogLevel.Trace))
                {
                    _shared.Logger.LogTrace("{Id} pruning completed task {TaskId}", GrainId, taskId);
                }
                _storage.RemoveTask(taskId);
            }
        }

        return completedTaskIds is not null;
    }

    private bool AreRequestsEquivalent(IDurableTaskRequest left, IDurableTaskRequest right)
    {
        if (!string.Equals(left.GetInterfaceName(), right.GetInterfaceName(), StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(left.GetMethodName(), right.GetMethodName(), StringComparison.Ordinal))
        {
            return false;
        }

        if (left.GetArgumentCount() != right.GetArgumentCount())
        {
            return false;
        }

        for (var arg = 0; arg < left.GetArgumentCount(); arg++)
        {
            var leftValue = left.GetArgument(arg);
            var rightValue = right.GetArgument(arg);
            if (leftValue is null ^ rightValue is null)
            {
                return false;
            }

            if (!Equals(left, right))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public ValueTask<Response> SubscribeOrPollAsync(TaskId taskId, IDurableTaskClient? client)
    {
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} received polling request for task {TaskId}", GrainId, taskId);
        }

        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            return new(UnknownTaskResponse.Instance);
        }

        var response = executionContext.State.Result;
        if (response is not null)
        {
            return new(response);
        }

        if (client is null)
        {
            return new(PendingResponse.Instance);
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
            return SubscribedResponse.Instance;
        }
    }
}
