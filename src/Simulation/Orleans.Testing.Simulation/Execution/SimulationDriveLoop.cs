namespace Orleans.Testing.Simulation;

/// <summary>
/// Options for a single execution of <see cref="SimulationDriveLoop"/>.
/// Consolidates the parameters that were previously duplicated across
/// <c>RunUntil</c>, <c>RunUntilIdle</c>, and <c>RunFor</c> on
/// <see cref="SimulationCluster"/>.
/// </summary>
/// <param name="Condition">
/// The condition that ends the loop successfully, or <see langword="null"/> to run until idle
/// (i.e. there is no condition to satisfy - the loop only stops for idle/limit/cancellation reasons).
/// </param>
/// <param name="MaxSimulatedTimeAdvance">The maximum simulated-time gap to jump across in a single advance.</param>
/// <param name="MaxIterations">The maximum number of loop iterations to execute.</param>
/// <param name="MaxConsecutiveTimeAdvances">The maximum number of consecutive time advances without executed work.</param>
/// <param name="ObserveTeardownCancellation">Whether to check for cluster teardown each iteration.</param>
/// <param name="InitialConsecutiveTimeAdvances">The consecutive time-advance count carried into this execution.</param>
/// <param name="EndTime">
/// An optional absolute ceiling for simulated time. Work due at this instant is drained, but the
/// scheduler's virtual time never advances beyond it.
/// </param>
/// <param name="CancellationToken">The caller-controlled token checked before each loop iteration.</param>
/// <param name="Progress">An optional observer invoked after every completed drive-loop iteration.</param>
internal readonly record struct SimulationDriveLoopOptions(
    Func<bool>? Condition,
    TimeSpan MaxSimulatedTimeAdvance,
    int MaxIterations,
    int MaxConsecutiveTimeAdvances,
    bool ObserveTeardownCancellation,
    int InitialConsecutiveTimeAdvances,
    DateTimeOffset? EndTime,
    CancellationToken CancellationToken,
    Action<SimulationProgressSnapshot>? Progress = null);

