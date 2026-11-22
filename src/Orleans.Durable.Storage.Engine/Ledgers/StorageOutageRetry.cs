using Orleans.Durable.Storage.Diagnostics;
using Polly;
using Polly.Retry;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Waits out storage outages by repeating a writer write until it answers other than <c>Backpressure</c>, fails in a way
/// repetition cannot change (<see cref="LedgerReplicaFailures.IsTransient"/>), or its caller cancels.
/// </summary>
/// <remarks>
/// LP-I12: storage outages delay an append and never end its ledger. Delays grow exponentially, with jitter, from
/// <see cref="BaseDelay"/> to <see cref="MaximumDelay"/>; attempts are unbounded, so only the operation's
/// cancellation token stops the waiting. The pipeline classifies failures as a recovery's replica operations do, and
/// repeats a <c>Backpressure</c> answer, which asserts nothing: the write may have left a batch on fewer than <c>Q</c>
/// replicas, as a failed write may, and the repeat at the same slot covers that (outcomes.md §1). So a writer and a
/// recovery agree on which answers a repetition waits out.
/// </remarks>
internal static class StorageOutageRetry
{
    /// <summary>
    /// Gets the delay before the first repetition.
    /// </summary>
    internal static TimeSpan BaseDelay { get; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets the longest delay between repetitions, which bounds how long a recovered store waits.
    /// </summary>
    internal static TimeSpan MaximumDelay { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Creates a pipeline which repeats a writer write while it answers <c>Backpressure</c> or its failure is repeatable
    /// (<see cref="IsRepeatable"/>); a cancellation of the token the pipeline runs with is the caller's, which ends it.
    /// </summary>
    /// <param name="timeProvider">Times the delays.</param>
    /// <returns>The pipeline.</returns>
    internal static ResiliencePipeline<LedgerWriteOutcome> Create(TimeProvider timeProvider) => Create(timeProvider, firstAttemptNumber: 0);

    /// <summary>
    /// Creates a pipeline which repeats a writer write while it answers <c>Backpressure</c> or its failure is repeatable
    /// (<see cref="IsRepeatable"/>); a cancellation of the token the pipeline runs with is the caller's, which ends it.
    /// </summary>
    /// <param name="timeProvider">Times the delays.</param>
    /// <param name="firstAttemptNumber">
    /// The repetition number of the pipeline's first retry delay, from 0; a caller that already ran attempt 0 directly
    /// and waited its delay before entering the pipeline passes <c>1</c> so subsequent backoff steps stay aligned.
    /// </param>
    /// <returns>The pipeline.</returns>
    internal static ResiliencePipeline<LedgerWriteOutcome> Create(TimeProvider timeProvider, int firstAttemptNumber)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegative(firstAttemptNumber);
        return new ResiliencePipelineBuilder<LedgerWriteOutcome> { TimeProvider = timeProvider }
            .AddRetry(new RetryStrategyOptions<LedgerWriteOutcome>
            {
                ShouldHandle = static arguments => ValueTask.FromResult(
                    arguments.Outcome.Exception is { } exception
                        ? IsRepeatable(exception, arguments.Context.CancellationToken)
                        : arguments.Outcome.Result is ReplicaWriteOutcome.Backpressure),
                MaxRetryAttempts = int.MaxValue,
                DelayGenerator = arguments => ValueTask.FromResult<TimeSpan?>(
                    Backoff.Delay(checked(arguments.AttemptNumber + firstAttemptNumber), BaseDelay, MaximumDelay, Random.Shared.NextDouble())),
            })
            .Build();
    }

    /// <summary>
    /// Returns whether a ledger's write or recovery repeats an operation which failed with <paramref name="exception"/>:
    /// when the failure is transient (<see cref="LedgerReplicaFailures.IsTransient"/>), unless the fatal-error handler
    /// reported it, which stopped the process, so a repetition would only hide the defect (conventions.md §14).
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="stop">The repeating caller's cancellation.</param>
    internal static bool IsRepeatable(Exception exception, CancellationToken stop) =>
        !FatalErrorHandler.IsReported(exception) && LedgerReplicaFailures.IsTransient(exception, stop);
}
