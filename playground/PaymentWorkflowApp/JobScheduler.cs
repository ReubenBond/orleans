using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Remoting;
using Orleans.Serialization.Invocation;

public class JobDescription
{
    public TaskId JobId { get; set; }
    public string? Status { get; set; }
    public string? Result { get; set; }
    public Exception? Exception { get; set; }

    public string? Type { get; set; }

    public string[]? Arguments { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public override string? ToString() => $"[Id: {JobId}, Status: {Status}, Type: {Type}, Arguments: {string.Join(", ", Arguments ?? Array.Empty<string>())}, CreatedAt: {CreatedAt}, CompletedAt: {CompletedAt}, Result: {Result}, Exception: {Exception?.GetType()}]";
}

public class JobScheduler : IHostedService
{
    private readonly Dictionary<string, object> _handlers = new();
    private readonly Dictionary<TaskId, JobDurableTaskExecutionContext> _tasks = new();
    private readonly Dictionary<TaskId, Task> _runningTasks = new();
    private readonly IJobStorage _storage;
    private readonly ILogger<JobScheduler> _logger;
    private readonly SemaphoreSlim _asyncLock = new(1);

    public JobScheduler(IJobStorage storage, ILogger<JobScheduler> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public ValueTask InitializeAsync()
    {
        return _storage.ReadAsync();
    }

    /// <summary>
    /// Creates a new execution context, registering it in the local collection of execution contexts.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="state">The task state.</param>
    /// <returns>The new execution context.</returns>
    private JobDurableTaskExecutionContext CreateExecutionContext(TaskId taskId, JobTaskState state) => _tasks[taskId] = new JobDurableTaskExecutionContext(taskId, this, state);

    /// <summary>
    /// Gets the execution context corresponding to the provided task, if it exists, and returns it.
    /// </summary>
    /// <param name="taskId">The task to get an execution context from.</param>
    /// <param name="executionContext">The execution context.</param>
    /// <returns><see langword="true"/> if the execution context was found, <see langword="false"/> otherwise.</returns>
    private bool TryGetExecutionContext(TaskId taskId, [NotNullWhen(true)] out JobDurableTaskExecutionContext? executionContext)
    {
        // Is an active method already waiting for this?
        if (_tasks.TryGetValue(taskId, out executionContext))
        {
            return true;
        }

        if (_storage.TryGetTask(taskId, out var state))
        {
            // Rehydrate the execution context from its persisted state.
            executionContext = new JobDurableTaskExecutionContext(taskId, this, state);

            // If the task has completed, set the result now.
            if (state.Result is { } response)
            {
                executionContext.SetResponse(response);
            }

            // Move the task into the list of active tasks.
            _tasks[taskId] = executionContext;
            return true;
        }

        return false;
    }

    private async Task<JobDurableTaskExecutionContext> CreateExecutionContextAsync(TaskId taskId, string? type, string[]? arguments)
    {
        var newTaskState = new JobTaskState
        {
            CreatedAt = DateTime.UtcNow,
            Type = type,
            Arguments = arguments,
        };

        _storage.AddOrUpdateTask(taskId, newTaskState);
        await _storage.WriteAsync();

        return CreateExecutionContext(taskId, newTaskState);
    }

    public void AddHandler(string jobType, Func<string[], DurableTask<string>> handler) => _handlers[jobType] = handler;
    public void AddHandler(string jobType, Func<string[], string> handler) => _handlers[jobType] = handler;

    public DurableTask<string> GetOrCreateJob(string jobType, params string[] args) => new JobTask(jobType, args, this);

    public async IAsyncEnumerable<JobDescription> GetJobsAsync(bool includeCompleted = true)
    {
        await Task.Yield();
        foreach (var (taskId, job) in _tasks)
        {
            var (result, error) = job.State.Result switch
            {
                { Exception: Exception exception } => (null, exception),
                { Result: object res } => (res, null),
                _ => (default(object), default(Exception)),
            };

            if ((result is not null || error is not null) && !includeCompleted)
            {
                continue;
            }

            var isRunning = _runningTasks.ContainsKey(taskId);
            var description = new JobDescription
            {
                JobId = taskId,
                Arguments = job.State.Arguments,
                Type = job.State.Type,
                Status = (result, error) switch { (null, null) when isRunning => "Running", (null, null) => "Pending", (not null, null) => "Completed", (null, not null) => "Faulted", _ => "Internal Error" },
                Exception = error,
                Result = result?.ToString(),
                CreatedAt = job.State.CreatedAt,
                CompletedAt = job.State.CompletedAt,
            };
            yield return description;
        }
    }

    public async ValueTask Cancel(TaskId jobId)
    {
        if (!_tasks.TryGetValue(jobId, out var jobContext))
        {
            return;
        }

        jobContext.
    }

    internal async ValueTask<DurableTaskExecutionContext> ScheduleAsync(JobTask job, TaskId taskId, SchedulingOptions? options)
    {
        try
        {
            await _asyncLock.WaitAsync();

            if (TryGetExecutionContext(taskId, out var executionContext))
            {
                if (!string.Equals(executionContext.State.Type, job.Type, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Attempt to schedule multiple jobs with the same task id, {taskId}, but different job types. Scheduled job type: {executionContext.State.Type}. Requested job type: {job.Type}");
                }

                // and similarly for the job arguments...

                return executionContext;
            }

            executionContext = await CreateExecutionContextAsync(taskId, job.Type, job.Arguments);

            // Start invoking the newly defined task.
            InvokeRequestMethod(job, taskId, executionContext);

            return executionContext;
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public async ValueTask<DurableTaskExecutionContext> EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition)
    {
        try
        {
            await _asyncLock.WaitAsync();

            if (!TryGetExecutionContext(taskId, out var executionContext))
            {
                executionContext = await CreateExecutionContextAsync(taskId, type: null, arguments: null);
            }

            try
            {
                var response = await DurableTaskInternal.InvokeAsync(taskDefinition, executionContext);
                await CompleteRequestWithResponse(taskId, response, executionContext);
            }
            catch (Exception exception)
            {
                await CompleteRequestWithResponse(taskId, Response.FromException(exception), executionContext);
            }

            return executionContext;
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    private void InvokeRequestMethod(JobTask job, TaskId taskId, JobDurableTaskExecutionContext executionContext)
    {
        _runningTasks.Add(taskId, InvokeRequestMethodCore(taskId, job, executionContext));
    }

    private async Task InvokeRequestMethodCore(TaskId taskId, JobTask job, JobDurableTaskExecutionContext executionContext)
    {
        await Task.Yield();

        try
        {
            var response = await DurableTaskInternal.InvokeAsync(job, executionContext);

            await CompleteRequestWithResponse(taskId, response, executionContext);
        }
        catch (Exception exception)
        {
            var arguments = job.Arguments;
            var argString = arguments switch { { Length: 1 } arg => arg[0], { Length: > 1 } => string.Join(", ", arguments), _ => "" };
            _logger.LogError(exception, "Error invoking durable task request {Type}({Arguments})", job.Type, argString);
            await CompleteRequestWithResponse(taskId, Response.FromException(exception), executionContext);
        }
    }

    private async Task CompleteRequestWithResponse(TaskId taskId, Response response, JobDurableTaskExecutionContext executionContext)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Task {TaskId} completed with result {Result}", taskId, response);
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
            state.CompletedAt = DateTime.UtcNow;
            _storage.AddOrUpdateTask(taskId, state);
            await _storage.WriteAsync();
            executionContext.SetResponse(response);
        }
    }

    async Task IHostedService.StartAsync(CancellationToken cancellationToken) => await InitializeAsync();
    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal class JobTask : DurableTask<string>, ISchedulableTask
    {
        private readonly JobScheduler _jobScheduler;
        public string[] Arguments { get; }
        public string Type { get; }

        public JobTask(string type, string[] args, JobScheduler jobScheduler)
        {
            Arguments = args;
            Type = type;
            _jobScheduler = jobScheduler;
        }

        public ValueTask<DurableTaskExecutionContext> ScheduleAsync(TaskId taskId, SchedulingOptions? options)
        {
            return _jobScheduler.ScheduleAsync(this, taskId, options);
        }

        protected override async ValueTask<Response> InvokeAsync(DurableTaskExecutionContext executionContext)
        {
            var handler = _jobScheduler._handlers[Type];
            Response response;
            if (handler is Func<string[], string> funcJob)
            {
                DurableTaskExecutionContext.SetCurrentContext(executionContext);
                response = Response.FromResult(funcJob(Arguments));

            }
            else if (handler is Func<string[], DurableTask<string>> durableTaskJob)
            {
                // This might be a bit confusing: we are forwarding the invocation on to the async method.
                response = await DurableTaskInternal.InvokeAsync(durableTaskJob(Arguments), executionContext);
            }
            else
            {
                // Add other types...
                throw new NotSupportedException($"Job handlers of type {handler.GetType()} are not supported.");
            }

            return response;
        }
    }
}
