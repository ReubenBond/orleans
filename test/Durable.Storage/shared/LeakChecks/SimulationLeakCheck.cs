using System.Globalization;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Establishes at a simulation's teardown that no background work outlives its owners: once the owners are disposed,
/// the cluster runs the work due at the current instant, without advancing its clock, and then holds no runnable,
/// waiting, or blocked work, no armed timer, and no unfinished operation (docs/spec/verification.md §11.1).
/// </summary>
/// <remarks>
/// A scenario first drains its environment: it ends every pause and heals the network, so disposal finds every node
/// reachable. Work left for a later instant, such as a timer, a retry, or a message still in flight, fails the check
/// with Clockwork's pending-work snapshot.
/// </remarks>
internal static class SimulationLeakCheck
{
    // Far more steps than the work due at one instant; a loop which reschedules itself at the same instant exhausts it.
    private const int MaxIterations = 10_000;

    /// <summary>
    /// Runs the work due now in <paramref name="cluster"/> and fails unless nothing is left pending.
    /// </summary>
    internal static void AssertQuiescent(SimulationCluster cluster, CancellationToken cancellationToken)
    {
        if (FindLeftovers(cluster, cancellationToken) is { } leftovers)
        {
            Assert.Fail(leftovers);
        }
    }

    /// <summary>
    /// Runs the work due now in <paramref name="cluster"/> and describes what is left pending, or returns
    /// <see langword="null"/> when nothing is; <c>SeededSimulation</c> runs it as a teardown check.
    /// </summary>
    internal static string? FindLeftovers(SimulationCluster cluster, CancellationToken cancellationToken)
    {
        var result = cluster.RunUntilIdle(cancellationToken, maxTimeAdvance: TimeSpan.Zero, maxIterations: MaxIterations);
        var operations = cluster.Scheduler.PendingOperationCount;
        return result.Reason == SimulationExecutionReason.Idle && result.PendingWork.PendingCount == 0 && operations == 0
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Work outlived the simulation's owners; {operations} operations are unfinished.{Environment.NewLine}{result.ToDetailedString()}");
    }
}
