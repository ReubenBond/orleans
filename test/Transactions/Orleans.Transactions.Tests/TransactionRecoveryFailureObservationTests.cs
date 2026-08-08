using System.Diagnostics;
using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionRecoveryFailureObservationTests
{
    [Fact]
    public async Task FaultAfterInFlightSignalAndBeforeShutdownIsPremature()
    {
        var mutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedFailure = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = TransactionRecoveryFailureObservation.ObserveAsync(
            mutation.Task,
            (_, observedAt) => observedFailure.TrySetResult(observedAt));

        inFlight.TrySetResult();
        await inFlight.Task;
        mutation.TrySetException(new InvalidOperationException("Pre-shutdown transaction fault"));
        var observedAt = await observedFailure.Task;
        var shutdownRequestedAt = Stopwatch.GetTimestamp();
        await observation;

        Assert.True(TransactionRecoveryFailureObservation.IsPremature(observedAt, shutdownRequestedAt));
    }
}
