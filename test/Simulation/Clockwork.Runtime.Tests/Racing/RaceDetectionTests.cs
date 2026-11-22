using System.Runtime.CompilerServices;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;
using Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;
using Orleans.Testing.Simulation.Runtime.Tests.Scheduling;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Racing;

/// <summary>Behavioral coverage of logical-location conflict detection and false-positive controls.</summary>
public sealed class RaceDetectionTests
{
    [Fact]
    public void ReadReadAccessesAreClean()
    {
        using SimulationScheduler scheduler = RunPair(
            () => RaceInstrumentation.ReadStatic("Fx.State::Value", "A", 1, "A.cs", 10),
            () => RaceInstrumentation.ReadStatic("Fx.State::Value", "B", 2, "B.cs", 20));

        Assert.Null(scheduler.FirstRace);
    }

    [Theory]
    [InlineData(RaceAccessKind.Read, RaceAccessKind.Write)]
    [InlineData(RaceAccessKind.Write, RaceAccessKind.Read)]
    [InlineData(RaceAccessKind.Write, RaceAccessKind.Write)]
    public void ConflictingStaticAccessesAreDetected(RaceAccessKind first, RaceAccessKind second)
    {
        using SimulationScheduler scheduler = RunPair(
            () => AccessStatic(first, "A"),
            () => AccessStatic(second, "B"));

        RaceReport race = Assert.IsType<RaceReport>(scheduler.FirstRace);
        Assert.Equal(first, race.FirstAccess.Kind);
        Assert.Equal(second, race.SecondAccess.Kind);
        Assert.Equal("Fx.State::Value", race.FirstAccess.Location.Member);
        Assert.NotEqual(race.FirstAccess.OperationId, race.SecondAccess.OperationId);
        Assert.NotEmpty(race.ScheduleTrace);
    }

    [Fact]
    public void InstanceLocationsDistinguishTargets()
    {
        var first = new object();
        var second = new object();
        using SimulationScheduler scheduler = RunPair(
            () => RaceInstrumentation.WriteInstance(first, "Fx.State::Value", "A", 1, null, -1),
            () => RaceInstrumentation.WriteInstance(second, "Fx.State::Value", "B", 2, null, -1));

        Assert.Null(scheduler.FirstRace);
    }

    [Fact]
    public void SameInstanceFieldIsDetected()
    {
        var target = new object();
        using SimulationScheduler scheduler = RunPair(
            () => RaceInstrumentation.WriteInstance(target, "Fx.State::Value", "A", 1, null, -1),
            () => RaceInstrumentation.ReadInstance(target, "Fx.State::Value", "B", 2, null, -1));

        RaceReport race = Assert.IsType<RaceReport>(scheduler.FirstRace);
        Assert.Equal(RaceMemoryLocationKind.InstanceField, race.FirstAccess.Location.Kind);
        Assert.True(race.FirstAccess.Location.ObjectId > 0);
    }

    [Fact]
    public void ClosedGenericStaticFieldsUseDistinctLocations()
    {
        using SimulationScheduler scheduler = RunPair(
            () => RaceInstrumentation.WriteStaticField(
                typeof(GenericStatic<int>).TypeHandle,
                "GenericStatic`1::Value",
                "A",
                1,
                null,
                -1),
            () => RaceInstrumentation.WriteStaticField(
                typeof(GenericStatic<string>).TypeHandle,
                "GenericStatic`1::Value",
                "B",
                2,
                null,
                -1));

        Assert.Null(scheduler.FirstRace);
    }

