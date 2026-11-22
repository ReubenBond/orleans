using System.Diagnostics.CodeAnalysis;

using Polly;
using Polly.Retry;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Repeats one replica operation of a recovery until it answers other than <c>Backpressure</c>, fails in a way repetition
/// cannot change, or which the fatal-error handler reported (<see cref="StorageOutageRetry.IsRepeatable"/>), or the recovery ends (ledger-protocol.md §8.3).
/// </summary>
/// <remarks>
/// A recovery decides from replicas' answers, so a replica which is down, partitioned, slow, or saturated delays it but
/// never fails it: the replica's seal, read, or recovery write is sent again with bounded, jittered, exponential backoff
/// after a transient failure or a <c>Backpressure</c> answer, which asserts nothing (outcomes.md §1), and once
/// <c>max(Q, N − Q + 1)</c> replicas answer, the recovery ends (ledger-protocol.md §6.5). Every repeated operation is
/// idempotent: a seal, a read, and a recovery write of the address's one value (LR-I1, LR-I5, LR-I8).
/// </remarks>
internal static class ReplicaRetry
{
    /// <summary>
    /// Gets the delay before the first repetition.
    /// </summary>
    internal static TimeSpan BaseDelay { get; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Gets the longest delay between repetitions, which bounds how long recovery takes to notice a replica's return.
    /// </summary>
    internal static TimeSpan MaximumDelay { get; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Creates the pipeline of one operation, whose answer <paramref name="isBackpressure"/> recognizes as
    /// <c>Backpressure</c>, timing its delays with <paramref name="timeProvider"/>.
    /// </summary>
    internal static ResiliencePipeline<T> Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(TimeProvider timeProvider, Func<T, bool> isBackpressure) =>
        new ResiliencePipelineBuilder<T> { TimeProvider = timeProvider }
            .AddRetry(new RetryStrategyOptions<T>
            {
                ShouldHandle = arguments => ValueTask.FromResult(
                    arguments.Outcome.Exception is { } exception
                        ? StorageOutageRetry.IsRepeatable(exception, arguments.Context.CancellationToken)
                        : isBackpressure(arguments.Outcome.Result!)),
                MaxRetryAttempts = int.MaxValue,
                DelayGenerator = static arguments => ValueTask.FromResult<TimeSpan?>(Backoff.Delay(arguments.AttemptNumber, BaseDelay, MaximumDelay, Random.Shared.NextDouble())),
            })
            .Build();
}
