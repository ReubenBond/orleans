#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using System.Distributed.DurableTasks.Scheduling;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DurableTasks;
using Orleans.Runtime.Placement;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime.DurableTasks;

internal sealed class DurableTaskGrainRuntimeShared(
    IGrainContextAccessor grainContextAccessor,
    TimeProvider timeProvider,
    PlacementStrategyResolver placementStrategyResolver,
    IGrainFactory grainFactory,
    ILogger<DurableTaskGrainRuntime> logger)
{
    public IGrainContextAccessor GrainContextAccessor { get; } = grainContextAccessor;
    public TimeProvider TimeProvider { get; } = timeProvider;
    public ILogger<DurableTaskGrainRuntime> Logger { get; } = logger;
    public PlacementStrategyResolver PlacementStrategyResolver { get; } = placementStrategyResolver;
    public IGrainFactory GrainFactory { get; } = grainFactory;
    public CleanupPolicy DefaultCleanupPolicy { get; } = new() { CleanupAge = TimeSpan.FromDays(1) };
}

internal sealed class DurableTaskGrainRuntime(
    IDurableTaskGrainStorage storage,
    DurableTaskGrainRuntimeShared shared) : IDurableTaskGrainRuntime, IDurableTaskGrainExtension
{
    private readonly Dictionary<TaskId, GrainDurableTaskContext> _pendingTasks = [];
    private readonly Dictionary<TaskId, Task> _runningTasks = [];
    private readonly DurableTaskGrainRuntimeShared _shared = shared;
    private readonly IDurableTaskGrainStorage _storage = storage;

    private GrainId GrainId => _shared.GrainContextAccessor.GrainContext.GrainId;

    /// <summary>
    /// Creates a new execution context, registering it in the local collection of execution contexts.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="state">The task state.</param>
    /// <returns>The new execution context.</returns>
    private GrainDurableTaskContext CreateExecutionContext(TaskId taskId, IDurableTaskState state) => _pendingTasks[taskId] = new(taskId, this, state);

    /// <summary>
    /// Gets the execution context corresponding to the provided task, if it exists, and returns it.
    /// </summary>
    /// <param name="taskId">The task to get an execution context from.</param>
    /// <param name="executionContext">The execution context.</param>
    /// <returns><see langword="true"/> if the execution context was found, <see langword="false"/> otherwise.</returns>
    private bool TryGetExecutionContext(TaskId taskId, [NotNullWhen(true)] out GrainDurableTaskContext? executionContext)
    {
        // Is an active method already waiting for this?
        if (_pendingTasks.TryGetValue(taskId, out executionContext))
        {
            return true;
        }

        if (_storage.TryGetTask(taskId, out var state))
        {
            // Rehydrate the execution context from its persisted state.
            executionContext = new(taskId, this, state);

            // If the task has completed, set the result now.
            if (state.Result is { } response)
            {
                DurableTaskRuntimeHelper.SetResult(executionContext, response);
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
        var caller = requestContext.CallerId;
        if (caller.IsDefault)
        {
            return null;
        }

        var type = caller.Type;

        // TODO: Consider using (cleaner?) grain manifest lookup instead. Placement can configure manifest (eg, see StatelessWorkerPlacement)
        var placement = _shared.PlacementStrategyResolver.GetPlacementStrategy(type);
        if (placement.IsGrain)
        {
            return _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(caller);
        }

        return null;
    }

    /// <summary>
    /// Gets a task-internal response if it is available.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="response">The response.</param>
    /// <returns>A value indicating whether the response exists.</returns>
    /// <remarks>
    /// A child task is a task which executes as part of another task and whose result it not externally visible.
    /// </remarks>
    public bool GetResponseOrCreateChildTask(TaskId taskId, [NotNullWhen(true)] out Response? response)
    {
        if (TryGetExecutionContext(taskId, out var context))
        {
            // The task exists, but it may not have a result.
            if (context.State.Result is { } completedResponse)
            {
                response = completedResponse;
                return true;
            }
        }
        else
        {
            // Create a new task.
            var newTaskState = _storage.GetOrCreateTask(taskId, null);
            context = CreateExecutionContext(taskId, newTaskState);
            _pendingTasks.Add(taskId, context);
        }

        response = default;
        return false;
    }

    /// <summary>
    /// Sets a task-internal response.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="response">The response.</param>
    /// <exception cref="InvalidOperationException">The response has already been set.</exception>
    /// <remarks>
    /// An child task is a task which executes as part of another task and whose result it not externally visible.
    /// </remarks>
    public void SetChildTaskResponse(TaskId taskId, Response response)
    {
        if (!TryGetExecutionContext(taskId, out var context))
        {
            throw new InvalidOperationException($"Cannot set response for unknown task {taskId}");
        }

        if (context.State.Result is not null)
        {
            throw new InvalidOperationException($"Cannot set response for completed task {taskId}");
        }

        _storage.SetResponse(taskId, context.State, response);
        DurableTaskRuntimeHelper.SetResult(context, response);
    }

    /// <summary>
    /// Called upon completion of a task. The receiver must persist consume the response as the caller may clear task state after this method returns.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="response">The task result.</param>
    /// <returns>A <see cref="ValueTask"/> representing the work performed.</returns>
    async ValueTask IDurableTaskObserver.OnResponseAsync(TaskId taskId, Response response)
    {
        if (!TryGetExecutionContext(taskId, out var context))
        {
            // No such task. This may be because this client has already received a response for this task and removed its entry for it.
            // TODO: Perhaps this should log at a lower level since it is likely not the symptom of a bug or exceptional condition.
            _shared.Logger.LogWarning("Received response for unknown task {TaskId}: {Response}", taskId, response);
            return;
        }

        // Persist the response before responding to the caller.
        // TODO: If this write (or just about any state write) fails, then we need to undo the update to the task state.
        // The most straightforward way to do that might be to take a copy before mutating it.
        _storage.SetResponse(taskId, context.State, response);
        await _storage.WriteAsync(CancellationToken.None);

        // Propagate the response to the application.
        DurableTaskRuntimeHelper.SetResult(context, response);
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
            var responseTask = DurableTaskRuntimeHelper.GetResponseAsync(executionContext);
            if (client is not null)
            {
                // The client will receive a callback with the response, rather than receiving an immediate response.
                await SubscribeClientAsync(taskId, executionContext, client, CancellationToken.None);
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
            var newTaskState = _storage.GetOrCreateTask(taskId, request);

            if (client is not null)
            {
                _storage.AddObserver(taskId, newTaskState, client);
            }

            await _storage.WriteAsync(CancellationToken.None);

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

    private async ValueTask SubscribeClientAsync(TaskId taskId, GrainDurableTaskContext executionContext, IDurableTaskObserver client, CancellationToken cancellationToken)
    {
        if (client is not null)
        {
            var existingClients = executionContext.State.Observers;
            if (existingClients is null || !existingClients.Contains(client))
            {
                var state = executionContext.State;

                if (state.Result is { } response)
                {
                    // The client has already completed, so notify the client immediately instead of performing a storage write.
                    // TODO: Would it be better/simpler to convert such calls into polling responses?
                    // This implementation means that the call to SubscribeAsync returns a 'SubscribedResponse' even though the client
                    // has already received the final response via 'OnResponse'.
                    await client.OnResponseAsync(taskId, response);
                }
                else
                {
                    // Add the client to the persisted task state.
                    _storage.AddObserver(taskId, state, client);
                    await _storage.WriteAsync(cancellationToken);
                }
            }
        }
    }

    public async ValueTask<DurableTaskContext> ScheduleAsync(TaskId taskId, DurableTask durableTask, CancellationToken cancellationToken)
    {
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} evaluating task {TaskId}", GrainId, taskId);
        }

        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            executionContext = await CreateExecutionContextAsync(taskId, cancellationToken);
        }

        var storedResponse = DurableTaskRuntimeHelper.GetResponseAsync(executionContext);

        // If the task has already completed, there is no need to start it again.
        if (!storedResponse.IsCompleted)
        {
            try
            {
                // Invoke the method immediately.
                var immediateResponse = await DurableTaskRuntimeHelper.RunAsync(durableTask, executionContext);

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
                            // TODO: make this configurable, possibly centralize polling, add a way to break out.
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
                                    response = await DurableTaskRuntimeHelper.RunAsync(durableTask, executionContext);
                                }

                                if (response is not PendingResponse)
                                {
                                    await CompleteRequestWithResponse(taskId, response, executionContext, cancellationToken);
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
                    await CompleteRequestWithResponse(taskId, immediateResponse, executionContext, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                // TODO: apply an internal retry policy here. If only the implementation of the task failed, 
                await CompleteRequestWithResponse(taskId, Response.FromException(exception), executionContext, cancellationToken);
            }
        }

        return executionContext;
    }

    private async Task<GrainDurableTaskContext> CreateExecutionContextAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        var newTaskState = _storage.GetOrCreateTask(taskId, null);
        await _storage.WriteAsync(cancellationToken);

        return CreateExecutionContext(taskId, newTaskState);
    }

    private void InvokeRequestMethod(TaskId taskId, IDurableTaskRequest request, GrainDurableTaskContext context)
    {
        _runningTasks.Add(taskId, InvokeRequestMethodCore(taskId, request, context));
    }

    private async Task InvokeRequestMethodCore(TaskId taskId, IDurableTaskRequest request, GrainDurableTaskContext context)
    {
        await Task.Yield();

        try
        {
            request.SetTarget(_shared.GrainContextAccessor.GrainContext);
            var response = await request.InvokeImplementation(context);
            await CompleteRequestWithResponse(taskId, response, context, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _shared.Logger.LogError(exception, "{Id} error invoking durable task request {Request}", GrainId, request);
            await CompleteRequestWithResponse(taskId, Response.FromException(exception), context, CancellationToken.None);
        }
    }

    private async Task CompleteRequestWithResponse(
        TaskId taskId,
        Response response,
       GrainDurableTaskContext executionContext,
       CancellationToken cancellationToken)
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
            _storage.SetResponse(taskId, state, response);
            await _storage.WriteAsync(cancellationToken);
            DurableTaskRuntimeHelper.SetResult(executionContext, response);
        }

        await NotifyClientsAndCleanupTask(taskId, executionContext, cancellationToken);
    }

    /// <summary>
    /// Notifies all subscribed clients that the task has completed and performs any necessary cleanup operations.
    /// </summary>
    /// <param name="taskId">The task which has completed.</param>
    /// <param name="executionContext">The task execution context, containing the result.</param>
    /// <returns>A <see cref="Task"/> representing the work performed.</returns>
    private async Task NotifyClientsAndCleanupTask(TaskId taskId, GrainDurableTaskContext executionContext, CancellationToken cancellationToken)
    {
        Debug.Assert(executionContext.State.Result is not null);
        while (true)
        {
            try
            {
                var clientTasks = new List<Task>();
                var clientCount = 0;

                var state = executionContext.State;
                if (state.Observers is { } clients)
                {
                    clientCount = clients.Count;
                    if (_shared.Logger.IsEnabled(LogLevel.Trace))
                    {
                        _shared.Logger.LogTrace("{Id} notifying {ClientsCount} clients for completion of task {TaskId}", GrainId, clientCount, taskId);
                    }

                    var response = state.Result;
                    foreach (var client in clients)
                    {
                        clientTasks.Add(client.OnResponseAsync(taskId, response).AsTask());
                    }
                }

                await Task.WhenAll(clientTasks);

                _storage.ClearObservers(taskId, state);

                PruneCompletedTasks();

                // NOTE: this write is not required for correctness, so it could be removed & performed lazily.
                await _storage.WriteAsync(cancellationToken);

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

            // TODO: Make this configurable and probably use exponential back-off, potentially with some coordination with other tasks.
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
        var now = _shared.TimeProvider.GetUtcNow();
        foreach (var (taskId, state) in allTasks)
        {
            if (state.Result is null)
            {
                // The task is incomplete.
                continue;
            }

            if (state.Observers is { Count: > 0 })
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
                waitingOnParent ??= [];
                ref var waiters = ref CollectionsMarshal.GetValueRefOrAddDefault(waitingOnParent, parent, out var exists);
                waiters ??= [];
                waiters.Add(taskId);
                continue;
            }

            completedTaskIds ??= [];
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
                        _pendingTasks.Remove(childTaskId);
                    }
                }

                // Prune the task.
                if (_shared.Logger.IsEnabled(LogLevel.Trace))
                {
                    _shared.Logger.LogTrace("{Id} pruning completed task {TaskId}", GrainId, taskId);
                }

                _storage.RemoveTask(taskId);
                _pendingTasks.Remove(taskId);
            }
        }

        return completedTaskIds is not null;
    }

    private static bool AreRequestsEquivalent(IDurableTaskRequest left, IDurableTaskRequest right)
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
    public ValueTask<Response> SubscribeOrPollAsync(TaskId taskId, IDurableTaskObserver client)
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

        var subscribeTask = SubscribeClientAsync(taskId, executionContext, client, CancellationToken.None);
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

    public async IAsyncEnumerable<(TaskId TaskId, DurableTaskDiagnosticState State)> GetTasksAsync()
    {
        await Task.CompletedTask;

        foreach (var (taskId, taskState) in _storage.Tasks)
        {
            var state = GetDiagnosticState(taskState);

            yield return (taskId, state);
        }

        static DurableTaskDiagnosticState GetDiagnosticState(IDurableTaskState taskState)
        {
            return new DurableTaskDiagnosticState
            {
                CompletedAt = taskState.CompletedAt,
                CreatedAt = taskState.CreatedAt,
                Response = taskState.Result?.ToString(),
                Request = taskState.Request?.ToMethodCallString(),
                Status = taskState.Result switch
                {
                    { } response when response.Exception is null => "Completed",
                    { } => "Faulted",
                    null => "Pending",
                },
                Waiters = taskState.Observers?.Select(static client => client.ToString()!).ToList() ?? [],
            };
        }
    }

    public async IAsyncEnumerable<TaskId> GetRunningTasksAsync()
    {
        await Task.CompletedTask;
        foreach (var task in _runningTasks.ToList())
        {
            yield return task.Key;
        }
    }

    private bool TrySignalCancellationCore(TaskId taskId, IDurableTaskState taskState)
    {
        if (taskState.CompletedAt.HasValue)
        {
            // If the task has completed then all child tasks have completed.
            return false;
        }   

        if (taskState.CancellationRequestedAt.HasValue)
        {
            return true;
        }   

        // Find all immediate children of the task and start canceling them.
        foreach (var (childTaskId, childTaskState) in _storage.Tasks)
        {
            if (!childTaskId.IsChildOf(taskId))
            {
                continue;
            }

            _ = TrySignalCancellationCore(childTaskId, childTaskState);
        }

        _storage.RequestCancellation(taskId, taskState);
        return true;
    }

    public async ValueTask SignalCancellationAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        if (taskId.IsDefault)
        {
            throw new ArgumentException("Invalid TaskId.", nameof(taskId));
        }

        if (!_storage.TryGetTask(taskId, out var taskState))
        {
            // The task may have been pruned or may never have existed.
            return;
        }

        if (!TrySignalCancellationCore(taskId, taskState))
        {
            return;
        }

        // Write state.
        await _storage.WriteAsync(cancellationToken);

        // Wait for the children to terminate.
        // Set the result to 'cancelled' (TaskCanceledException) if the task is not already completed.
        // Write state.
    }
}
