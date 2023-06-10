using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Remoting;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(services =>
            {
                services.AddSingleton<JobScheduler>();
                services.AddSingleton<IJobStorage, VolatileJobStorage>();
                services.AddSerializer();
            }).UseConsoleLifetime().Build();
        await host.StartAsync();

        // NOTE: this example doesn't use real storage and doesn't implement recovery logic.
        // We can do that...

        var jobScheduler = host.Services.GetRequiredService<JobScheduler>();

        jobScheduler.AddHandler("stringJoin", args => new(string.Join(", ", args))); 

        // During program config. This could be ASP.NET route mapping
        jobScheduler.AddHandler("SayHello", async args =>
        {
            string result;
            if (args is { Length: > 1 })
            {
                result = await jobScheduler.GetOrCreateJob("stringJoin", args).AsStep("join");
            }
            else
            {
                result = args[0];
            }

            return $"hello, {result}";
        });

        // Later, or somewhere else:
        var job1 = await jobScheduler.GetOrCreateJob("SayHello", "Bob").ScheduleAsync("job-1");
        var job2 = await jobScheduler.GetOrCreateJob("SayHello", "Brian", "Mary", "Jehoshaphat").ScheduleAsync("job-2");

        // Some time later, maybe an app crash happens in between.
        var result1 = await job1;
        Console.WriteLine($"Result of {job1.TaskId}: {result1}");

        var result2 = await job2;
        Console.WriteLine($"Result of {job2.TaskId}: {result2}");
        await host.WaitForShutdownAsync();
    }
}

public class JobScheduler
{
    private readonly Dictionary<string, object> _handlers = new();
    private readonly Dictionary<TaskId, JobDurableTaskExecutionContext> _pendingTasks = new();
    private readonly Dictionary<TaskId, Task> _runningTasks = new();
    private readonly IJobStorage _storage;
    private readonly ILogger<JobScheduler> _logger;
    private readonly SemaphoreSlim _asyncLock = new(1);

    public JobScheduler(IJobStorage storage, ILogger<JobScheduler> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new execution context, registering it in the local collection of execution contexts.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="state">The task state.</param>
    /// <returns>The new execution context.</returns>
    private JobDurableTaskExecutionContext CreateExecutionContext(TaskId taskId, JobTaskState state) => _pendingTasks[taskId] = new JobDurableTaskExecutionContext(taskId, this, state);

    /// <summary>
    /// Gets the execution context corresponding to the provided task, if it exists, and returns it.
    /// </summary>
    /// <param name="taskId">The task to get an execution context from.</param>
    /// <param name="executionContext">The execution context.</param>
    /// <returns><see langword="true"/> if the execution context was found, <see langword="false"/> otherwise.</returns>
    private bool TryGetExecutionContext(TaskId taskId, [NotNullWhen(true)] out JobDurableTaskExecutionContext? executionContext)
    {
        // Is an active method already waiting for this?
        if (_pendingTasks.TryGetValue(taskId, out executionContext))
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
            _pendingTasks[taskId] = executionContext;
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

    internal async ValueTask<DurableTaskExecutionContext> ScheduleAsync(JobTask job, TaskId taskId, SchedulingOptions? options)
    {
        try
        {
            await _asyncLock.WaitAsync();

            if (TryGetExecutionContext(taskId, out var executionContext))
            {
                if (!string.Equals(executionContext.State.Type, job.Type, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Attempt to schedule two jobs with the same task id, {taskId}, but different job types. Scheduled job type: {executionContext.State.Type}. Requested job type: {job.Type}");
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
                // This might be a bit confusing: we are forwarding the invocation on to async method.
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


internal sealed class JobDurableTaskExecutionContext : DurableTaskExecutionContext
{
    private readonly JobScheduler _jobScheduler;
    public JobTaskState State { get; }

    public JobDurableTaskExecutionContext(TaskId taskId, JobScheduler jobScheduler, JobTaskState state) : base(taskId)
    {
        _jobScheduler = jobScheduler;
        State = state;
    }

    protected override ValueTask<DurableTaskExecutionContext> EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition) => _jobScheduler.EvaluateStepAsync(taskId, taskDefinition);
}

public interface IJobStorage
{
    IEnumerable<(TaskId Id, JobTaskState State)> Tasks { get; }
    void AddOrUpdateTask(TaskId taskId, JobTaskState state);
    bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out JobTaskState? state);

    // Removes a request and its state
    bool RemoveTask(TaskId taskId);
    
    ValueTask WriteAsync();
    ValueTask ReadAsync();
}

internal class VolatileJobStorage : IJobStorage
{
    private Dictionary<TaskId, JobTaskState> _workingCopy = new();
    private Dictionary<TaskId, JobTaskState> _persistedCopy = new();
    private readonly DeepCopier<Dictionary<TaskId, JobTaskState>> _storageCopier;
    private readonly DeepCopier<JobTaskState> _stateCopier;

    public IEnumerable<(TaskId Id, JobTaskState State)> Tasks => _workingCopy.Select(static pair => (pair.Key, pair.Value));

    public VolatileJobStorage(DeepCopier<Dictionary<TaskId, JobTaskState>> storageCopier, DeepCopier<JobTaskState> stateCopier)
    {
        _storageCopier = storageCopier;
        _stateCopier = stateCopier;
    }

    public void AddOrUpdateTask(TaskId taskId, JobTaskState state) => _workingCopy[taskId] = _stateCopier.Copy(state);
    public bool RemoveTask(TaskId taskId) => _workingCopy.Remove(taskId);
    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out JobTaskState? state)
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

[GenerateSerializer]
public class JobTaskState
{
    /// <summary>
    /// Gets or sets the result of this task.
    /// </summary>
    [Id(0)]
    public Response? Result { get; set; }

    /// <summary>
    /// Gets or sets the invokable request.
    /// </summary>
    [Id(1)]
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the invokable request.
    /// </summary>
    [Id(2)]
    public string[]? Arguments { get; set; }

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
