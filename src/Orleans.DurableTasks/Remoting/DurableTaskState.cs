using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks.Remoting;

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
