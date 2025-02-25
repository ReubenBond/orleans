#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Eventing.Reader;
using System.Distributed.DurableTasks;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.Extensions.Logging;
using Orleans.DurableTasks;
using Orleans.Runtime.Placement;

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
    private readonly Dictionary<TaskId, GrainDurableExecutionContext> _executionContexts = [];
    private readonly Dictionary<TaskId, Task> _runningRequests = [];
    private readonly Dictionary<TaskId, IScheduledTaskHandle> _taskHandles = [];
    private readonly DurableTaskGrainRuntimeShared _shared = shared;
    private readonly IDurableTaskGrainStorage _storage = storage;

    // TODO: Cancel during deactivation.
    // Then drain all tasks.
    private readonly CancellationTokenSource _deactivationCts = new();

    private GrainId GrainId => _shared.GrainContextAccessor.GrainContext.GrainId;

    /// <summary>
    /// Creates a new execution context, registering it in the local collection of execution contexts.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="state">The task state.</param>
    /// <returns>The new execution context.</returns>
    private GrainDurableExecutionContext CreateExecutionContext(TaskId taskId, IDurableTaskState state) => _executionContexts[taskId] = new(taskId, this, state);

    /// <summary>
    /// Gets the execution context corresponding to the provided task, if it exists, and returns it.
    /// </summary>
    /// <param name="taskId">The task to get an execution context from.</param>
    /// <param name="executionContext">The execution context.</param>
    /// <returns><see langword="true"/> if the execution context was found, <see langword="false"/> otherwise.</returns>
    private bool TryGetExecutionContext(TaskId taskId, [NotNullWhen(true)] out GrainDurableExecutionContext? executionContext) => _executionContexts.TryGetValue(taskId, out executionContext);

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

    /*
    /// <summary>
    /// Gets a task-internal response if it is available.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="response">The response.</param>
    /// <returns>A value indicating whether the response exists.</returns>
    /// <remarks>
    /// A child task is a task which executes as part of another task and whose result it not externally visible.
    /// </remarks>
    public bool GetResponseOrCreateChildTask(TaskId taskId, [NotNullWhen(true)] out DurableTaskResponse? response)
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
    public void SetChildTaskResponse(TaskId taskId, DurableTaskResponse response)
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
    */

    /// <summary>
    /// Called upon completion of a task. The receiver must persist consume the response as the caller may clear task state after this method returns.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="response">The task result.</param>
    /// <returns>A <see cref="ValueTask"/> representing the work performed.</returns>
    ValueTask IDurableTaskObserver.OnResponseAsync(TaskId taskId, DurableTaskResponse response)
    {
        /*
        /*
        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            // No such task. This may be because this client has already received a response for this task and removed its entry for it.
            // TODO: Perhaps this should log at a lower level since it is likely not the symptom of a bug or exceptional condition.
            _shared.Logger.LogWarning("Received response for unknown task '{TaskId}': '{Response}'", taskId, response);
            return;
        }
        if (!_storage.TryGetTask(taskId, out var state))
        {
            _shared.Logger.LogDebug("Received response for unknown task '{TaskId}': '{Response}'", taskId, response);
        }


        // Persist the response before responding to the caller.
        // TODO: If this write (or just about any state write) fails, then we need to undo the update to the task state.
        // The most straightforward way to do that might be to take a copy before mutating it.
        _storage.SetResponse(taskId, state, response);
        await _storage.WriteAsync(_deactivationCts.Token);

        // Propagate the response to the application.
        //DurableTaskRuntimeHelper.SetResult(executionContext, response);
        */
        throw new NotImplementedException("TODO");
    }

    /// <summary>
    /// Durably schedules a request for invocation against this instance.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>A <see cref="DurableTaskResponse"/> indicating the status of the request. A response of type <see cref="PendingDurableTaskResponse"/> indicates that the caller can call this method again to poll for completion.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    async ValueTask<DurableTaskResponse> IDurableTaskServer.ScheduleAsync(TaskId taskId, IDurableTaskRequest request)
    {
        if (request.Context is not { } requestContext)
        {
            throw new InvalidOperationException($"No context for durable task request {request}");
        }

        var client = GetCallerClientReference(requestContext);

                // The client will receive a callback with the response, rather than receiving an immediate response.
                await SubscribeClientAsync(taskId, executionContext, client, _deactivationCts.Token);
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            var clientId = client?.GetGrainId().ToString() ?? "[none]";
            _shared.Logger.LogTrace("{Id} received scheduling request for task {TaskId} from client {Client}", GrainId, taskId, clientId);
        }

        // Check if the task is already running.
        if (_taskHandles.TryGetValue(taskId, out var handle))
        {
            if (handle is LocalScheduledTaskHandle localHandle)
            {
                if (localHandle.ResponseTask.IsCompleted)
                {
                    return await localHandle.ResponseTask;
                }
            }

            // The task is already scheduled, so return the existing handle.
            return DurableTaskResponse.Pending;
        }

        // Otherwise, schedule and run the task.


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
            var responseTask = DurableTaskRuntimeHelper.WaitAsync(executionContext, _deactivationCts.Token);
            if (client is not null)
            {
                // The client will receive a callback with the response, rather than receiving an immediate response.
                await SubscribeClientAsync(taskId, executionContext, client, _deactivationCts.Token);
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
            Debug.Assert(!_executionContexts.ContainsKey(taskId));
            var newTaskState = _storage.GetOrCreateTask(taskId, request);

            if (client is not null)
            {
                _storage.AddObserver(taskId, newTaskState, client);
            }

            await _storage.WriteAsync(_deactivationCts.Token);

            // Schedule the task with the runtime.
            executionContext = CreateExecutionContext(taskId, newTaskState);
            _runningRequests.Add(taskId, InvokeRequest(taskId, request, executionContext));
        }

        // The result indicates whether the caller will receive a callback (subscribed) or whether they must poll for a result.
        return client switch
        {
            { } => SubscribedDurableTaskResponse.Instance,
            _ => PendingDurableTaskResponse.Instance
        };

        async Task InvokeRequest(TaskId taskId, IDurableTaskRequest request, GrainDurableExecutionContext context)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);

            try
            {
                request.SetTarget(_shared.GrainContextAccessor.GrainContext);
                var response = await request.InvokeImplementation(context);
                await CompleteRequestWithResponseAsync(taskId, response, context, _deactivationCts.Token);
            }
            catch (Exception exception)
            {
                _shared.Logger.LogError(exception, "{Id} error invoking durable task request {Request}", GrainId, request);
                await CompleteRequestWithResponseAsync(taskId, DurableTaskResponse.FromException(exception), context, _deactivationCts.Token);
            }
            finally
            {
                _runningRequests.Remove(taskId);
            }
        }
    }

    private bool TrySubscribeClient(TaskId taskId, IDurableTaskState state, IDurableTaskObserver? client)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (client is not null && (state.Observers is not { } observers || !observers.Contains(client)))
        {
            // Add the client to the persisted task state but do not write state yet: that will be the responsibility of the caller.
            _storage.AddObserver(taskId, state, client);
            return true;
        }

        return false;
    }

    public async ValueTask<IScheduledTaskHandle> ScheduleAsync(TaskId taskId, DurableTask durableTask, CancellationToken cancellationToken)
    {
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} evaluating task {TaskId}", GrainId, taskId);
        }

        // If the task has already completed, return the result immediately.
        if (_storage.TryGetTask(taskId, out var taskState) && taskState.Result is { } storedResponse)
        {
            return new CompletedScheduledTaskHandle(taskId, storedResponse);
        }

        // If the task is currently running, return the existing handle.
        if (_taskHandles.TryGetValue(taskId, out var handle))
        {
            return handle;
        }

        // If the task is schedulable, schedule it.
        if (durableTask is ISchedulableTask schedulableTask)
        {
            var storedTask = _storage.GetOrCreateTask(taskId, null);
            var schedulingResponse = await schedulableTask.ScheduleAsync(taskId, cancellationToken);
            if (schedulingResponse.IsCompleted)
            {
                _storage.SetResponse(taskId, storedTask, schedulingResponse);
                await _storage.WriteAsync(cancellationToken);

                return new CompletedScheduledTaskHandle(taskId, schedulingResponse);
            }
            
            // Schedule the task and store a handle to it in-memory.
            var taskHandle = schedulableTask.GetHandle(taskId);
            _taskHandles[taskId] = taskHandle;
            await _storage.WriteAsync(cancellationToken);
            return taskHandle;
        }

        // Otherwise, the task must be a local method invocation, so create an execution context for it and execute it.

        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            executionContext = await CreateExecutionContextAsync(taskId, durableTask, cancellationToken);
        }

        var storedResponse = DurableTaskRuntimeHelper.Poll(executionContext);

        // If the task has already completed, there is no need to start it again.
        if (!storedResponse.IsCompleted)
        {
            try
            {
                // Invoke the method immediately.
                var immediateResponse = await DurableTaskRuntimeHelper.RunAsync(durableTask, executionContext);

                if (immediateResponse is PendingDurableTaskResponse)
                {
                    if (_shared.Logger.IsEnabled(LogLevel.Trace))
                    {
                        _shared.Logger.LogTrace("{Id} polling task {TaskId}", GrainId, taskId);
                    }

                    // Ensure that the request is being polled in the background so that the response can be propagated to the caller.
                    _ = Task.Run(async () =>
                    {
                        var handle = (durableTask as ISchedulableTask)?.GetHandle(taskId);
                        var pollingOptions = new PollingOptions { PollTimeout = TimeSpan.FromSeconds(10) };
                        while (true)
                        {
                            try
                            {
                                DurableTaskResponse response;
                                if (handle is not null)
                                {
                                    // Poll the task, which is cheaper than sending the initial request again.
                                    response = await handle.PollAsync(pollingOptions, cancellationToken);
                                }
                                else
                                {
                                    // Resubmit the request, relying on idempotency.
                                    response = await DurableTaskRuntimeHelper.RunAsync(durableTask, executionContext);
                                }

                                if (response is not PendingDurableTaskResponse)
                                {
                                    await CompleteRequestWithResponseAsync(taskId, response, executionContext, cancellationToken);
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
                else if (immediateResponse is SubscribedDurableTaskResponse)
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
                    await CompleteRequestWithResponseAsync(taskId, immediateResponse, executionContext, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                // TODO: apply an internal retry policy here. If only the implementation of the task failed, 
                await CompleteRequestWithResponseAsync(taskId, DurableTaskResponse.FromException(exception), executionContext, cancellationToken);
            }
        }

        return DurableTaskRuntimeHelper.Poll(executionContext);
    }

    private async Task<GrainDurableExecutionContext> CreateExecutionContextAsync(TaskId taskId, DurableTask task, CancellationToken cancellationToken)
    {
        var newTaskState = _storage.GetOrCreateTask(taskId, null);
        await _storage.WriteAsync(cancellationToken);

        var executionContext = CreateExecutionContext(taskId, newTaskState);
        executionContext.Task = task;
        return executionContext;
    }

    private async Task CompleteRequestWithResponseAsync(
        TaskId taskId,
        DurableTaskResponse response,
        GrainDurableExecutionContext executionContext,
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

// TODO: 'Structured concurrency':
// - Cancel all dangling child tasks now.
// - Wait for all child tasks to complete before propagating the result to the caller.
// - Emit diagnostic logs indicating that the task is waiting for its children to complete.
// Only signal clients once the task itself has transitioned.

// NOTE: For Structured Concurrency to work here, we need to track all child tasks.
// That likely requires having a hook so that we can write state before any ScheduleAsync call is issued by one of our tasks.
// So ScheduleAsync needs to call back into the parent context to launch (which it might already do...), giving us a way to write state before calling.

            DurableTaskRuntimeHelper.SetResult(executionContext, response);
        }

        await NotifyClientsAndCleanupTask(taskId, state, cancellationToken);
    }

    private async Task CompleteRequestWithResponseAsync(
        TaskId taskId,
        DurableTaskResponse response,
        IDurableTaskState state,
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
        if (state.Result is null)
        {
            Debug.Assert(state.Result is null);

            // Store the result.
            // Note that this and the next call to notify callers may result in two writes in quick succession.
            // That is ok: we want to ensure that every client always sees the same result for a task, so it is important to persist the task before notifying the first client.
            _storage.SetResponse(taskId, state, response);
            await _storage.WriteAsync(cancellationToken);


// TODO: 'Structured concurrency':
// - Cancel all dangling child tasks now.
// - Wait for all child tasks to complete before propagating the result to the caller.
// - Emit diagnostic logs indicating that the task is waiting for its children to complete.
// Only signal clients once the task itself has transitioned.

// NOTE: For Structured Concurrency to work here, we need to track all child tasks.
// That likely requires having a hook so that we can write state before any ScheduleAsync call is issued by one of our tasks.
// So ScheduleAsync needs to call back into the parent context to launch (which it might already do...), giving us a way to write state before calling.
        }

        await NotifyClientsAndCleanupTask(taskId, state, cancellationToken);
    }

    /// <summary>
    /// Notifies all subscribed clients that the task has completed and performs any necessary cleanup operations.
    /// </summary>
    /// <param name="taskId">The task which has completed.</param>
    /// <param name="state">The task execution context, containing the result.</param>
    /// <returns>A <see cref="Task"/> representing the work performed.</returns>
    private async Task NotifyClientsAndCleanupTask(TaskId taskId, IDurableTaskState state, CancellationToken cancellationToken)
    {
        Debug.Assert(state.Result is not null);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var clientTasks = new List<Task>();
                var clientCount = 0;

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

                await Task.WhenAll(clientTasks).WaitAsync(cancellationToken);

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
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
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
                        _executionContexts.Remove(childTaskId);
                    }
                }

                // Prune the task.
                if (_shared.Logger.IsEnabled(LogLevel.Trace))
                {
                    _shared.Logger.LogTrace("{Id} pruning completed task {TaskId}", GrainId, taskId);
                }

                _storage.RemoveTask(taskId);
                _executionContexts.Remove(taskId);
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
    public async ValueTask<DurableTaskResponse> SubscribeOrPollAsync(TaskId taskId, SubscribeOrPollOptions options)
    {
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} received polling request for task {TaskId}", GrainId, taskId);
        }

        if (!TryGetExecutionContext(taskId, out var executionContext))
        {
            return DurableTaskResponse.FromException(new KeyNotFoundException($"A task with the identifier '{taskId}' was not found."));
        }

        if (options.PollTimeout > TimeSpan.Zero)
        {
            // Wait for the task to complete for up to the specified time.
            var task = DurableTaskRuntimeHelper.GetCompletionTask(executionContext);
            await ((Task)task)
                .WaitAsync(options.PollTimeout)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            if (task.IsCompletedSuccessfully)
            {
                return await task;
            }
        }

        var response = executionContext.State.Result;
        if (response is not null)
        {
            return response;
        }

        var client = options.Observer;
        if (client is null)
        {
            return DurableTaskResponse.Pending;
        }

        await SubscribeClientAsync(taskId, executionContext, client, _deactivationCts.Token);
        return DurableTaskResponse.Subscribed;
    }

    async IAsyncEnumerable<(TaskId TaskId, DurableTaskDiagnosticState State)> IDurableTaskGrainExtension.GetTasksAsync()
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

    async IAsyncEnumerable<TaskId> IDurableTaskGrainExtension.GetRunningTasksAsync()
    {
        await Task.CompletedTask;
        foreach (var task in _runningRequests.ToList())
        {
            yield return task.Key;
        }
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

        List<GrainDurableExecutionContext> canceledContexts = [];
        if (!RequestCancellationCore(taskId, taskState, canceledContexts))
        {
            // No need to write state.
            return;
        }

        // Write state.
        await _storage.WriteAsync(cancellationToken);

        // If any task is waiting on this, notify them now.
        var tasks = new List<Task>(canceledContexts.Count);
        foreach (var context in canceledContexts)
        {
            tasks.Add(DurableTaskRuntimeHelper.CancelAsync(context, cancellationToken));
        }

        await Task.WhenAll(tasks);

        bool RequestCancellationCore(TaskId taskId, IDurableTaskState taskState, List<GrainDurableExecutionContext> canceledContexts)
        {
            if (taskState.CompletedAt.HasValue)
            {
                // If the task has completed then all child tasks have completed.
                return false;
            }

            if (taskState.CancellationRequestedAt.HasValue)
            {
                // Cancellation has already been requested.
                return false;
            }

            // Find all immediate children of the task and start canceling them.
            // TODO: It may be more efficient to get all descendants and to enumerate them in descendant-first order.
            foreach (var (childTaskId, childTaskState) in _storage.GetChildren(taskId))
            {
                Debug.Assert(taskId.IsParentOf(childTaskId));
                _ = RequestCancellationCore(childTaskId, childTaskState, canceledContexts);
            }

            _storage.RequestCancellation(taskId, taskState);
            if (TryGetExecutionContext(taskId, out var context))
            {
                canceledContexts.Add(context);
            }

            return true;
        }
    }

    async ValueTask IDurableTaskServer.CancelAsync(TaskId taskId)
    {
        await SignalCancellationAsync(taskId, _deactivationCts.Token);
    }

    public IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId)
    {
        if (!_taskHandles.TryGetValue(taskId, out var handle))
        {
            throw new KeyNotFoundException($"A task with the identifier '{taskId}' was not found.");
        }

        return handle;
    }

    private class LocalScheduledTaskHandle(TaskId taskId, DurableTaskGrainRuntime runtime) : IScheduledTaskHandle
    {
        private readonly TaskCompletionSource<DurableTaskResponse> _responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DurableTaskResponse> ResponseTask => _responseTcs.Task;

        public TaskId TaskId { get; } = taskId;

        public async ValueTask CancelAsync(CancellationToken cancellationToken)
        {
            await runtime.SignalCancellationAsync(TaskId, cancellationToken);
        }

        public async ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
        {
            if (options.PollTimeout > TimeSpan.Zero)
            {
                await ((Task)_responseTcs.Task).WaitAsync(options.PollTimeout, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            }

            if (_responseTcs.Task.IsCompleted)
            {
                return await _responseTcs.Task;
            }

            return DurableTaskResponse.Pending;
        }

        public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        {
            return await _responseTcs.Task.WaitAsync(cancellationToken);
        }

        public bool TrySetResponse(DurableTaskResponse response) => _responseTcs.TrySetResult(response);
    }

    private sealed class RemoteScheduledTaskHandle(TaskId taskId, IScheduledTaskHandle handle, DurableTaskGrainRuntime runtime) : IScheduledTaskHandle
    {
        private readonly TaskCompletionSource<DurableTaskResponse> _responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskId TaskId { get; } = taskId;

        public async ValueTask CancelAsync(CancellationToken cancellationToken)
        {
            await runtime.SignalCancellationAsync(TaskId, cancellationToken);
            await handle.CancelAsync(cancellationToken);
        }

        public async ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
        {
            var pollOptions = new SubscribeOrPollOptions { PollTimeout = options.PollTimeout };
            var response = await runtime.SubscribeOrPollAsync(TaskId, pollOptions);
            if (response.IsCompleted)
            {
                await runtime.CompleteRequestWithResponseAsync(TaskId, response, cancellationToken);
            }

            return response;
        }

        public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        {
            if (!runtime.TryGetExecutionContext(TaskId, out var context))
            {
                throw new KeyNotFoundException($"A task with the identifier '{TaskId}' was not found.");
            }

            var response = await DurableTaskRuntimeHelper.WaitAsync(context, cancellationToken);
            TrySetResponse(response);
            return response;
        }

        public bool TrySetResponse(DurableTaskResponse response) => _responseTcs.TrySetResult(response);
    }

    private sealed class CompletedScheduledTaskHandle(TaskId taskId, DurableTaskResponse response) : IScheduledTaskHandle
    {
        public TaskId TaskId { get; } = taskId;

        public ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken) => new(response);

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) => new(response);
    }
}
