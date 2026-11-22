using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tests.Scheduling;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

namespace Orleans.Testing.Simulation.Runtime.Tests.Racing;

/// <summary>
/// The race detector's retained state is bounded by the work in flight, not by the work done. Continuation chains,
/// <c>Parallel.ForAsync</c>, repeated root operations, fan-outs awaited or not, and a chain forking a helper per link
/// each keep their clocks, live clock slots, locations, and kept accesses within a bound independent of their length,
/// while a race at the end of a run is still reported. Objects the workloads allocate and drop are collected along the
/// way, and the detector releases their locations. Where a run keeps mutually unordered reads of a live location, the
/// detector keeps one per read and its clocks stay bounded.
/// </summary>
public sealed class RaceTrackerBoundsTests
{
    private const string Shared = "Fx.State::Shared";
    private const string Counter = "Fx.State::Counter";
    private const string Config = "Fx.State::Config";

    public enum ChainKind
    {
        Yield,
        AwaitOwnCompletion,
        Delay,
    }

    [Theory]
    [InlineData(ChainKind.Yield)]
    [InlineData(ChainKind.AwaitOwnCompletion)]
    [InlineData(ChainKind.Delay)]
    public void ContinuationChainKeepsStateBoundedAndReportsARaceAtItsEnd(ChainKind chain)
    {
        RaceTrackerStatistics shortRun = RunChain(chain, links: 100, out RaceReport? shortRace);
        RaceTrackerStatistics longRun = RunChain(chain, links: 10_000, out RaceReport? longRace);

        AssertRacesWithTheEarlyRead(shortRace);
        AssertRacesWithTheEarlyRead(longRace);
        AssertSameRetainedState(shortRun, longRun);
        Assert.Equal(0, longRun.Operations);

        // A link whose first event precedes its acquire of the previous link's completion starts a slot rather than
        // continuing the previous link's, so the links of such a chain alternate between two slots.
        Assert.InRange(longRun.LargestClock, 1, 2);
        Assert.Equal(2, longRun.Locations);
        Assert.Equal(2, longRun.RetainedAccesses);
    }

    [Fact]
    public void ParallelForAsyncKeepsStateBoundedAndReportsARaceOnItsLastIteration()
    {
        RaceTrackerStatistics shortRun = RunForAsync(iterations: 100, out RaceReport? shortRace);
        RaceTrackerStatistics longRun = RunForAsync(iterations: 10_000, out RaceReport? longRace);

        AssertRacesWithTheEarlyRead(shortRace);
        AssertRacesWithTheEarlyRead(longRace);
        Assert.Equal(0, longRun.Operations);
        Assert.Equal(shortRun.LargestClock, longRun.LargestClock);

        // One slot per worker, which the simulated processor count bounds, and the caller's.
        Assert.InRange(longRun.LargestClock, 1, SimulationRuntimeEnvironment.DefaultProcessorCount + 1);
        Assert.InRange(longRun.PeakSlots, 1, 2 * (SimulationRuntimeEnvironment.DefaultProcessorCount + 1));

        // Each body writes a fresh object, which is collected every few hundred iterations.
        Assert.InRange(longRun.Locations, 1, 4 * CollectionInterval);
        Assert.InRange(longRun.RetainedAccesses, 1, 4 * CollectionInterval);
    }

    [Fact]
    public void RepeatedRootOperationsReadingUnderALockKeepStateBounded()
    {
        RaceTrackerStatistics shortRun = RunTimerChain(ticks: 100);
        RaceTrackerStatistics longRun = RunTimerChain(ticks: 5_000);

        AssertSameRetainedState(shortRun, longRun);
        Assert.Equal(0, longRun.Operations);
        Assert.InRange(longRun.LargestClock, 1, 4);
        Assert.InRange(longRun.Slots, 1, 2);
    }

    [Fact]
    public void RepeatedAwaitedFanOutOverFreshObjectsKeepsStateBounded()
    {
        RaceTrackerStatistics shortRun = RunAwaitedFanOut(rounds: 5);
        RaceTrackerStatistics longRun = RunAwaitedFanOut(rounds: 50);

        // A round's children run at once, so a clock holds up to a round's worth of their slots, and a child's slot
        // lives on until the object it wrote is collected and swept; neither grows with the number of rounds.
        foreach (RaceTrackerStatistics run in new[] { shortRun, longRun })
        {
            Assert.Equal(0, run.Operations);
            Assert.InRange(run.LargestClock, 1, 4 * FanOutChildren);
            Assert.InRange(run.PeakSlots, 1, 4 * FanOutChildren);
            Assert.InRange(run.Locations, 0, 4 * FanOutChildren);
        }
    }

