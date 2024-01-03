namespace Orleans.DurableTasks;

public class RetryOptions
{
    public double BackOffCoefficient { get; init; } = 2;
    public TimeSpan FirstRetryInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumRetryInterval { get; init; } = TimeSpan.FromMinutes(5);
    public int MaximumAttemptCount { get; init; }

    // NOTE: this is inherently not serializable. Would it therefore likely be better to specify retry using a named policy, rather than serializing the entire policy?
    // Question is, what implications would that have on xplat? 
    public Func<Exception, bool>? RetryFilter { get; init; }
}
