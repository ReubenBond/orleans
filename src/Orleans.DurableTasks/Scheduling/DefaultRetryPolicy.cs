using Microsoft.Extensions.Options;

namespace Orleans.DurableTasks;

public class DefaultRetryPolicy : RetryPolicy
{
    private readonly RetryOptions _options;

    public DefaultRetryPolicy(IOptions<RetryOptions> retryOptions)
    {
        _options = retryOptions.Value;
    }

    public override bool ShouldRetry(ExecutionAttemptSummary executionAttemptSummary)
    {
        if (executionAttemptSummary.AttemptCount > _options.MaximumAttemptCount)
        {
            return false;
        }

        return true;
    }
}