    [Fact]
    public void UnawaitedFanOutUnderASharedLockKeepsStateBounded()
    {
        RaceTrackerStatistics shortRun = RunUnawaitedFanOut(children: 100);
        RaceTrackerStatistics longRun = RunUnawaitedFanOut(children: 2_000);

        AssertSameRetainedState(shortRun, longRun);
        Assert.Equal(0, longRun.Operations);
        Assert.InRange(longRun.LargestClock, 1, 4);
        Assert.Equal(1, longRun.Locations);
        Assert.Equal(1, longRun.RetainedAccesses);
    }

    [Fact]
    public void ChainForkingAHelperWhichReadsAfterALockKeepsStateBoundedWhenItsObjectsAreCollected()
    {
        RaceTrackerStatistics shortRun = RunHelperChain(links: 500, readFreshObject: true);
        RaceTrackerStatistics longRun = RunHelperChain(links: 2_000, readFreshObject: true);

        foreach (RaceTrackerStatistics run in new[] { shortRun, longRun })
        {
            Assert.Equal(0, run.Operations);
            Assert.InRange(run.LargestClock, 1, 8);
            Assert.InRange(run.Locations, 1, 6 * CollectionInterval);
            Assert.InRange(run.RetainedAccesses, 1, 6 * CollectionInterval);
        }
    }

    [Fact]
    public void ChainForkingAHelperWhichReadsALiveLocationKeepsEachUnorderedReadAndBoundedClocks()
    {
        RaceTrackerStatistics shortRun = RunHelperChain(links: 500, readFreshObject: false);
        RaceTrackerStatistics longRun = RunHelperChain(links: 2_000, readFreshObject: false);

        // Each helper's read of the never-written location is unordered with every other, so a later write must be
        // checked against each, and each keeps its helper's slot; the clocks hold none of those slots.
        Assert.Equal(shortRun.LargestClock, longRun.LargestClock);
        Assert.InRange(longRun.LargestClock, 1, 8);
        Assert.Equal(2_000 + 1, longRun.RetainedAccesses);
        Assert.InRange(longRun.Slots, 2_000, 2_000 + 1);
    }

    [Fact]
    public void LongRunningOperationReadingFreshObjectsKeepsLocationsBounded()
    {
        using SimulationScheduler scheduler = SchedulerTestHarness.NewScheduler();
        var tracker = new RaceTracker();
        SimulationOperation operation = Start(scheduler, tracker, parent: null);
        for (int index = 0; index < 10_000; index++)
        {
            ReadFreshObject(tracker, operation);
            if (index % CollectionInterval == 0)
            {
                Collect();
            }
        }

        RaceTrackerStatistics statistics = tracker.Statistics;
        Assert.Equal(1, statistics.Operations);
        Assert.InRange(statistics.Locations, 1, 4 * CollectionInterval);
        Assert.InRange(statistics.RetainedAccesses, 1, 4 * CollectionInterval);
    }

    [Fact]
    public void ClockWhichManyEndedOperationsSignalIsPruned()
    {
        using SimulationScheduler scheduler = SchedulerTestHarness.NewScheduler();
        var tracker = new RaceTracker();
        var hub = new object();
        SimulationOperation waiter = Start(scheduler, tracker, parent: null);
        for (int index = 0; index < 1_000; index++)
        {
            SimulationOperation signaler = Start(scheduler, tracker, parent: null);
            tracker.SignalSynchronization(signaler, hub);
            tracker.CompleteOperation(signaler);
            tracker.WaitSynchronization(waiter, hub);
        }

        Assert.InRange(tracker.Statistics.LargestClock, 1, 4);
        Assert.InRange(tracker.ClockSize(waiter), 1, 4);
    }

    [Fact]
    public void ForkedOperationCopiesOnlyTheEntriesWhichDecideSomething()
    {
        using SimulationScheduler scheduler = SchedulerTestHarness.NewScheduler();
        var tracker = new RaceTracker();
        var signal = new object();
        SimulationOperation parent = Start(scheduler, tracker, parent: null);
        var signalers = new List<SimulationOperation>();
        for (int index = 0; index < 10; index++)
        {
            SimulationOperation signaler = Start(scheduler, tracker, parent: null);
            tracker.SignalSynchronization(signaler, signal);
            signalers.Add(signaler);
        }

        tracker.WaitSynchronization(parent, signal);
        Assert.Equal(11, tracker.ClockSize(parent));

        // The signalers keep no access, so their slots end with them, and the parent does not merge again.
        signalers.ForEach(tracker.CompleteOperation);
        SimulationOperation child = Start(scheduler, tracker, parent);

        Assert.Equal(11, tracker.ClockSize(parent));
        Assert.Equal(1, tracker.ClockSize(child));
    }

    private const int CollectionInterval = 100;
    private const int FanOutChildren = 100;