    [Fact]
    public void ArrayElementsAreTrackedIndependently()
    {
        int[] values = new int[2];
        using SimulationScheduler clean = RunPair(
            () => RaceInstrumentation.WriteArray(values, 0, "A", 1, null, -1),
            () => RaceInstrumentation.WriteArray(values, 1, "B", 2, null, -1));
        Assert.Null(clean.FirstRace);

        using SimulationScheduler raced = RunPair(
            () => RaceInstrumentation.WriteArray(values, 0, "A", 1, null, -1),
            () => RaceInstrumentation.ReadArray(values, 0, "B", 2, null, -1));
        RaceReport race = Assert.IsType<RaceReport>(raced.FirstRace);
        Assert.Equal(RaceMemoryLocationKind.ArrayElement, race.FirstAccess.Location.Kind);
        Assert.Equal(0, race.FirstAccess.Location.ElementIndex);
    }

    [Fact]
    public void MutableCollectionAccessesRaceButConcurrentCollectionPointsDoNot()
    {
        var mutable = new List<int>();
        using SimulationScheduler raced = RunPair(
            () => RaceInstrumentation.WriteCollection(mutable, "List::Add", "A", 1, null, -1),
            () => RaceInstrumentation.ReadCollection(mutable, "List::GetEnumerator", "B", 2, null, -1));
        Assert.Equal(RaceMemoryLocationKind.Collection, Assert.IsType<RaceReport>(raced.FirstRace).FirstAccess.Location.Kind);

        var concurrent = new System.Collections.Concurrent.ConcurrentQueue<int>();
        using SimulationScheduler clean = RunPair(
            () => RaceInstrumentation.InterleaveConcurrentCollection(concurrent, "ConcurrentQueue::Enqueue", "A", 1, null, -1),
            () => RaceInstrumentation.InterleaveConcurrentCollection(concurrent, "ConcurrentQueue::TryDequeue", "B", 2, null, -1));
        Assert.Null(clean.FirstRace);
    }

    [Fact]
    public void SharedControlledLockOrdersProtectedAccesses()
    {
        var synchronization = new object();
        using SimulationScheduler scheduler = RunPair(
            () => ProtectedWrite(synchronization),
            () => ProtectedWrite(synchronization));

        Assert.Null(scheduler.FirstRace);
    }

