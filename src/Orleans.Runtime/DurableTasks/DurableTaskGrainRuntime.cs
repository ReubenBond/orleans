#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    //private readonly AsyncLock _scheduleLock = new();

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
    private IDurableTaskGrainExtension? GetCallerReferenceOrDefault(DurableTaskRequestContext requestContext)
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

        // Check if the task is already running.
        if (TryGetScheduledTaskHandle(taskId, out var handle))
        {
            // If it is and it's completed, return the result immediately.
            var response = await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, _deactivationCts.Token);
            if (response.IsCompleted)
            {
                return response;
            }

            // Subscribe the caller to the task if possible.
            if (_storage.TryGetTask(taskId, out var state) && TrySubscribeClient(taskId, state, GetCallerReferenceOrDefault(requestContext)))
            {
                await _storage.WriteAsync(_deactivationCts.Token);
                return DurableTaskResponse.Subscribed;
            }

            return DurableTaskResponse.Pending;
        }
        else
        {
            // Create the task state and register the caller if they are addressable.
            var state = _storage.GetOrCreateTask(taskId, request);

            // Subscribe the caller to the task if possible.
            var subscribed = TrySubscribeClient(taskId, state, GetCallerReferenceOrDefault(requestContext));

            // If the task was already scheduled, return a response immediately.
            if (state.Result is { } response && response.IsCompleted)
            {
                return response;
            }

            // Persist the task state before invoking the task.
            // Note that if we intercept all outgoing calls to other durable tasks, then we do not need to do this here.
            // Instead, we can defer it until either the task completes or an outgoing call is made, since we can guarantee
            // no visible side-effects.
            // If the user does the 'wrong' thing and calls a non-durable task from their code, then that could expose an externality.
            await _storage.WriteAsync(_deactivationCts.Token);

            // Schedule the task with the runtime.
            var executionContext = CreateExecutionContext(taskId, state);
            _runningRequests.Add(taskId, InvokeRequest(taskId, request, executionContext));

            // Otherwise, schedule and run the task.
            handle = new ScheduledTaskHandle(taskId, this) { IsRunning = true };
            _taskHandles.Add(taskId, handle);

            return subscribed ? DurableTaskResponse.Subscribed : DurableTaskResponse.Pending;
        }
    }

    public async ValueTask<IScheduledTaskHandle> ScheduleChildAsync(TaskId taskId, DurableTask durableTask, CancellationToken cancellationToken)
    {
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} evaluating task {TaskId}", GrainId, taskId);
        }

        // If the task is currently running, return the existing handle.
        if (TryGetScheduledTaskHandle(taskId, out var handle) && (handle is not ScheduledTaskHandle localHandle || localHandle.IsRunning))
        {
            return handle;
        }

        var storedTask = _storage.GetOrCreateTask(taskId, null);

        // If the task is schedulable, schedule it.
        if (durableTask is ISchedulableTask schedulableTask)
        {
            var schedulingResponse = await schedulableTask.ScheduleAsync(taskId, cancellationToken);
            if (schedulingResponse.IsCompleted)
            {
                _storage.SetResponse(taskId, storedTask, schedulingResponse);
                await _storage.WriteAsync(cancellationToken);

                return new CompletedScheduledTaskHandle(taskId, schedulingResponse);
            }
            
            // Schedule the task and store a handle to it in-memory.
            handle = schedulableTask.GetHandle(taskId);
            _taskHandles.Add(taskId, handle);
            await _storage.WriteAsync(cancellationToken);
            return handle;
        }

        // Otherwise, the task must be a local method invocation, so create an execution context for it and execute it.
        var state = _storage.GetOrCreateTask(taskId, null);
        var executionContext = CreateExecutionContext(taskId, state);
        handle =  new ScheduledTaskHandle(taskId, this) { IsRunning = true };
        _taskHandles.Add(taskId, handle);
        _runningRequests.Add(taskId, InvokeChildTask(taskId, durableTask, executionContext));
        return handle;
    }

    private async Task InvokeRequest(TaskId taskId, IDurableTaskRequest request, GrainDurableExecutionContext context)
    {
        try
        {
            request.SetTarget(_shared.GrainContextAccessor.GrainContext);
            var response = await request.InvokeImplementation(context);
            await SetResponseAsync(taskId, response, _deactivationCts.Token);
        }
        catch (Exception exception)
        {
            _shared.Logger.LogError(exception, "{Id} error invoking durable task request '{Request}'.", GrainId, request);
            await SetResponseAsync(taskId, DurableTaskResponse.FromException(exception), _deactivationCts.Token);
        }
        finally
        {
            _runningRequests.Remove(taskId);
        }
    }

    private async Task InvokeChildTask(TaskId taskId, DurableTask durableTask, GrainDurableExecutionContext context)
    {
        try
        {
            DurableTaskRuntimeHelper.SetCurrentContext(context);
            var response = await DurableTaskRuntimeHelper.RunAsync(durableTask, context);
            await SetResponseAsync(taskId, response, _deactivationCts.Token);
        }
        catch (Exception exception)
        {
            _shared.Logger.LogError(exception, "{Id} error invoking durable task '{DurableTask}'.", GrainId, durableTask);
            await SetResponseAsync(taskId, DurableTaskResponse.FromException(exception), _deactivationCts.Token);
        }
        finally
        {
            _runningRequests.Remove(taskId);
        }
    }

    private async Task SetResponseAsync(
        TaskId taskId,
        DurableTaskResponse response,
        CancellationToken cancellationToken)
    {
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} task {TaskId} completed with result '{Result}'.", GrainId, taskId, response);
        }

        // Only update the result if an existing result has not been set. If this were to overwrite an already-persisted result,
        // that could cause the result to appear to change after it has already been observed.
        // This condition guards against the case where a scheduling call fails after the response has already been received via an OnResponse callback,
        // which could occur due to a recovery retry or concurrency (multiple clients scheduling the same workflow).
        if (!_storage.TryGetTask(taskId, out var state))
        {
            throw new InvalidOperationException($"Cannot complete unknown task '{taskId}'.");
        }

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

        if (_taskHandles.TryGetValue(taskId, out var handle))
        {
            if (handle is ScheduledTaskHandle localHandle)
            {
                localHandle.TrySetResponse(response);
            }
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

        var handle = GetScheduledTaskHandle(taskId);
        var response = await handle.PollAsync(new PollingOptions { PollTimeout = options.PollTimeout }, _deactivationCts.Token);
        if (response.IsCompleted)
        {
            return response;
        }

        var client = options.Observer;
        if (client is not null && _storage.TryGetTask(taskId, out var state) && TrySubscribeClient(taskId, state, client))
        {
            await _storage.WriteAsync(_deactivationCts.Token);
            return DurableTaskResponse.Subscribed;
        }

        return DurableTaskResponse.Pending;
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
        List<IScheduledTaskHandle> canceledHandles = [];
        if (!RequestCancellationCore(taskId, taskState, canceledContexts, canceledHandles))
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
        foreach (var handle in canceledHandles)
        {
            tasks.Add(handle.CancelAsync(cancellationToken).AsTask());
        }

        await Task.WhenAll(tasks);

        bool RequestCancellationCore(TaskId taskId, IDurableTaskState taskState, List<GrainDurableExecutionContext> canceledContexts, List<IScheduledTaskHandle> canceledHandles)
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
                _ = RequestCancellationCore(childTaskId, childTaskState, canceledContexts, canceledHandles);
            }

            _storage.RequestCancellation(taskId, taskState);
            if (TryGetExecutionContext(taskId, out var context))
            {
                canceledContexts.Add(context);
            }
            else if (TryGetScheduledTaskHandle(taskId, out var handle))
            {
                canceledHandles.Add(handle);
            }

            return true;
        }
    }

    async ValueTask IDurableTaskServer.CancelAsync(TaskId taskId)
    {
        await SignalCancellationAsync(taskId, _deactivationCts.Token);
    }

    private bool TryGetScheduledTaskHandle(TaskId taskId, [NotNullWhen(true)] out IScheduledTaskHandle? handle)
    {
        if (_taskHandles.TryGetValue(taskId, out handle))
        {
            return true;
        }

        if (_storage.TryGetTask(taskId, out var taskState))
        {
            // Rehydrate the task handle.
            if (taskState.Result is { } response)
            {
                Debug.Assert(response.IsCompleted);
                handle = new CompletedScheduledTaskHandle(taskId, response);
                return true;
            }
            else
            {
                // Create a new handle for the task.
                handle = new ScheduledTaskHandle(taskId, this);
                _taskHandles.Add(taskId, handle);
                return true;
            }
        }

        return false;
    }

    public IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId)
    {
        if (!TryGetScheduledTaskHandle(taskId, out var handle))
        {
            throw new KeyNotFoundException($"A task with the identifier '{taskId}' was not found.");
        }

        return handle;
    }

    private class ScheduledTaskHandle(TaskId taskId, DurableTaskGrainRuntime runtime) : IScheduledTaskHandle
    {
        private readonly TaskCompletionSource<DurableTaskResponse> _responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DurableTaskResponse> ResponseTask => _responseTcs.Task;

        public TaskId TaskId { get; } = taskId;

        public bool IsRunning { get; set; }

        public async ValueTask CancelAsync(CancellationToken cancellationToken)
        {
            await runtime.SignalCancellationAsync(TaskId, cancellationToken);
        }

        public async ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
        {
            if (options.PollTimeout > TimeSpan.Zero)
            {
                await ((Task)ResponseTask).WaitAsync(options.PollTimeout, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            }

            if (ResponseTask.IsCompleted)
            {
                return await ResponseTask;
            }

            return DurableTaskResponse.Pending;
        }

        public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        {
            return await ResponseTask.WaitAsync(cancellationToken);
        }

        public bool TrySetResponse(DurableTaskResponse response) => _responseTcs.TrySetResult(response);
    }

    private sealed class CompletedScheduledTaskHandle(TaskId taskId, DurableTaskResponse response) : IScheduledTaskHandle
    {
        public TaskId TaskId => taskId;

        public DurableTaskResponse Response => response;

        public ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken) => new(response);

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) => new(response);
    }
}
