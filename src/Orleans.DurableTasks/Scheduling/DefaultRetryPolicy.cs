using Microsoft.Extensions.Options;

namespace Orleans.DurableTasks.Scheduling;

public class DefaultRetryPolicy(IOptions<RetryOptions> retryOptions) : RetryPolicy
{
    private readonly RetryOptions _options = retryOptions.Value;

    public override bool ShouldRetry(ExecutionAttemptSummary executionAttemptSummary)
    {
        if (executionAttemptSummary.AttemptCount > _options.MaximumAttemptCount && _options.MaximumAttemptCount > 0)
        {
            return false;
        }

        return true;
    }
}
