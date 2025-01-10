using System;
using System.Collections.Generic;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

/*
 * Grain activates
 * Grain enumerates stored pending tasks and re-invokes any which are not completed.
 *   * Some tasks will not be directly invokable since they represent local methods on a grain (not remote requests to the grain)
     * Those tasks do not need to be invoked.
 */

public interface IDurableTaskState
{
    /// <summary>
    /// Gets the result of the task, which will be <see langword="null"/> if the task has not yet completed.
    /// </summary>
    public Response Result { get; }

    /// <summary>
    /// Gets or sets the set of clients which are interested in the result of this task.
    /// </summary>
    /// <remarks>
    /// This task cannot be retired until all clients have acknowledged the task's result.
    /// If the task has a parent task (determined using the task's hierarchical identifier), then the result will not be retired until that
    /// In the case of nested tasks (eg, defined by local methods), there will typically be no clients.
    /// In that case, the result will not be 
    /// </remarks>
    public IReadOnlySet<IDurableTaskObserver> Observers { get; }

    /// <summary>
    /// Gets or sets the invokable request.
    /// </summary>
    public IDurableTaskRequest Request { get; }

    /// <summary>
    /// Gets or sets the time that the task completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; }

    /// <summary>
    /// Gets or sets the time that the task was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }
}