/// <summary>
/// <para>
/// The single internal execution engine that drives a <see cref="SimulationCluster"/>
/// forward in time. It centralizes round-robin task execution, time advancement
/// to the next scheduled item, and all stuck/limit detection.
/// </para>
/// <para>
/// The engine is deliberately decoupled from <see cref="SimulationCluster"/> via
/// delegates so that its state machine can be reasoned about (and, if needed, tested)
/// independently of the cluster and node registry.
/// </para>
/// </summary>
/// <param name="getUtcNow">Returns the current simulated time.</param>
/// <param name="runOneTaskRoundRobin">Attempts to execute one ready task; returns true if one ran.</param>
/// <param name="getNextWaitingDueTime">Returns the earliest due time of any not-yet-ready item, or null.</param>
/// <param name="advanceVirtualTime">Advances scheduler-owned virtual time by the given, non-negative amount.</param>
/// <param name="capturePendingWorkSummary">Captures a snapshot of runnable/waiting/blocked work.</param>
/// <param name="hasPendingOperations">Returns whether the scheduler still owns non-terminal operations.</param>
/// <param name="isTeardownRequested">Returns whether the cluster is being torn down.</param>
internal sealed class SimulationDriveLoop(
    Func<DateTimeOffset> getUtcNow,
    Func<CancellationToken, bool> runOneTaskRoundRobin,
    Func<DateTimeOffset?> getNextWaitingDueTime,
    Action<TimeSpan> advanceVirtualTime,
    Func<SimulationPendingWorkSummary> capturePendingWorkSummary,
    Func<bool> hasPendingOperations,
    Func<bool> isTeardownRequested)
{
    /// <summary>
    /// Runs the drive loop to completion for a single logical operation (one of the RunUntil/RunUntilIdle
    /// family of calls), returning a structured result describing exactly why it stopped.
    /// </summary>
    /// <param name="options">The options controlling this execution.</param>
    /// <returns>A structured result describing the execution.</returns>
    public SimulationExecutionResult Execute(SimulationDriveLoopOptions options)
    {
        var startTime = getUtcNow();
        var consecutiveTimeAdvances = options.InitialConsecutiveTimeAdvances;
        var totalTimeAdvances = 0;
        var stepsExecuted = 0;
        options.CancellationToken.ThrowIfCancellationRequested();

        for (var iteration = 0; iteration < options.MaxIterations; iteration++)
        {
            if (options.ObserveTeardownCancellation && isTeardownRequested())
            {
                return Complete(SimulationExecutionReason.TeardownCancellationRequested, iteration);
            }

            if (options.Condition is { } condition && condition())
            {
                return Complete(SimulationExecutionReason.ConditionMet, iteration);
            }

            if (runOneTaskRoundRobin(options.CancellationToken))
            {
                stepsExecuted++;
                consecutiveTimeAdvances = 0; // Reset the stuck-detection counter when real work happens.
                ReportProgress(iteration + 1);
                continue;
            }

            // No tasks ready to execute right now - need to advance time or stop.
            var now = getUtcNow();
            if (options.EndTime is { } endTime && now >= endTime)
            {
                return CompleteIdle(iteration);
            }

            if (options.Condition is not null || options.EndTime is not null)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
            }

            var nextDueTime = getNextWaitingDueTime();
            if (nextDueTime is null)
            {
                if (options.EndTime is { } target)
                {
                    advanceVirtualTime(target - now);
                    totalTimeAdvances++;
                    return CompleteIdle(iteration);
                }

                // Nothing is waiting for a future time either. Distinguish "truly nothing left" from
                // "there is work, but it cannot run" (e.g. a ready item on a suspended node's queue).
                return CompleteIdle(iteration);
            }

            options.CancellationToken.ThrowIfCancellationRequested();

            if (options.EndTime is { } ceiling && nextDueTime.Value > ceiling)
            {
                advanceVirtualTime(ceiling - now);
                totalTimeAdvances++;
                return CompleteIdle(iteration);
            }

            var timeDelta = nextDueTime.Value - now;
            if (timeDelta > options.MaxSimulatedTimeAdvance)
            {
                return Complete(SimulationExecutionReason.MaxSimulatedTimeAdvanceExceeded, iteration, attemptedTimeAdvance: timeDelta);
            }

            if (timeDelta > TimeSpan.Zero)
            {
                advanceVirtualTime(timeDelta);
            }

            consecutiveTimeAdvances++;
            totalTimeAdvances++;
            ReportProgress(iteration + 1);

            if (consecutiveTimeAdvances > options.MaxConsecutiveTimeAdvances)
            {
                return Complete(SimulationExecutionReason.MaxConsecutiveTimeAdvancesExceeded, iteration);
            }
        }

        if (options.MaxIterations > 0)
        {
            if (options.ObserveTeardownCancellation && isTeardownRequested())
            {
                return Complete(SimulationExecutionReason.TeardownCancellationRequested, options.MaxIterations);
            }

            if (options.Condition is { } finalCondition && finalCondition())
            {
                return Complete(SimulationExecutionReason.ConditionMet, options.MaxIterations);
            }

            if (options.Condition is null)
            {
                var pendingWork = capturePendingWorkSummary();
                if (!hasPendingOperations() &&
                    pendingWork.RunnableCount == 0 &&
                    pendingWork.WaitingCount == 0)
                {
                    return Complete(
                        pendingWork.BlockedCount > 0
                            ? SimulationExecutionReason.IdleWithPendingWork
                            : SimulationExecutionReason.Idle,
                        options.MaxIterations,
                        pendingWork);
                }
            }
        }

        options.CancellationToken.ThrowIfCancellationRequested();
        return Complete(SimulationExecutionReason.MaxIterationsReached, options.MaxIterations);

        SimulationExecutionResult CompleteIdle(int iteration)
        {
            var pendingWork = capturePendingWorkSummary();
            var reason = pendingWork.BlockedCount > 0
                ? SimulationExecutionReason.IdleWithPendingWork
                : SimulationExecutionReason.Idle;
            return Complete(reason, iteration, pendingWork);
        }

        SimulationExecutionResult Complete(
            SimulationExecutionReason reason,
            int iteration,
            SimulationPendingWorkSummary? pendingWork = null,
            TimeSpan? attemptedTimeAdvance = null)
        {
            return new SimulationExecutionResult(
                reason,
                startTime,
                getUtcNow(),
                iteration,
                stepsExecuted,
                totalTimeAdvances,
                consecutiveTimeAdvances,
                pendingWork ?? capturePendingWorkSummary(),
                new SimulationExecutionLimits(options.MaxIterations, options.MaxSimulatedTimeAdvance, options.MaxConsecutiveTimeAdvances),
                attemptedTimeAdvance);
        }

        void ReportProgress(int iterations)
        {
            options.Progress?.Invoke(new SimulationProgressSnapshot(
                iterations,
                stepsExecuted,
                totalTimeAdvances,
                consecutiveTimeAdvances,
                startTime,
                getUtcNow()));
        }
    }
}
