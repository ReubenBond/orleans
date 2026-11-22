namespace Orleans.Testing.Simulation;

/// <summary>
/// Describes why a <see cref="SimulationCluster"/> drive-loop execution
/// (<c>RunUntil</c>, <c>RunUntilIdle</c>, or <c>RunFor</c>) stopped.
/// </summary>
public enum SimulationExecutionReason
{
    /// <summary>The requested condition became true.</summary>
    ConditionMet,

    /// <summary>
    /// The simulation has no more work: every queue (cluster and node) is empty. There is nothing
    /// left that could ever make progress.
    /// </summary>
    Idle,

    /// <summary>
    /// The simulation could not make progress, but at least one item is still sitting in a queue.
    /// This happens when a scheduled item became ready (its due time has passed) on a node that is
    /// currently suspended, so it cannot be executed until the node resumes.
    /// </summary>
    IdleWithPendingWork,

    /// <summary>
    /// The next scheduled item is further in simulated time than <see cref="SimulationCluster.MaxSimulatedTimeAdvance"/>
    /// allows in one jump, and the execution is being treated as stuck.
    /// </summary>
    MaxSimulatedTimeAdvanceExceeded,

    /// <summary>
    /// The clock was advanced more than <see cref="SimulationCluster.MaxConsecutiveTimeAdvances"/>
    /// times in a row without executing any work in between, and the execution is being treated as stuck.
    /// </summary>
    MaxConsecutiveTimeAdvancesExceeded,

    /// <summary>The configured maximum number of loop iterations was reached before any other stopping condition.</summary>
    MaxIterationsReached,

    /// <summary>The cluster's <see cref="SimulationCluster.TeardownCancellationToken"/> was cancelled.</summary>
    TeardownCancellationRequested,
}
