using System.Runtime.CompilerServices;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;
using Orleans.Testing.Simulation.Runtime.Tests.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Tests.Racing;

/// <summary>
/// No race verdict, race report, or scheduling decision depends on clock-slot identity or GC timing. Each worker writes
/// a fresh object and forks a continuation which reads a shared location, and one in fifty writes it; the continuation
/// continues the worker's clock slot only while the worker's write keeps it live. One seeded run keeps every worker's
/// object alive, so no sweep releases it; the other drops them and collects garbage at every operation transition, so
/// the detector sweeps the objects, ends the workers' slots sooner, and starts more slots. Both report the same race,
/// reach the same scheduling points, and schedule the same transitions.
/// </summary>
public sealed class RaceDeterminismTests
{
    private const int Workers = 150;

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(29)]
    public void ForcedCollectionsChangeNoOutput(int seed)
    {
        Outcome plain = Run(seed, collect: false);
        GC.KeepAlive(plain.Retained);
        Outcome collected = Run(seed, collect: true);

        Assert.NotNull(plain.Race);
        Assert.Equal(plain.Race, collected.Race);
        Assert.Equal(plain.Points, collected.Points);
        Assert.Equal(plain.Transitions, collected.Transitions);

        // The runs numbered their slots differently, so the comparison above covers slot identity.
        Assert.True(
            collected.SlotsStarted > plain.SlotsStarted,
            $"The collecting run started {collected.SlotsStarted} slots, not more than the {plain.SlotsStarted} the other did.");
    }

    private static Outcome Run(int seed, bool collect)
    {
        var listener = new Listener(collect);
        List<object>? retained = collect ? null : [];
        using SimulationScheduler scheduler = SchedulerTestHarness.NewScheduler(listener, seed);
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.SeededRandom(seed);
        for (int index = 0; index < Workers; index++)
        {
            int worker = index;
            scheduler.Schedule("worker", () =>
            {
                WriteFreshObject(worker, retained);
                scheduler.Schedule("continuation", () =>
                {
                    RaceInstrumentation.ReadStatic("Fx.State::Shared", "continuation", 2, null, -1);
                    if (worker % 50 == 49)
                    {
                        RaceInstrumentation.WriteStatic("Fx.State::Shared", "continuation", 3, null, -1);
                    }
                });
            });
        }

        scheduler.Drain(TestContext.Current.CancellationToken);
        return new Outcome(
            scheduler.FirstRace?.ToDetailedString(),
            string.Join('\n', scheduler.CaptureRaceSchedulingPoints()),
            string.Join(',', listener.Transitions),
            scheduler.RaceTrackerStatistics.SlotsStarted,
            retained);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteFreshObject(int worker, List<object>? retained)
    {
        var target = new int[worker % 3 + 1];
        retained?.Add(target);
        RaceInstrumentation.WriteInstance(target, "Fx.State::Field", "worker", 1, null, -1);
    }

    private sealed record Outcome(string? Race, string Points, string Transitions, long SlotsStarted, List<object>? Retained);

    private sealed class Listener(bool collect) : ISimulationOperationListener
    {
        public List<string> Transitions { get; } = [];

        public void OnStateChanged(SimulationOperation operation, SimulationOperationState newState)
        {
            lock (Transitions)
            {
                Transitions.Add($"{operation.Id.Value}:{newState}");
            }

            if (collect)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }
}
