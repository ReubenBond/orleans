using System.Globalization;
using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation;

internal readonly record struct SimulationProgressSnapshot(
    int Iterations,
    int StepsExecuted,
    int TimeAdvanceCount,
    int ConsecutiveTimeAdvanceCount,
    DateTimeOffset StartTime,
    DateTimeOffset CurrentTime);

internal static class SimulationProgressOutput
{
    private const string AppContextKey = "Orleans.Testing.Simulation.ProgressOutput";

    public static TextWriter Writer => AppContext.GetData(AppContextKey) as TextWriter ?? Console.Error;

    public static void SetWriter(TextWriter? writer) => AppContext.SetData(AppContextKey, writer);
}

internal sealed class SimulationProgressReporter
{
    private readonly TimeSpan _interval;
    private readonly SimulationRuntimeIdentity _runtime;
    private readonly TextWriter _output;
    private readonly Func<TimeSpan> _getWallTime;
    private readonly Func<SimulationPendingWorkSummary> _capturePendingWork;
    private readonly Func<int> _getPendingOperationCount;
    private TimeSpan _lastReportTime;
    private DateTimeOffset? _simulationStartTime;
    private int _completedIterations;
    private int _completedSteps;
    private int _completedTimeAdvances;

    internal SimulationProgressReporter(
        TimeSpan interval,
        SimulationRuntimeIdentity runtime,
        TextWriter output,
        Func<TimeSpan> getWallTime,
        Func<SimulationPendingWorkSummary> capturePendingWork,
        Func<int> getPendingOperationCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(getWallTime);
        ArgumentNullException.ThrowIfNull(capturePendingWork);
        ArgumentNullException.ThrowIfNull(getPendingOperationCount);

        _interval = interval;
        _runtime = runtime;
        _output = output;
        _getWallTime = getWallTime;
        _capturePendingWork = capturePendingWork;
        _getPendingOperationCount = getPendingOperationCount;
        _lastReportTime = getWallTime();
    }

    public static SimulationProgressReporter? CreateFromEnvironment(
        SimulationRuntimeIdentity runtime,
        Func<SimulationPendingWorkSummary> capturePendingWork,
        Func<int> getPendingOperationCount)
    {
        TimeSpan? interval = SimulationProgressEnvironment.GetInterval();
        if (interval is null)
        {
            return null;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        return new SimulationProgressReporter(
            interval.Value,
            runtime,
            SimulationProgressOutput.Writer,
            () => stopwatch.Elapsed,
            capturePendingWork,
            getPendingOperationCount);
    }

    public void Report(SimulationProgressSnapshot snapshot)
    {
        TimeSpan wallTime = _getWallTime();
        if (wallTime - _lastReportTime < _interval)
        {
            return;
        }

        _lastReportTime = wallTime;
        SimulationPendingWorkSummary pending = _capturePendingWork();
        int iterations = _completedIterations + snapshot.Iterations;
        int steps = _completedSteps + snapshot.StepsExecuted;
        int timeAdvances = _completedTimeAdvances + snapshot.TimeAdvanceCount;
        _simulationStartTime ??= snapshot.StartTime;
        TimeSpan simulatedTime = snapshot.CurrentTime - _simulationStartTime.Value;

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[Orleans simulation] runtime={_runtime.Id:N} seed={_runtime.Seed} wall={wallTime:c} " +
            $"iterations={iterations} steps={steps} timeAdvances={timeAdvances} " +
            $"consecutiveTimeAdvances={snapshot.ConsecutiveTimeAdvanceCount} simulated={simulatedTime:c} " +
            $"operations={_getPendingOperationCount()} runnable={pending.RunnableCount} " +
            $"waiting={pending.WaitingCount} blocked={pending.BlockedCount}"));
    }

    public void CompleteBatch(SimulationExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _simulationStartTime ??= result.StartTime;
        _completedIterations += result.Iterations;
        _completedSteps += result.StepsExecuted;
        _completedTimeAdvances += result.TimeAdvanceCount;
    }
}
