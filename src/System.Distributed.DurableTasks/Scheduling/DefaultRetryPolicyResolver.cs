using System.Diagnostics.CodeAnalysis;

namespace System.Distributed.DurableTasks.Scheduling;

public class DefaultRetryPolicyResolver(DefaultRetryPolicy defaultRetryPolicy) : IRetryPolicyResolver
{
    private readonly RetryPolicy _defaultRetryPolicy = defaultRetryPolicy;

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
