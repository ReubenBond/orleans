namespace System.Distributed.DurableTasks.Scheduling;

public abstract class RetryPolicy
{
    public abstract bool ShouldRetry(ExecutionAttemptSummary executionAttemptSummary);
}
