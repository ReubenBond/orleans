namespace Orleans.DurableTasks;

public abstract class RetryPolicy
{
    public abstract bool ShouldRetry(ExecutionAttemptSummary executionAttemptSummary);
}