    private static SimulationOperation Start(SimulationScheduler scheduler, RaceTracker tracker, SimulationOperation? parent)
    {
        SimulationOperation operation = scheduler.Register("bounds", static () => { });
        tracker.RegisterOperation(operation, parent);
        return operation;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReadFreshObject(RaceTracker tracker, SimulationOperation operation) =>
        tracker.RecordAccess(
            operation,
            RaceAccessKind.Read,
            tracker.ResolveLocation(RaceMemoryLocationKind.InstanceField, new Box(), "Box::Value", null)!,
            new RaceSourceLocation("reader", 1, null, -1),
            []);

    // The slots a run started and its sweeps count its work, not what it retains.
    private static void AssertSameRetainedState(RaceTrackerStatistics shortRun, RaceTrackerStatistics longRun) =>
        Assert.Equal(shortRun with { SlotsStarted = 0, Sweeps = 0 }, longRun with { SlotsStarted = 0, Sweeps = 0 });

    private static void AssertRacesWithTheEarlyRead(RaceReport? race)
    {
        Assert.NotNull(race);
        Assert.Equal(("reader", RaceAccessKind.Read), (race.FirstAccess.Source.Method, race.FirstAccess.Kind));
        Assert.Equal(RaceAccessKind.Write, race.SecondAccess.Kind);
        Assert.Equal(Shared, race.SecondAccess.Location.Member);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    // Runs the operations which start registers until the simulation is idle, firing virtual timers as it goes.
    private static RaceTrackerStatistics Run(Action<SimulationScheduler> start, out RaceReport? race)
    {
        using var host = new SimulationSchedulerTestHost();
        SimulationScheduler scheduler = host.Scheduler;
        TaskTestHarness.RunInSimulation(host, () =>
        {
            start(scheduler);
            scheduler.Drain(TestContext.Current.CancellationToken);
        });

        race = scheduler.FirstRace;
        return scheduler.RaceTrackerStatistics;
    }

    // Each link reads the shared location and writes the chain's own counter, then schedules the next link: through a
    // yield, through an await of a task the link completes after registering the continuation, or through a virtual
    // delay whose timer callback, a root operation, completes the awaited task. The last link writes the shared
    // location, which an operation started alongside the chain read once, unordered with every link.
    private static RaceTrackerStatistics RunChain(ChainKind chain, int links, out RaceReport? race)
    {
        int completed = 0;

        void Link()
        {
            RaceInstrumentation.ReadStatic(Shared, "link", 1, null, -1);
            RaceInstrumentation.WriteStatic(Counter, "link", 2, null, -1);
            if (++completed == links)
            {
                RaceInstrumentation.WriteStatic(Shared, "link", 3, null, -1);
                return;
            }

            switch (chain)
            {
                case ChainKind.Yield:
                    new ControlledYieldAwaitable().GetAwaiter().UnsafeOnCompleted(Link);
                    break;
                case ChainKind.AwaitOwnCompletion:
                    var next = new TaskCompletionSource();
                    var awaiter = new ControlledTaskAwaiter(next.Task);
                    awaiter.UnsafeOnCompleted(() =>
                    {
                        awaiter.GetResult();
                        Link();
                    });
                    ControlledTaskCompletionSource.SetResult(next);
                    break;
                case ChainKind.Delay:
                    var delay = new ControlledTaskAwaiter(ControlledTask.Delay(1));
                    delay.UnsafeOnCompleted(() =>
                    {
                        delay.GetResult();
                        Link();
                    });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(chain), chain, null);
            }
        }

        RaceTrackerStatistics statistics = Run(
            scheduler =>
            {
                scheduler.Schedule("chain", Link);
                scheduler.Schedule("reader", () => RaceInstrumentation.ReadStatic(Shared, "reader", 4, null, -1));
            },
            out race);
        Assert.Equal(links, completed);
        return statistics;
    }

    // Every body reads the shared location and writes a fresh object. The last iteration's body also writes the shared
    // location, which an operation started alongside the loop read once, unordered with the loop.
    private static RaceTrackerStatistics RunForAsync(int iterations, out RaceReport? race)
    {
        Task? loop = null;
        RaceTrackerStatistics statistics = Run(
            scheduler =>
            {
                scheduler.Schedule("loop", () =>
                {
                    loop = ControlledParallel.ForAsync(0, iterations, (index, _) =>
                    {
                        RaceInstrumentation.ReadStatic(Shared, "body", 1, null, -1);
                        RaceInstrumentation.WriteInstance(new Box(), "Box::Value", "body", 2, null, -1);
                        if (index % CollectionInterval == 0)
                        {
                            Collect();
                        }

                        if (index == iterations - 1)
                        {
                            RaceInstrumentation.WriteStatic(Shared, "body", 3, null, -1);
                        }

                        return ValueTask.CompletedTask;
                    });
                    ControlledTask.Wait(loop);
                });
                scheduler.Schedule("reader", () => RaceInstrumentation.ReadStatic(Shared, "reader", 4, null, -1));
            },
            out race);
        Assert.NotNull(loop);
        Assert.True(loop.IsCompletedSuccessfully);
        return statistics;
    }

    // A chain awaits a task which a timer callback, a root operation ordered after nothing, completes after reading a
    // never-written location under a lock every callback takes.
    private static RaceTrackerStatistics RunTimerChain(int ticks)
    {
        var gate = new object();
        int fired = 0;
        RaceTrackerStatistics statistics = Run(
            scheduler =>
            {
                void Link()
                {
                    if (fired == ticks)
                    {
                        return;
                    }

                    var tick = new TaskCompletionSource();
                    scheduler.ScheduleAfter(
                        "timer",
                        () =>
                        {
                            ControlledMonitor.Enter(gate);
                            RaceInstrumentation.ReadStatic(Config, "timer", 1, null, -1);
                            ControlledMonitor.Exit(gate);
                            fired++;
                            ControlledTaskCompletionSource.SetResult(tick);
                        },
                        TimeSpan.FromMilliseconds(1));
                    var awaiter = new ControlledTaskAwaiter(tick.Task);
                    awaiter.UnsafeOnCompleted(() =>
                    {
                        awaiter.GetResult();
                        Link();
                    });
                }

                scheduler.Schedule("chain", Link);
            },
            out RaceReport? race);
        Assert.Null(race);
        Assert.Equal(ticks, fired);
        return statistics;
    }

    // In each round, an operation starts children which each write a fresh object, then waits for them all.
    private static RaceTrackerStatistics RunAwaitedFanOut(int rounds)
    {
        RaceTrackerStatistics statistics = Run(
            scheduler => scheduler.Schedule("parent", () =>
            {
                for (int round = 0; round < rounds; round++)
                {
                    var tasks = new Task[FanOutChildren];
                    for (int index = 0; index < tasks.Length; index++)
                    {
                        tasks[index] = ControlledTask.Run(
                            () => RaceInstrumentation.WriteInstance(new Box(), "Box::Value", "child", 1, null, -1));
                    }

                    ControlledTask.Wait(ControlledTask.WhenAll(tasks));
                    Collect();
                }
            }),
            out RaceReport? race);
        Assert.Null(race);
        return statistics;
    }

    // An operation starts children which each increment a shared counter under a shared lock, and awaits none of them.
    private static RaceTrackerStatistics RunUnawaitedFanOut(int children)
    {
        var gate = new object();
        RaceTrackerStatistics statistics = Run(
            scheduler => scheduler.Schedule("parent", () =>
            {
                for (int index = 0; index < children; index++)
                {
                    _ = ControlledTask.Run(() =>
                    {
                        ControlledMonitor.Enter(gate);
                        RaceInstrumentation.ReadStatic(Counter, "child", 1, null, -1);
                        RaceInstrumentation.WriteStatic(Counter, "child", 2, null, -1);
                        ControlledMonitor.Exit(gate);
                    });
                }
            }),
            out RaceReport? race);
        Assert.Null(race);
        return statistics;
    }

    // Each link forks a helper and increments a log under a lock, then schedules the next link after a virtual delay, by
    // which time the helper has finished. The helper increments the log under the lock, then reads either a fresh
    // object's field or a never-written static field. The link and its helper share only the lock, and the read follows
    // the helper's release of it, so no later link orders it.
    private static RaceTrackerStatistics RunHelperChain(int links, bool readFreshObject)
    {
        var gate = new object();
        int completed = 0;

        void Increment(string method)
        {
            ControlledMonitor.Enter(gate);
            RaceInstrumentation.ReadStatic(Counter, method, 1, null, -1);
            RaceInstrumentation.WriteStatic(Counter, method, 2, null, -1);
            ControlledMonitor.Exit(gate);
        }

        void Link()
        {
            if (completed == links)
            {
                return;
            }

            int link = completed++;
            _ = ControlledTask.Run(() =>
            {
                Increment("helper");
                if (readFreshObject)
                {
                    RaceInstrumentation.ReadInstance(new Box(), "Box::Value", "helper", 3, null, -1);
                }
                else
                {
                    RaceInstrumentation.ReadStatic(Config, "helper", 3, null, -1);
                }
            });
            Increment("link");
            if (readFreshObject && link % CollectionInterval == 0)
            {
                Collect();
            }

            var delay = new ControlledTaskAwaiter(ControlledTask.Delay(1));
            delay.UnsafeOnCompleted(() =>
            {
                delay.GetResult();
                Link();
            });
        }

        RaceTrackerStatistics statistics = Run(scheduler => scheduler.Schedule("chain", Link), out RaceReport? race);
        Assert.Null(race);
        Assert.Equal(links, completed);
        return statistics;
    }

    private sealed class Box;
}
