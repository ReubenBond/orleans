using System.Globalization;

namespace Orleans.Testing.Simulation;

/// <summary>
/// <para>
/// A structured outcome of a single <see cref="SimulationCluster"/> drive-loop
/// execution (<c>RunUntil</c>, <c>RunUntilIdle</c>, or <c>RunFor</c>).
/// </para>
/// <para>
/// It reports exactly why the loop stopped, how much simulated
/// time and work it consumed, and what (if anything) is still pending.
/// </para>
/// </summary>
public sealed class SimulationExecutionResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationExecutionResult"/> class.
    /// </summary>
    internal SimulationExecutionResult(
        SimulationExecutionReason reason,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        int iterations,
        int stepsExecuted,
        int timeAdvanceCount,
        int consecutiveTimeAdvanceCount,
        SimulationPendingWorkSummary pendingWork,
        SimulationExecutionLimits limits,
        TimeSpan? attemptedTimeAdvance)
    {
        ArgumentNullException.ThrowIfNull(pendingWork);
        ArgumentNullException.ThrowIfNull(limits);
        Reason = reason;
        StartTime = startTime;
        EndTime = endTime;
        Iterations = iterations;
        StepsExecuted = stepsExecuted;
        TimeAdvanceCount = timeAdvanceCount;
        ConsecutiveTimeAdvanceCount = consecutiveTimeAdvanceCount;
        PendingWork = pendingWork;
        Limits = limits;
        AttemptedTimeAdvance = attemptedTimeAdvance;
    }

    /// <summary>
    /// Gets the reason the execution stopped.
    /// </summary>
    public SimulationExecutionReason Reason { get; }

    /// <summary>
    /// Gets a value indicating whether the requested condition was met.
    /// Always <see langword="false"/> for <c>RunUntilIdle</c>/<c>RunFor</c>, which have no condition.
    /// </summary>
    public bool ConditionMet => Reason == SimulationExecutionReason.ConditionMet;

    /// <summary>
    /// Gets the simulated time at which this execution began.
    /// </summary>
    public DateTimeOffset StartTime { get; }

    /// <summary>
    /// Gets the simulated time at which this execution stopped.
    /// </summary>
    public DateTimeOffset EndTime { get; }

    /// <summary>
    /// Gets the amount of simulated time that elapsed during this execution (<see cref="EndTime"/> - <see cref="StartTime"/>).
    /// </summary>
    public TimeSpan ElapsedSimulatedTime => EndTime - StartTime;

    /// <summary>
    /// Gets the number of drive-loop iterations executed. Each iteration is one condition check
    /// plus, at most, one round-robin step or one time advance.
    /// </summary>
    public int Iterations { get; }

    /// <summary>
    /// Gets the number of scheduled items actually executed (successful round-robin steps).
    /// </summary>
    public int StepsExecuted { get; }

    /// <summary>
    /// Gets the total number of times the simulated clock was advanced during this execution.
    /// </summary>
    public int TimeAdvanceCount { get; }

    /// <summary>
    /// Gets the number of consecutive time advances observed with no work executed in between, as
    /// of when this execution stopped. Only meaningful for diagnosing <see cref="SimulationExecutionReason.MaxConsecutiveTimeAdvancesExceeded"/>.
    /// </summary>
    public int ConsecutiveTimeAdvanceCount { get; }

    /// <summary>
    /// Gets a snapshot of the work still pending (runnable, waiting, or blocked) across the
    /// cluster and node queues when this execution stopped.
    /// </summary>
    public SimulationPendingWorkSummary PendingWork { get; }

    /// <summary>
    /// Gets the limits that were configured for this execution.
    /// </summary>
    public SimulationExecutionLimits Limits { get; }

    /// <summary>
    /// Gets the simulated-time gap that would have needed to be advanced across, when
    /// <see cref="Reason"/> is <see cref="SimulationExecutionReason.MaxSimulatedTimeAdvanceExceeded"/>.
    /// <see langword="null"/> for every other reason.
    /// </summary>
    public TimeSpan? AttemptedTimeAdvance { get; }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Reason} (conditionMet={ConditionMet}, iterations={Iterations}, steps={StepsExecuted}, " +
        $"timeAdvances={TimeAdvanceCount}, elapsed={ElapsedSimulatedTime}, pending={PendingWork.PendingCount})");

    /// <summary>
    /// Formats a detailed, multi-line, deterministic report of this result, including the full
    /// pending-work diagnostics. Intended for logging or test failure messages.
    /// </summary>
    /// <returns>A deterministic, invariant-culture, multi-line description of this result.</returns>
    public string ToDetailedString()
    {
        var attempted = AttemptedTimeAdvance is { } delta ? delta.ToString() : "n/a";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            Reason: {Reason}
            ConditionMet: {ConditionMet}
            StartTime: {StartTime:O}
            EndTime: {EndTime:O}
            ElapsedSimulatedTime: {ElapsedSimulatedTime}
            Iterations: {Iterations}
            StepsExecuted: {StepsExecuted}
            TimeAdvanceCount: {TimeAdvanceCount}
            ConsecutiveTimeAdvanceCount: {ConsecutiveTimeAdvanceCount}
            AttemptedTimeAdvance: {attempted}
            Limits: {Limits}
            PendingWork: {PendingWork}
            """);
    }
}
