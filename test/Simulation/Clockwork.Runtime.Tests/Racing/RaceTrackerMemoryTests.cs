using System.Runtime.CompilerServices;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Tests.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Tests.Racing;

/// <summary>Runs the memory measurements alone, so no other test's allocations reach the heap they measure.</summary>
[CollectionDefinition(nameof(RaceTrackerMemoryIsolation), DisableParallelization = true)]
public sealed class RaceTrackerMemoryIsolation;

/// <summary>
/// The race detector's memory, measured directly on a chain whose every link forks a helper, which increments a counter
/// under a lock and then reads a field of a fresh object: the shape whose clocks once grew by one entry per link. Each
/// link allocates the same number of bytes however long the chain has run, and the heap the detector retains after a
/// full collection is the same after 10³ links as after 10⁴ and 6·10⁴. On the steady path, an access allocates nothing
/// and a lock section only the array of the locks its holder holds. The run collects its young garbage every hundred links,
/// as a run which allocates does, so the detector can release the locations of the objects it drops.
/// </summary>
[Collection(nameof(RaceTrackerMemoryIsolation))]
public sealed class RaceTrackerMemoryTests
{
    [Fact]
    public void EachLinkAllocatesTheSameAndRetainedHeapIsFlat()
    {
        // A first run warms up code and statics, whose allocations are not the detector's.
        _ = Measure(links: 1_000);
        Measurement shortRun = Measure(links: 1_000);
        foreach (int links in new[] { 10_000, 60_000 })
        {
            Measurement longRun = Measure(links);

            // The first window follows warm-up of the detector's tables; the last ends the run.
            Assert.InRange(longRun.LateBytesPerLink, 0, (longRun.EarlyBytesPerLink * 1.1) + 16);
            Assert.InRange(longRun.RetainedBytes, 0, shortRun.RetainedBytes + (64 * 1024));
            Assert.Equal(shortRun.Statistics.LargestClock, longRun.Statistics.LargestClock);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    public void SteadyAccessesAllocateNothingAndLockSectionsOnlyTheirHeldLockIds(int clockSize)
    {
        const int Iterations = 1_000;
        using SimulationScheduler scheduler = SchedulerTestHarness.NewScheduler();
        var tracker = new RaceTracker();
        SimulationOperation operation = scheduler.Register("steady", static () => { });
        tracker.RegisterOperation(operation, parent: null);
        var signal = new object();
        for (int index = 1; index < clockSize; index++)
        {
            SimulationOperation signaler = scheduler.Register("signaler", static () => { });
            tracker.RegisterOperation(signaler, parent: null);
            tracker.SignalSynchronization(signaler, signal);
        }

        tracker.WaitSynchronization(operation, signal);
        Assert.Equal(clockSize, tracker.ClockSize(operation));
        RaceTracker.LocationState location =
            tracker.ResolveLocation(RaceMemoryLocationKind.StaticField, null, "Steady::Value", null)!;
        var source = new RaceSourceLocation("steady", 1, null, -1);
        var gate = new object();

        void Accesses()
        {
            tracker.RecordAccess(operation, RaceAccessKind.Read, location, source, []);
            tracker.RecordAccess(operation, RaceAccessKind.Write, location, source, []);
        }

        void LockSection()
        {
            tracker.EnterExclusive(operation, gate);
            tracker.ExitExclusive(operation, gate);
        }

        Assert.Equal(0, BytesPer(Accesses));

        // The entry allocates the holder's sorted array of held lock ids: one id.
        Assert.Equal(32, BytesPer(LockSection));

        static double BytesPer(Action step)
        {
            for (int index = 0; index < Iterations; index++)
            {
                step();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < Iterations; index++)
            {
                step();
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Iterations;
        }
    }

    private static Measurement Measure(int links)
    {
        using SimulationScheduler scheduler = SchedulerTestHarness.NewScheduler();
        var chain = new Chain(scheduler);
        long baseline = GC.GetTotalMemory(forceFullCollection: true);
        double early = 0;
        double late = 0;
        int window = links / 10;
        for (int link = 0; link < links; link++)
        {
            if (link % 100 == 0)
            {
                GC.Collect(0);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            chain.Link();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (link >= window && link < 2 * window)
            {
                early += allocated;
            }
            else if (link >= links - window)
            {
                late += allocated;
            }
        }

        chain.Finish();
        long retained = GC.GetTotalMemory(forceFullCollection: true) - baseline;
        GC.KeepAlive(chain);
        return new Measurement(early / window, late / window, retained, chain.Tracker.Statistics);
    }

    private sealed record Measurement(
        double EarlyBytesPerLink,
        double LateBytesPerLink,
        long RetainedBytes,
        RaceTrackerStatistics Statistics);

    // Drives the detector with operations the scheduler never registers, so the scheduler retains none of them.
    private sealed class Chain
    {
        private static readonly RaceSourceLocation Source = new("memory", 1, null, -1);

        private readonly SimulationScheduler _scheduler;
        private readonly object _gate = new();
        private readonly RaceTracker.LocationState _counter;
        private SimulationOperation _link;
        private long _nextOperationId;

        public Chain(SimulationScheduler scheduler)
        {
            _scheduler = scheduler;
            _counter = Tracker.ResolveLocation(RaceMemoryLocationKind.StaticField, null, "Memory::Counter", null)!;
            _link = Start(parent: null);
        }

        public RaceTracker Tracker { get; } = new();

        public void Link()
        {
            SimulationOperation helper = Start(_link);
            Increment(helper);
            ReadFreshObject(helper);
            Tracker.CompleteOperation(helper);
            Increment(_link);
            SimulationOperation next = Start(_link);
            Tracker.CompleteOperation(_link);
            _link = next;
        }

        public void Finish() => Tracker.CompleteOperation(_link);

        private SimulationOperation Start(SimulationOperation? parent)
        {
            var operation = new SimulationOperation(
                _scheduler,
                new SimulationOperationId(++_nextOperationId),
                parent?.Id ?? SimulationOperationId.None,
                _scheduler.Runtime,
                node: null,
                default(SimulationLogicalExecutionId),
                "memory",
                static () => { },
                capturedContext: null);
            Tracker.RegisterOperation(operation, parent);
            return operation;
        }

        private void Increment(SimulationOperation operation)
        {
            Tracker.EnterExclusive(operation, _gate);
            Tracker.RecordAccess(operation, RaceAccessKind.Read, _counter, Source, []);
            Tracker.RecordAccess(operation, RaceAccessKind.Write, _counter, Source, []);
            Tracker.ExitExclusive(operation, _gate);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ReadFreshObject(SimulationOperation operation)
        {
            RaceTracker.LocationState location =
                Tracker.ResolveLocation(RaceMemoryLocationKind.InstanceField, new object(), "Memory::Field", null)!;
            Tracker.RecordAccess(operation, RaceAccessKind.Read, location, Source, []);
        }
    }
}
