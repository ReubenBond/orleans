using System.Globalization;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// Runs a conformance kit's cases each in a Clockwork simulation of its own, seeded by the case, and starts the calls of
/// each concurrent group as operations of the simulation's scheduler (verification.md §4.1, §7.1).
/// </summary>
/// <remarks>
/// <para>
/// A case's seed decides its system's faults and, outside its explored windows, its schedule, so CsCheck shrinks a
/// failing case's operations and interleaving together, and <c>CLOG_TEST_SEED</c> replays both. Each case is independent
/// of every other: a fresh cluster, virtual clock, and page scope, with every check of the driver
/// (<see cref="SeededSimulation.RunCase"/>), its page leak and quiescence checks among them.
/// </para>
/// <para>
/// With a <see cref="ScheduleBound"/>, each case runs under every schedule of its concurrent windows within the bound,
/// which <see cref="WindowScheduleExplorer"/> enumerates, and fails with the first schedule the spec does not allow,
/// whose deviations <c>CLOG_SCHEDULE</c> replays. Each exploration's count of schedules is kept for the test to report.
/// </para>
/// <para>
/// The kit's cases run one at a time, so a CsCheck sample runs on one thread, since a simulation project runs one
/// simulation at a time.
/// </para>
/// </remarks>
internal sealed class SimulatedConformance(ScheduleBound? bound = null) : IConformanceScheduler
{
    private readonly List<ScheduleExploration> _explorations = [];
    private SimulationEnvironment? _environment;
    private WindowSchedulingStrategy? _strategy;

    /// <summary>
    /// Gets the simulation of the running case, in which its system is created.
    /// </summary>
    public SimulationEnvironment Environment =>
        _environment ?? throw new InvalidOperationException("No conformance case is running.");

    /// <summary>
    /// Gets the explorations of the cases run so far, in order.
    /// </summary>
    public IReadOnlyList<ScheduleExploration> Explorations => _explorations;

    /// <summary>
    /// Gets the number of cases run so far.
    /// </summary>
    public int Cases { get; private set; }

    /// <summary>
    /// Gets the number of simulations run so far: one per case, or one per schedule explored.
    /// </summary>
    public int Runs { get; private set; }

    /// <summary>
    /// Gets or sets a callback which observes every run, such as a check that a case replays.
    /// </summary>
    public Action<ConformanceCase, ClusterSimulationRun>? Observed { get; set; }

    public Task RunCaseAsync(ConformanceCase testCase, Func<Task> body)
    {
        Cases++;
        if (bound is null)
        {
            var run = Run(testCase, body, strategy: null);
            return run.Failure is { } failure
                ? Task.FromException(new ConformanceScheduleException(Describe(failure.Message, testCase, "the case's seeded schedule"), failure))
                : Task.CompletedTask;
        }

        var exploration = WindowScheduleExplorer.Explore(bound, (prefix, expected) =>
        {
            WindowSchedulingStrategy? strategy = null;
            var run = Run(testCase, body, outside => strategy = new WindowSchedulingStrategy(outside, prefix, expected));
            return new ScheduleRun(prefix, strategy!.Choices, run.Failure, strategy.Divergence);
        });
        _explorations.Add(exploration);
        if (exploration.Failure is not { } failed)
        {
            return Task.CompletedTask;
        }

        var schedule = string.Create(
            CultureInfo.InvariantCulture,
            $"schedule {exploration.Schedules} of its exploration ({exploration}), {WindowScheduleExplorer.ScheduleVariable}={WindowScheduleExplorer.Format(failed.Prefix)}");
        var message = failed.Divergence is { } divergence
            ? Invariant($"The exploration of the case's windows diverged at {divergence}: the run depends on more than its choices, so no schedule of it replays.")
            : failed.Failure!.Message;
        return Task.FromException(new ConformanceScheduleException(Describe(message, testCase, schedule), failed.Failure));
    }

    public Task<object> Start(Func<Task<object>> call)
    {
        // Each call starts as an operation of its own, so the scheduler chooses which call takes the first step. The
        // awaits run in the test assembly's code on the lane's synchronization context, so they resume in the simulation.
        var started = new TaskCompletionSource<Task<object>>();
        Environment.Cluster.SchedulerLane.Enqueue(() => started.SetResult(call()));
        return AnswerAsync(started.Task);

        static async Task<object> AnswerAsync(Task<Task<object>> started) => await await started;
    }

    public void GroupStarting() => _strategy?.Open();

    public void GroupCompleted() => _strategy?.Close();

    /// <summary>
    /// Returns a summary of the explorations: cases, schedules, the most of one case, and the share explored completely.
    /// </summary>
    public string Summary()
    {
        if (_explorations.Count == 0)
        {
            return Invariant($"{Cases} cases in {Runs} simulations, each under its seeded schedule");
        }

        var schedules = _explorations.Sum(static exploration => exploration.Schedules);
        var complete = _explorations.Count(static exploration => exploration.Complete);
        var windowed = _explorations.Count(static exploration => exploration.ChoicePoints > 0);
        return Invariant(
            $"{_explorations.Count} cases explored in {schedules} schedules ({windowed} with choice points in their windows, at most {_explorations.Max(static exploration => exploration.Schedules)} schedules and {_explorations.Max(static exploration => exploration.ChoicePoints)} choice points in one); {complete} explored completely, the rest to {bound!.MaxDeviations} deviations or {bound.MaxSchedules} schedules");
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private static string Describe(string failure, ConformanceCase testCase, string schedule) =>
        Invariant($"{failure}{System.Environment.NewLine}The case ran in a simulation of its own, seed {testCase.Seed}, under {schedule}.");

    private ClusterSimulationRun Run(ConformanceCase testCase, Func<Task> body, Func<ISimulationSchedulingStrategy, WindowSchedulingStrategy>? strategy)
    {
        Runs++;
        try
        {
            var run = SeededSimulation.RunCase(
                testCase.Seed,
                environment =>
                {
                    _environment = environment;
                    return body();
                },
                strategy is null ? null : outside => _strategy = strategy(outside));
            Observed?.Invoke(testCase, run);
            return run;
        }
        finally
        {
            _environment = null;
            _strategy = null;
        }
    }
}

/// <summary>
/// Fails a conformance case run in a simulation, naming its seed and the schedule it ran.
/// </summary>
public sealed class ConformanceScheduleException : Exception
{
    public ConformanceScheduleException()
    {
    }

    public ConformanceScheduleException(string message)
        : base(message)
    {
    }

    public ConformanceScheduleException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
