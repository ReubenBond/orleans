using System.Diagnostics.CodeAnalysis;
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
}

public interface IDurableTaskGrainExtension : IGrainExtension, IDurableTaskServer, IDurableTaskClient
{
}

public interface IDurableTaskGrainRuntime : IDurableTaskGrainExtension
{
    // Similar to `ScheduleOrPollAsync`, except that:
    // It is intended for local `DurableTask` methods (steps) versus remotely issued requests
    // The DurableTaskRequest is not 
    // It blocks until the response has been completed.
    ValueTask<Response> InvokeAsync(IDurableTaskRequest request);
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
    /// Gets or sets the request context for this request.
    /// </summary>
    [Id(2)]
    public Dictionary<string, object>? RequestContext { get; set; }
}

internal interface IDurableTaskGrainStorage
{
    bool TryGetRequest(TaskId taskId, [NotNullWhen(true)] IDurableTaskRequest? request);

    void AddOrUpdateState(TaskId taskId, DurableTaskState state);
    bool TryGetState(TaskId taskId, [NotNullWhen(true)] out DurableTaskState? state);
    bool Remove(TaskId taskId);
    
    ValueTask WriteAsync();
    ValueTask ReadAsync();
}

internal class DurableTaskGrainStorage : IDurableTaskGrainStorage
{
    private Dictionary<TaskId, DurableTaskState> _workingCopy = new();
    private Dictionary<TaskId, DurableTaskState> _persistedCopy = new();
    private DeepCopier<Dictionary<TaskId, DurableTaskState>> _copier;

    public DurableTaskGrainStorage(DeepCopier<Dictionary<TaskId, DurableTaskState>> copier)
    {
        _copier = copier;
    }

    public void AddOrUpdate(TaskId taskId, DurableTaskState state) => _workingCopy[taskId] = state;
    public bool Remove(TaskId taskId) => _workingCopy.Remove(taskId);
    public bool TryGet(TaskId taskId, [NotNullWhen(true)] out DurableTaskState? state) => _workingCopy.TryGetValue(taskId, out state);

    public ValueTask ReadAsync()
    {
        _workingCopy = _copier.Copy(_persistedCopy);
        return default;
    }

    public ValueTask WriteAsync()
    {
        _persistedCopy = _copier.Copy(_workingCopy);
        return default;
    }
}


internal class DurableTaskGrainExtension : IDurableTaskGrainExtension
{
    private readonly Dictionary<TaskId, DurableTaskExecutionContext> _activeTasks = new();
    private readonly ILogger<DurableTaskGrainExtension> _logger;
    private readonly IDurableTaskGrainStorage _storage;

    public DurableTaskGrainExtension(ILogger<DurableTaskGrainExtension> logger, IDurableTaskGrainStorage storage)
    {
        _logger = logger;
        _storage = storage;
    }

    public async ValueTask OnResponse(TaskId taskId, Response response)
    {
        // Is a method already waiting for this?
        if (!_activeTasks.TryGetValue(taskId, out var executionContext))
        {
            
            _logger.LogWarning("Received response for unknown task {TaskId}: {Response}", taskId, response);
            return;
        }

        _storage.

        // Persist the response before responding to the caller.
        await _storage.WriteAsync();

        // Propagate the response to the application.
        executionContext.SetResponse(response);
    }

    public async ValueTask<Response> ScheduleOrPollAsync(IDurableTaskRequest request)
    {
        

    }
}

internal static class DurableTaskExecutionContextExtensions
{
    public static void SetResponse(this DurableTaskExecutionContext context, Response response)
    {
        if (response.Exception is { } exception)
        {
            context.SetException(exception);
        }
        else
        {
            context.SetResult(response.Result);
        }
    }
}
