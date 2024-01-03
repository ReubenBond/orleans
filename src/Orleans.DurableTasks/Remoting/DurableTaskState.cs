using System.Diagnostics;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks.Remoting;

/*
 * Grain activates
 * Grain enumerates stored pending tasks and re-invokes any which are not completed.
 *   * Some tasks will not be directly invokable, since they represent local methods on a grain (not remote requests to the grain)
     * Those tasks do not need to be invoked.
 */

public interface IDurableTaskState
{
    /// <summary>
    /// Gets the result of the task, which will be <see langword="null"/> if the task has not yet completed.
    /// </summary>
    public Response? Result { get; }

    /// <summary>
    /// Gets or sets the set of clients which are interested in the result of this task.
    /// </summary>
    /// <remarks>
    /// This task cannot be retired until all clients have acknowledged the task's result.
    /// If the task has a parent task (determined using the task's hierarchical identifier), then the result will not be retired until that
    /// In the case of nested tasks (eg, defined by local methods), there will typically be no clients.
    /// In that case, the result will not be 
    /// </remarks>
    public IReadOnlySet<IDurableTaskClient>? Observers { get; }

    /// <summary>
    /// Gets or sets the invokable request.
    /// </summary>
    public IDurableTaskRequest? Request { get; }

    /// <summary>
    /// Gets or sets the time that the task completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; }

    /// <summary>
    /// Gets or sets the time that the task was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }
}

[GenerateSerializer, SuppressReferenceTracking]
[Alias("DurableTaskState")]
public class DurableTaskState : IDurableTaskState
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
    // TODO: Use GrainId or some other simply-serializable type instead of IDurableTaskClient, and potentially pair it with
    // IDurableTaskClientReferenceFactory or some such to create the IDurableTaskClient references from the stored value.
    [Id(1)]
    public HashSet<IDurableTaskClient>? Observers { get; set; }

    /// <summary>
    /// Gets or sets the invokable request.
    /// </summary>
    [Id(2)]
    public IDurableTaskRequest? Request { get; set; }

    /// <summary>
    /// Gets or sets the time that the task completed.
    /// </summary>
    [Id(3)]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the time that the task was created.
    /// </summary>
    [Id(4)]
    public DateTimeOffset CreatedAt { get; set; }

    Response? IDurableTaskState.Result => Result;
    IReadOnlySet<IDurableTaskClient>? IDurableTaskState.Observers => Observers;
    IDurableTaskRequest? IDurableTaskState.Request => Request;
    DateTimeOffset? IDurableTaskState.CompletedAt => CompletedAt;
    DateTimeOffset IDurableTaskState.CreatedAt => CreatedAt;
}

