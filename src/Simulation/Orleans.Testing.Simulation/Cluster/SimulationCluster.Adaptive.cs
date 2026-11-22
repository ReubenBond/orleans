namespace Orleans.Testing.Simulation;

/// <summary>
/// Adaptive execution-budget overloads for <see cref="SimulationCluster"/> that spare callers from
/// picking a size-specific <c>maxIterations</c> value.
/// </summary>
public sealed partial class SimulationCluster
{
    /// <summary>
    /// <para>
    /// Runs the simulation until <paramref name="condition"/> becomes true, automatically
    /// escalating the iteration budget across successive batches instead of requiring a single
    /// <c>maxIterations</c> value sized to the scenario. This is the adaptive counterpart to
    /// <see cref="RunUntil(Func{bool}, CancellationToken, int)"/>.
    /// </para>
    /// <para>
    /// <b>Progress heuristic:</b> each batch is run with <see cref="RunUntil(Func{bool}, CancellationToken, int)"/>.
    /// If a batch stops for any reason other than <see cref="SimulationExecutionReason.MaxIterationsReached"/>,
    /// execution stops immediately and that result is returned - the goal was met
    /// (<see cref="SimulationExecutionReason.ConditionMet"/>), or the simulation is genuinely stuck
    /// in a way a bigger iteration budget cannot fix (<see cref="SimulationExecutionReason.Idle"/>,
    /// <see cref="SimulationExecutionReason.IdleWithPendingWork"/>,
    /// <see cref="SimulationExecutionReason.MaxSimulatedTimeAdvanceExceeded"/>,
    /// <see cref="SimulationExecutionReason.MaxConsecutiveTimeAdvancesExceeded"/>, or
    /// <see cref="SimulationExecutionReason.TeardownCancellationRequested"/>). Escalation only
    /// happens after <see cref="SimulationExecutionReason.MaxIterationsReached"/>, because reaching
    /// that reason means every one of the batch's iterations executed a real step or a clock
    /// advance (<c>StepsExecuted + TimeAdvanceCount == Iterations</c> for that batch) - there is
    /// always more forward motion to give a bigger budget a chance to reach, and a scenario that is
    /// truly spinning without making progress is instead caught by the separate, always-enforced
    /// <see cref="MaxConsecutiveTimeAdvances"/> safety net. Its consecutive-advance count is carried
    /// across batch boundaries and surfaces as
    /// <see cref="SimulationExecutionReason.MaxConsecutiveTimeAdvancesExceeded"/> rather than
    /// <see cref="SimulationExecutionReason.MaxIterationsReached"/>.
    /// </para>
    /// <para>
    /// Escalation never removes the hard safety cap that explicit <c>maxIterations</c> limits
    /// already provide: the total number of iterations across every batch cannot exceed
    /// <paramref name="budget"/>'s <see cref="AdaptiveExecutionBudget.MaxTotalIterations"/>.
    /// If that ceiling is reached
    /// while still needing more iterations, the returned result's
    /// <see cref="SimulationExecutionResult.Reason"/> is
    /// <see cref="SimulationExecutionReason.MaxIterationsReached"/>, exactly as it would be for a
    /// plain <see cref="RunUntil(Func{bool}, CancellationToken, int)"/> call whose fixed budget ran out. The
    /// returned <see cref="SimulationExecutionResult.Limits"/> reports this overall adaptive ceiling,
    /// not the size of the final batch.
    /// </para>
    /// </summary>
    /// <param name="condition">The condition that ends the run when it becomes true.</param>
    /// <param name="budget">
    /// The adaptive budget controlling the initial batch size, escalation factor, and hard total
    /// iteration ceiling.
    /// </param>
    /// <param name="cancellationToken">A token that can cancel the run between simulation dispatches.</param>
    /// <returns>A detailed result describing the whole (possibly multi-batch) execution.</returns>
    public SimulationExecutionResult RunUntil(
        Func<bool> condition,
        AdaptiveExecutionBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(budget);
        using var control = Scheduler.EnterControlScope();
        using var _ = Guard.Enter();
        SimulationProgressReporter? progressReporter = SimulationProgressReporter.CreateFromEnvironment(
            RuntimeIdentity,
            CapturePendingWorkSummary,
            () => Scheduler.PendingOperationCount);
        return RunAdaptiveCore(
            budget,
            (batchMaxIterations, consecutiveTimeAdvances) => ExecuteDriveLoop(
                condition,
                MaxSimulatedTimeAdvance,
                batchMaxIterations,
                observeTeardownCancellation: false,
                initialConsecutiveTimeAdvances: consecutiveTimeAdvances,
                absoluteEndTime: null,
                cancellationToken: cancellationToken,
                progressReporter: progressReporter));
    }