    [Fact]
    public void DifferentLocksDoNotHideRaceAndReportSynchronizationContext()
    {
        var firstLock = new object();
        var secondLock = new object();
        using SimulationScheduler scheduler = RunPair(
            () => ProtectedWrite(firstLock),
            () => ProtectedWrite(secondLock));

        RaceReport race = Assert.IsType<RaceReport>(scheduler.FirstRace);
        Assert.Equal(["sync#1"], race.FirstAccess.SynchronizationContext);
        Assert.Equal(["sync#2"], race.SecondAccess.SynchronizationContext);
        Assert.Contains("synchronization:", race.ToDetailedString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SignalWaitCreatesHappensBeforeEdge()
    {
        var signal = new object();
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.Schedule("producer", () =>
        {
            RaceInstrumentation.WriteStatic("Fx.State::Value", "producer", 1, null, -1);
            RaceSynchronization.Signal(signal);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.Schedule("consumer", () =>
        {
            RaceSynchronization.Wait(signal);
            RaceInstrumentation.ReadStatic("Fx.State::Value", "consumer", 2, null, -1);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Null(scheduler.FirstRace);
    }

    [Fact]
    public void ControlledTaskCompletionCreatesHappensBeforeEdge()
    {
        TaskCompletionSource? completion = null;
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.Schedule("producer", () =>
        {
            completion = new TaskCompletionSource();
            RaceInstrumentation.WriteStatic("Fx.State::Value", "producer", 1, null, -1);
            ControlledTaskCompletionSource.SetResult(completion);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.Schedule("consumer", () =>
        {
            ControlledTask.Wait(completion!.Task);
            RaceInstrumentation.ReadStatic("Fx.State::Value", "consumer", 2, null, -1);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Null(scheduler.FirstRace);
    }

    [Fact]
    public void ControlledTaskAwaitConsumesCompletionHappensBeforeEdge()
    {
        TaskCompletionSource? completion = null;
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.Schedule("producer", () =>
        {
            completion = new TaskCompletionSource();
            RaceInstrumentation.WriteStatic("Fx.State::Value", "producer", 1, null, -1);
            ControlledTaskCompletionSource.SetResult(completion);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.Schedule("consumer", () =>
        {
            new ControlledTaskAwaiter(completion!.Task).GetResult();
            RaceInstrumentation.ReadStatic("Fx.State::Value", "consumer", 2, null, -1);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Null(scheduler.FirstRace);
    }

    [Fact]
    public void ControlledTaskProxyPropagatesAntecedentHappensBeforeEdges()
    {
        TaskCompletionSource? first = null;
        TaskCompletionSource? second = null;
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.Schedule("producer", () =>
        {
            first = new TaskCompletionSource();
            second = new TaskCompletionSource();
            RaceInstrumentation.WriteStatic("Fx.State::Value", "producer", 1, null, -1);
            ControlledTaskCompletionSource.SetResult(first);
            ControlledTaskCompletionSource.SetResult(second);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.Schedule("consumer", () =>
        {
            Task proxy = ControlledTask.WhenAll(first!.Task, second!.Task);
            new ControlledTaskAwaiter(proxy).GetResult();
            RaceInstrumentation.ReadStatic("Fx.State::Value", "consumer", 2, null, -1);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Null(scheduler.FirstRace);
    }

    [Fact]
    public void SharedReaderLocksDoNotSuppressWriteRace()
    {
        ReaderWriterLockSlim? rwLock = null;
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.Schedule("setup", () => rwLock = ControlledReaderWriterLockSlim.Create());
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.Schedule("first", () => WriteUnderReadLock(rwLock!));
        scheduler.Schedule("second", () => WriteUnderReadLock(rwLock!));
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.NotNull(scheduler.FirstRace);
    }

    [Fact]
    public void ReaderReleasesAggregateBeforeWriterAcquires()
    {
        ReaderWriterLockSlim? rwLock = null;
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.Schedule("setup", () => rwLock = ControlledReaderWriterLockSlim.Create());
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.Schedule("first-reader", () => ReadUnderReadLock(rwLock!));
        scheduler.Schedule("second-reader", () => ReadUnderReadLock(rwLock!));
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.Schedule("writer", () =>
        {
            ControlledReaderWriterLockSlim.EnterWriteLock(rwLock!);
            try
            {
                RaceInstrumentation.WriteStatic("Fx.State::Value", "writer", 3, null, -1);
            }
            finally
            {
                ControlledReaderWriterLockSlim.ExitWriteLock(rwLock!);
            }
        });
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Null(scheduler.FirstRace);
    }

    [Fact]
    public void LockAndSignalOnSameObjectUseDifferentSynchronizationDomains()
    {
        Task synchronization = Task.CompletedTask;
        using SimulationScheduler scheduler = RunPair(
            () =>
            {
                RaceSynchronization.EnterExclusive(synchronization);
                try
                {
                    RaceInstrumentation.WriteStatic("Fx.State::Value", "lock", 1, null, -1);
                }
                finally
                {
                    RaceSynchronization.ExitExclusive(synchronization);
                }
            },
            () =>
            {
                RaceSynchronization.Wait(synchronization);
                RaceInstrumentation.ReadStatic("Fx.State::Value", "task", 2, null, -1);
            });

        Assert.NotNull(scheduler.FirstRace);
    }

    [Fact]
    public void ImmediateAsyncSemaphoreWaitPropagatesReleaseClock()
    {
        SemaphoreSlim? semaphore = null;
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.Schedule("setup", () => semaphore = ControlledSemaphoreSlim.Create(0, 1));
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.Schedule("producer", () =>
        {
            RaceInstrumentation.WriteStatic("Fx.State::Value", "producer", 1, null, -1);
            ControlledSemaphoreSlim.Release(semaphore!);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.Schedule("consumer", () =>
        {
            Task<bool> wait = ControlledSemaphoreSlim.WaitAsync(semaphore!, Timeout.Infinite);
            Assert.True(new ControlledTaskAwaiter<bool>(wait).GetResult());
            RaceInstrumentation.ReadStatic("Fx.State::Value", "consumer", 2, null, -1);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Null(scheduler.FirstRace);
    }

    [Fact]
    public void SchedulerAndNestedStrandIdentitiesUseDisjointRanges()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        long schedulerStrand = 0;
        long nestedStrand = 0;
        scheduler.Schedule("identity", () =>
        {
            schedulerStrand = SimulationSynchronizationFlow.CurrentId;
            SimulationSynchronizationFlow.RunAsNewStrand(
                () => nestedStrand = SimulationSynchronizationFlow.CurrentId);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.True(schedulerStrand < 0);
        Assert.True(nestedStrand > 0);
    }

    [Fact]
    public void ControlledResourceSignalCreatesHappensBeforeEdge()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(
            SimulationResourceKind.ManualResetEvent,
            "race-event");
        scheduler.Schedule("consumer", () =>
        {
            scheduler.WaitOnResource(resource, SimulationPauseReason.ResourceWait("race-event"));
            RaceInstrumentation.ReadStatic("Fx.State::Value", "consumer", 2, null, -1);
        });
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        scheduler.Schedule("producer", () =>
        {
            RaceInstrumentation.WriteStatic("Fx.State::Value", "producer", 1, null, -1);
            scheduler.SignalOne(resource);
        });
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Null(scheduler.FirstRace);
    }

    [Fact]
    public void ResourceWaitConsumesClockFromItsResolvingSignal()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.Priority();
        var resource = scheduler.CreateResource(
            SimulationResourceKind.ManualResetEvent,
            "clocked-event");
        scheduler.Schedule("consumer", () =>
        {
            scheduler.WaitOnResource(resource, SimulationPauseReason.ResourceWait("clocked-event"));
            RaceInstrumentation.ReadStatic("Fx.State::Y", "consumer", 4, null, -1);
        });
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));

        scheduler.Schedule("first-signal", () => scheduler.SignalOne(resource), priority: 5);
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));

        scheduler.Schedule("later-signal", () =>
        {
            RaceInstrumentation.WriteStatic("Fx.State::Y", "later", 3, null, -1);
            scheduler.SignalOne(resource);
        }, priority: 10);
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.NotNull(scheduler.FirstRace);
    }

    [Fact]
    public void CanceledOperationUnwoundLockExitOrdersTheNextHolder()
    {
        var synchronization = new object();
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(
            SimulationResourceKind.ManualResetEvent,
            "never-set");
        SimulationOperation parked = scheduler.Schedule("parked", () =>
        {
            RaceSynchronization.EnterExclusive(synchronization);
            try
            {
                RaceInstrumentation.WriteStatic("Fx.State::Value", "parked", 1, null, -1);
                scheduler.WaitOnResource(resource, SimulationPauseReason.ResourceWait("never-set"));
            }
            finally
            {
                RaceSynchronization.ExitExclusive(synchronization);
            }
        });
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));

        scheduler.Cancel(parked);
        scheduler.Schedule("next", () => ProtectedWrite(synchronization));
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Null(scheduler.FirstRace);
        Assert.Equal(0, scheduler.RaceTrackerStatistics.Operations);
    }

    [Fact]
    public void DisposeEndsTheRaceTrackingOfEveryOperationAfterItUnwinds()
    {
        var synchronization = new object();
        var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.ManualResetEvent, "never-set");
        scheduler.Schedule("parked", () =>
        {
            RaceSynchronization.EnterExclusive(synchronization);
            try
            {
                scheduler.WaitOnResource(resource, SimulationPauseReason.ResourceWait("never-set"));
            }
            finally
            {
                RaceSynchronization.ExitExclusive(synchronization);
            }
        });
        scheduler.Schedule("never-started", static () => { });
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));

        scheduler.Dispose();

        Assert.Equal(0, scheduler.RaceTrackerStatistics.Operations);
    }

    // Where an operation runs as another's flow (a started task constructed without the constructor rules runs as its
    // creator's), it exits the lock its flow holds: the exit publishes both the holder's clock, at the holder's end, and
    // its own, so the next entry is ordered after both operations' accesses.
    [Fact]
    public void LockExitedByAnotherOperationOfItsFlowPublishesBothClocks()
    {
        using var tracker = new TrackerHarness();
        SimulationOperation holder = tracker.Start();
        SimulationOperation exiter = tracker.Start();
        SimulationOperation next = tracker.Start();

        tracker.Tracker.EnterExclusive(holder, tracker.Lock);
        tracker.Write(holder, "Fx.State::First");
        tracker.Tracker.CompleteOperation(holder);
        tracker.Write(exiter, "Fx.State::Second");
        tracker.Tracker.ExitExclusive(exiter, tracker.Lock);
        tracker.Tracker.EnterExclusive(next, tracker.Lock);

        Assert.Null(tracker.Read(next, "Fx.State::First"));
        Assert.Null(tracker.Read(next, "Fx.State::Second"));
        Assert.Null(tracker.Tracker.FirstRace);
    }

    // An operation which enters a lock another running operation of its flow holds still acquires the releases published
    // before that hold, so another flow's earlier accesses under the lock are ordered before its own.
    [Fact]
    public void ConcurrentHolderOfItsFlowsLockAcquiresEarlierReleases()
    {
        using var tracker = new TrackerHarness();
        SimulationOperation earlier = tracker.Start();
        SimulationOperation creator = tracker.Start();

        tracker.Tracker.EnterExclusive(earlier, tracker.Lock);
        tracker.Write(earlier, "Fx.State::Value");
        tracker.Tracker.ExitExclusive(earlier, tracker.Lock);
        SimulationOperation started = tracker.Fork(creator);
        tracker.Tracker.EnterExclusive(creator, tracker.Lock);
        tracker.Tracker.EnterExclusive(started, tracker.Lock);

        Assert.Null(tracker.Read(started, "Fx.State::Value"));
        Assert.Null(tracker.Tracker.FirstRace);
    }

    // An inner exit of a recursive hold releases nothing while the hold lasts, so a concurrent holder of the flow does not
    // acquire the holder's accesses through it.
    [Fact]
    public void InnerExitOfARecursiveHoldPublishesNothingToAConcurrentHolder()
    {
        using var tracker = new TrackerHarness();
        SimulationOperation holder = tracker.Start();
        SimulationOperation concurrent = tracker.Fork(holder);

        tracker.Tracker.EnterExclusive(holder, tracker.Lock);
        tracker.Tracker.EnterExclusive(holder, tracker.Lock);
        tracker.Write(holder, "Fx.State::Value");
        tracker.Tracker.ExitExclusive(holder, tracker.Lock);
        tracker.Tracker.EnterExclusive(concurrent, tracker.Lock);

        Assert.NotNull(tracker.Read(concurrent, "Fx.State::Value"));
    }

    // Once the flow's hold ends, its releases, held back while it lasted, reach the next acquisition.
    [Fact]
    public void ReleasesHeldBackDuringAHoldReachTheNextAcquisition()
    {
        using var tracker = new TrackerHarness();
        SimulationOperation holder = tracker.Start();
        SimulationOperation concurrent = tracker.Fork(holder);
        SimulationOperation next = tracker.Start();

        tracker.Tracker.EnterExclusive(holder, tracker.Lock);
        tracker.Tracker.EnterExclusive(concurrent, tracker.Lock);
        tracker.Write(concurrent, "Fx.State::Second");
        tracker.Tracker.ExitExclusive(concurrent, tracker.Lock);
        tracker.Write(holder, "Fx.State::First");
        tracker.Tracker.ExitExclusive(holder, tracker.Lock);
        tracker.Tracker.EnterExclusive(next, tracker.Lock);

        Assert.Null(tracker.Read(next, "Fx.State::First"));
        Assert.Null(tracker.Read(next, "Fx.State::Second"));
    }

    // A flow which re-enters its lock after the operation holding it ended acquires that operation's clock, as a thread
    // re-entering a lock it holds follows its own earlier accesses.
    [Fact]
    public void FlowReenteringItsLockAfterItsHolderEndedIsOrderedAfterTheHolder()
    {
        using var tracker = new TrackerHarness();
        SimulationOperation holder = tracker.Start();
        SimulationOperation continuation = tracker.Start();

        tracker.Tracker.EnterExclusive(holder, tracker.Lock);
        tracker.Write(holder, "Fx.State::Value");
        tracker.Tracker.CompleteOperation(holder);
        tracker.Tracker.EnterExclusive(continuation, tracker.Lock);

        Assert.Null(tracker.Read(continuation, "Fx.State::Value"));
        Assert.Null(tracker.Tracker.FirstRace);
    }

    // Two operations of one flow which hold its lock at once are not ordered by it, so their accesses race, as the
    // unconstrained program's would; both report the lock as held.
    [Fact]
    public void OperationsHoldingALockAtOnceAsOneFlowAreUnorderedByIt()
    {
        using var tracker = new TrackerHarness();
        SimulationOperation creator = tracker.Start();
        SimulationOperation started = tracker.Start();

        tracker.Tracker.EnterExclusive(creator, tracker.Lock);
        tracker.Write(creator, "Fx.State::Value");
        tracker.Tracker.EnterExclusive(started, tracker.Lock);

        Assert.NotNull(tracker.Read(started, "Fx.State::Value"));
        RaceReport race = Assert.IsType<RaceReport>(tracker.Tracker.FirstRace);
        Assert.Equal(["sync#1"], race.FirstAccess.SynchronizationContext);
        Assert.Equal(["sync#1"], race.SecondAccess.SynchronizationContext);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("signal")]
    [InlineData("task")]
    public void SynchronizationStateIsFreedWithItsObject(string kind)
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        WeakReference? weak = null;
        scheduler.Schedule("synchronize", () => weak = SynchronizeOnTemporaryObject(kind));
        scheduler.Drain(TestContext.Current.CancellationToken);

        ForceCollection();

        Assert.NotNull(weak);
        Assert.False(weak.IsAlive);
    }

    [Fact]
    public void TrackerDoesNotRetainTargetObjectsAfterOperationCompletes()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        WeakReference? weak = null;
        scheduler.Schedule("access", () => weak = AccessTemporaryTarget());
        scheduler.Drain(TestContext.Current.CancellationToken);

        ForceCollection();

        Assert.NotNull(weak);
        Assert.False(weak!.IsAlive);
    }

    [Fact]
    public void SameSeedProducesByteStableFirstRaceReport()
    {
        string first = RunReport(seed: 19);
        string replay = RunReport(seed: 19);
        Assert.Equal(first, replay);
    }

    private static SimulationScheduler RunPair(Action first, Action second)
    {
        var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.Schedule("first", first);
        scheduler.Schedule("second", second);
        scheduler.Drain(TestContext.Current.CancellationToken);
        return scheduler;
    }

    private static void AccessStatic(RaceAccessKind kind, string method)
    {
        if (kind == RaceAccessKind.Read)
        {
            RaceInstrumentation.ReadStatic("Fx.State::Value", method, 1, method + ".cs", 10);
        }
        else
        {
            RaceInstrumentation.WriteStatic("Fx.State::Value", method, 1, method + ".cs", 10);
        }
    }

    private static void ProtectedWrite(object synchronization)
    {
        RaceSynchronization.EnterExclusive(synchronization);
        try
        {
            RaceInstrumentation.WriteStatic("Fx.State::Value", "protected", 1, null, -1);
        }
        finally
        {
            RaceSynchronization.ExitExclusive(synchronization);
        }
    }

    private static void WriteUnderReadLock(ReaderWriterLockSlim rwLock)
    {
        ControlledReaderWriterLockSlim.EnterReadLock(rwLock);
        try
        {
            RaceInstrumentation.WriteStatic("Fx.State::Value", "reader", 1, null, -1);
        }
        finally
        {
            ControlledReaderWriterLockSlim.ExitReadLock(rwLock);
        }
    }

    private static void ReadUnderReadLock(ReaderWriterLockSlim rwLock)
    {
        ControlledReaderWriterLockSlim.EnterReadLock(rwLock);
        try
        {
            RaceInstrumentation.ReadStatic("Fx.State::Value", "reader", 1, null, -1);
        }
        finally
        {
            ControlledReaderWriterLockSlim.ExitReadLock(rwLock);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AccessTemporaryTarget()
    {
        var target = new object();
        var weak = new WeakReference(target);
        RaceInstrumentation.ReadInstance(target, "Fx.State::Value", "temporary", 1, null, -1);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SynchronizeOnTemporaryObject(string kind)
    {
        switch (kind)
        {
            case "lock":
                var gate = new object();
                RaceSynchronization.EnterExclusive(gate);
                RaceSynchronization.ExitExclusive(gate);
                return new WeakReference(gate);
            case "signal":
                var signal = new object();
                RaceSynchronization.Signal(signal);
                RaceSynchronization.Wait(signal);
                return new WeakReference(signal);
            default:
                var completion = new TaskCompletionSource();
                ControlledTaskCompletionSource.SetResult(completion);
                ControlledTask.Wait(completion.Task);
                return new WeakReference(completion.Task);
        }
    }

    private static void ForceCollection()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static string RunReport(int seed)
    {
        using var scheduler = SchedulerTestHarness.NewScheduler(seed: seed);
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.SeededRandom(seed);
        scheduler.Schedule("first", () => AccessStatic(RaceAccessKind.Write, "A"));
        scheduler.Schedule("second", () => AccessStatic(RaceAccessKind.Read, "B"));
        scheduler.Drain(TestContext.Current.CancellationToken);
        return Assert.IsType<RaceReport>(scheduler.FirstRace).ToDetailedString();
    }

    // Drives a race tracker directly with operations of a scheduler which never runs them.
    private sealed class TrackerHarness : IDisposable
    {
        private readonly SimulationScheduler _scheduler = SchedulerTestHarness.NewScheduler();
        private int _nextOffset;

        public RaceTracker Tracker { get; } = new();

        public object Lock { get; } = new();

        public SimulationOperation Start() => Fork(parent: null);

        public SimulationOperation Fork(SimulationOperation? parent)
        {
            SimulationOperation operation = _scheduler.Register("tracked", static () => { });
            Tracker.RegisterOperation(operation, parent);
            return operation;
        }

        public RaceTracker.RacingAccess? Write(SimulationOperation operation, string member) =>
            Record(operation, RaceAccessKind.Write, member);

        public RaceTracker.RacingAccess? Read(SimulationOperation operation, string member) =>
            Record(operation, RaceAccessKind.Read, member);

        public void Dispose() => _scheduler.Dispose();

        private RaceTracker.RacingAccess? Record(SimulationOperation operation, RaceAccessKind kind, string member) =>
            Tracker.RecordAccess(
                operation,
                kind,
                Tracker.ResolveLocation(RaceMemoryLocationKind.StaticField, null, member, null)!,
                new RaceSourceLocation("tracked", ++_nextOffset, null, -1),
                []);
    }

    private static class GenericStatic<T>
    {
    }
}
