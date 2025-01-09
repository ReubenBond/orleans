using System.Diagnostics.CodeAnalysis;

namespace System.Distributed.DurableTasks.Scheduling;

public interface IRetryPolicyResolver
{
    bool TryResolveRetryPolicy(string? policyName, [NotNullWhen(true)] out RetryPolicy? retryPolicy);
}

public interface ICleanupPolicyResolver
{
    bool TryResolveCleanupPolicy(string? policyName, [NotNullWhen(true)] out CleanupPolicy? cleanupPolicy);
}

public class CleanupPolicy
{
    public TimeSpan CleanupAge { get; init; }
}
