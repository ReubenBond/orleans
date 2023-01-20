using System.Diagnostics.CodeAnalysis;

namespace Orleans.DurableTasks;

public interface IRetryPolicyResolver
{
    bool TryResolveRetryPolicy(string? policyName, [NotNullWhen(true)] out RetryPolicy? retryPolicy);
}
