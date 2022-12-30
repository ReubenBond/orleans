using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks.Runtime;

public interface IDurableTaskCaller
{
    // Notify this instance that a task has completed
    Task OnCompleted(ScheduledTaskId taskId, Response response);
}

public interface IDurableTaskGrainExtension : IGrainExtension
{
    Task Ping(GrainId caller, ScheduledTaskId callback);

    /// <summary>
    /// Queries the status of the specified task or schedules a new task if the task is unknown.
    /// </summary> 
    Task QueryOrScheduleTask(GrainId caller, ScheduledTaskId taskId, IInvokable request);
}

internal interface IDurableTaskGrainRuntime
{
    // Impl: Checks task storage for workflowStep.TaskId (which is a fully-qualified task id)
    ValueTask<Response> GetOrScheduleWorkflowStep(WorkflowStep workflowStep);
}

internal class DurableTaskGrainExtension
{
}

public class WorkflowStep
{
    public ScheduledTaskId TaskId { get; }
    public ReadOnlySpan<char> TaskName => ReadOnlySpan<char>.Empty;
}

public class WorkflowStep<T> : WorkflowStep { }
