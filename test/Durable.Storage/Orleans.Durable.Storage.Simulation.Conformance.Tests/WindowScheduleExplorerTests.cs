using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// Qualifies the window explorer (verification.md §4.1): over two chains of yields it reaches every interleaving, the
/// failing schedule it reports replays from its deviations, and a run which depends on more than its choices fails the
/// exploration rather than reporting a schedule it did not take.
/// </summary>
public sealed class WindowScheduleExplorerTests
{
    private static readonly ScheduleBound Unbounded = new(10_000, int.MaxValue);

    /// <summary>
    /// Two chains of <c>a</c> and <c>b</c> steps, each step its own operation, interleave in <c>C(a + b, a)</c> orders,
    /// and an unbounded exploration reaches each and ends complete; a bound of one preemption reaches every order with at
    /// most one switch away from a chain which could go on.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    public void AnUnboundedExplorationReachesEveryInterleaving(int a, int b)
    {
        HashSet<string> orders = [with(StringComparer.Ordinal)];
        var exploration = WindowScheduleExplorer.Explore(Unbounded, (prefix, expected) => Run(a, b, prefix, expected, orders));

        Assert.Null(exploration.Failure);
        Assert.True(exploration.Complete, exploration.ToString());
        Assert.Equal(Binomial(a + b, a), orders.Count);

        HashSet<string> bounded = [with(StringComparer.Ordinal)];
        var oneDeviation = WindowScheduleExplorer.Explore(new ScheduleBound(10_000, 1), (prefix, expected) => Run(a, b, prefix, expected, bounded));
        Assert.True(oneDeviation.Schedules <= exploration.Schedules);
        Assert.Subset(orders, bounded);
        Assert.Contains(bounded, static order => order.Contains("ab", StringComparison.Ordinal) || order.Contains("ba", StringComparison.Ordinal));
    }

    /// <summary>
    /// A schedule which fails replays from the deviations the exploration reports, as <c>CLOG_SCHEDULE</c> gives them.
    /// </summary>
    [Fact]
    public void AFailingScheduleReplaysFromItsDeviations()
    {
        HashSet<string> orders = [with(StringComparer.Ordinal)];
        const string Failing = "abba";
        var exploration = WindowScheduleExplorer.Explore(Unbounded, (prefix, expected) => Run(2, 2, prefix, expected, orders, failOn: Failing));
        var failed = Assert.IsType<ScheduleRun>(exploration.Failure);

        var replay = Run(2, 2, WindowScheduleExplorer.ParsePrefix(WindowScheduleExplorer.Format(failed.Prefix)), null, [], failOn: Failing);
        Assert.NotNull(replay.Failure);
        Assert.Equal(failed.Choices.Select(static choice => choice.Chosen), replay.Choices.Select(static choice => choice.Chosen));
    }

    /// <summary>
    /// A run whose choice points depend on more than its earlier choices, here a third chain on every other run, offers
    /// other candidates than the run it extends, and the exploration fails with the divergence.
    /// </summary>
    [Fact]
    public void ARunWhichDependsOnMoreThanItsChoicesFailsTheExploration()
    {
        var runs = 0;
        var exploration = WindowScheduleExplorer.Explore(Unbounded, (prefix, expected) => Run(2, 2, prefix, expected, [], third: runs++ % 2 == 1));

        var failed = Assert.IsType<ScheduleRun>(exploration.Failure);
        Assert.NotNull(failed.Divergence);
        Assert.Null(failed.Failure);
    }

    private static long Binomial(int n, int k)
    {
        long result = 1;
        for (var i = 1; i <= k; i++)
        {
            result = result * (n - k + i) / i;
        }

        return result;
    }

    /// <summary>
    /// Runs two chains of yields in one window under the schedule <paramref name="prefix"/> gives, adding the order their
    /// steps ran in to <paramref name="orders"/>; the run fails when that order is <paramref name="failOn"/>.
    /// </summary>
    private static ScheduleRun Run(
        int a,
        int b,
        IReadOnlyList<int> prefix,
        IReadOnlyList<ScheduleChoice>? expected,
        HashSet<string> orders,
        string? failOn = null,
        bool third = false)
    {
        WindowSchedulingStrategy? strategy = null;
        var run = SeededSimulation.RunCase(
            1,
            async environment =>
            {
                List<char> order = [];
                strategy!.Open();
                var first = Chain(environment, 'a', a, order);
                var second = Chain(environment, 'b', b, order);
                var other = third ? Chain(environment, 'c', 1, order) : Task.CompletedTask;
                await first;
                await second;
                await other;
                strategy.Close();
                var ran = new string([.. order]);
                _ = orders.Add(ran);
                Assert.NotEqual(failOn, ran);
            },
            outside => strategy = new WindowSchedulingStrategy(outside, prefix, expected));
        return new ScheduleRun(prefix, strategy!.Choices, run.Failure, strategy.Divergence);
    }

    /// <summary>
    /// Starts a chain of <paramref name="steps"/> steps on the cluster's lane, each after a yield, so each is an operation
    /// of its own.
    /// </summary>
    private static Task Chain(SimulationEnvironment environment, char name, int steps, List<char> order)
    {
        var done = new TaskCompletionSource();
        environment.Cluster.SchedulerLane.Enqueue(() => _ = RunAsync());
        return done.Task;

        async Task RunAsync()
        {
            for (var step = 0; step < steps; step++)
            {
                await Task.Yield();
                order.Add(name);
            }

            done.SetResult();
        }
    }
}
