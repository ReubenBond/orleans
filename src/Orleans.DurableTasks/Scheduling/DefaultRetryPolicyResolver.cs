using System.Diagnostics.CodeAnalysis;

namespace Orleans.DurableTasks;

public class DefaultRetryPolicyResolver : IRetryPolicyResolver
{
    private readonly RetryPolicy _defaultRetryPolicy;

    public DefaultRetryPolicyResolver(DefaultRetryPolicy defaultRetryPolicy)
    {
        _defaultRetryPolicy = defaultRetryPolicy;
    }

    public bool TryResolveRetryPolicy(string? policyName, [NotNullWhen(true)] out RetryPolicy? retryPolicy)
    {
        if (policyName is null)
        {
            retryPolicy = _defaultRetryPolicy;
            return true;
        }

        retryPolicy = null;
        return false;
    }
}