    /// <summary>
    /// <para>
    /// Runs the simulation until it becomes idle, automatically escalating the iteration budget
    /// across successive batches instead of requiring a single <c>maxIterations</c> value sized to
    /// the scenario. This is the adaptive counterpart to
    /// <see cref="RunUntilIdle(CancellationToken, TimeSpan?, int)"/>.
    /// </para>
    /// <para>
    /// Follows the same progress heuristic and hard-cap behavior as
    /// <see cref="RunUntil(Func{bool}, AdaptiveExecutionBudget, CancellationToken)"/> - see its remarks for
    /// the precise rules governing when escalation happens and when it stops.
    /// </para>
    /// </summary>
    /// <param name="budget">
    /// The adaptive budget controlling the initial batch size, escalation factor, and hard total
    /// iteration ceiling.
    /// </param>
    /// <param name="cancellationToken">A token that can cancel the run between simulation dispatches.</param>
    /// <param name="maxTimeAdvance">The maximum simulated-time gap to jump in a single advance. Defaults to <see cref="MaxSimulatedTimeAdvance"/>.</param>
    /// <returns>A detailed result describing the whole (possibly multi-batch) execution.</returns>
#pragma warning disable CA1068 // Cancellation is required while the time-advance limit retains its established default.
    public SimulationExecutionResult RunUntilIdle(
        AdaptiveExecutionBudget budget,
        CancellationToken cancellationToken,
        TimeSpan? maxTimeAdvance = null)
    {
        ArgumentNullException.ThrowIfNull(budget);
        using var control = Scheduler.EnterControlScope();
        using var _ = Guard.Enter();
        SimulationProgressReporter? progressReporter = SimulationProgressReporter.CreateFromEnvironment(
            RuntimeIdentity,
            CapturePendingWorkSummary,
            () => Scheduler.PendingOperationCount);
        return RunAdaptiveCore(
            budget,
            (batchMaxIterations, consecutiveTimeAdvances) => ExecuteDriveLoop(
                condition: null,
                maxTimeAdvance ?? MaxSimulatedTimeAdvance,
                batchMaxIterations,
                observeTeardownCancellation: true,
                initialConsecutiveTimeAdvances: consecutiveTimeAdvances,
                absoluteEndTime: null,
                cancellationToken: cancellationToken,
                progressReporter: progressReporter));
    }
#pragma warning restore CA1068

    /// <summary>
    /// Shared escalation loop for the adaptive RunUntil and RunUntilIdle overloads.
    /// Runs <paramref name="runBatch"/> repeatedly with an escalating iteration budget, combining
    /// results, until a batch stops for a reason other than
    /// <see cref="SimulationExecutionReason.MaxIterationsReached"/> or the hard
    /// <see cref="AdaptiveExecutionBudget.MaxTotalIterations"/> cap is reached.
    /// </summary>
    private SimulationExecutionResult RunAdaptiveCore(AdaptiveExecutionBudget budget, Func<int, int, SimulationExecutionResult> runBatch)
    {
        var startTime = TimeProvider.GetUtcNow();
        var totalIterations = 0;
        var currentBatchSize = budget.InitialMaxIterations;
        SimulationExecutionResult? combined = null;

        while (true)
        {
            var remaining = budget.MaxTotalIterations - totalIterations;
            var batchMaxIterations = Math.Min(currentBatchSize, remaining);
            var batch = runBatch(batchMaxIterations, combined?.ConsecutiveTimeAdvanceCount ?? 0);

            combined = combined is null ? batch : CombineExecutionResults(startTime, combined, batch, forcedTimeAdvanceCount: 0);
            combined = WithOverallAdaptiveLimits(combined, budget.MaxTotalIterations);
            totalIterations += batch.Iterations;

            if (batch.Reason != SimulationExecutionReason.MaxIterationsReached || totalIterations >= budget.MaxTotalIterations)
            {
                return combined;
            }

            var nextBatchSize = currentBatchSize * budget.GrowthFactor;
            currentBatchSize = nextBatchSize >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(nextBatchSize);
        }
    }

    private static SimulationExecutionResult WithOverallAdaptiveLimits(SimulationExecutionResult result, int maxTotalIterations) =>
        new(
            result.Reason,
            result.StartTime,
            result.EndTime,
            result.Iterations,
            result.StepsExecuted,
            result.TimeAdvanceCount,
            result.ConsecutiveTimeAdvanceCount,
            result.PendingWork,
            new SimulationExecutionLimits(
                maxTotalIterations,
                result.Limits.MaxSimulatedTimeAdvance,
                result.Limits.MaxConsecutiveTimeAdvances),
            result.AttemptedTimeAdvance);
}
