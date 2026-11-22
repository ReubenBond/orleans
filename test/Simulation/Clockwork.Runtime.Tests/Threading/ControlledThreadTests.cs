using System.Threading;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

/// <summary>
/// Tests for the controlled <see cref="ControlledThread"/> shims: a controlled thread is a real
/// <see cref="Thread"/> object whose body is queued as a fresh controlled operation, <c>Join</c> pumps
/// the deterministic loop instead of blocking, the static <c>Sleep</c>/<c>Yield</c>/<c>SpinWait</c> hints
/// are cooperative no-ops that never consume real time, and the OS-specific surface (priority, apartment
/// state, interrupt) is rejected precisely.
/// </summary>
public sealed class ControlledThreadTests
{
    [Fact]
    public void StartQueuesBodyAndJoinObservesCompletion()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ran = false;
            var thread = ControlledThread.Create(() => ran = true);

            // The body is queued as controlled work, not run inline by Start.
            ControlledThread.Start(thread);
            Assert.False(ran);

            // Join pumps the deterministic loop until the body completes rather than blocking a thread.
            ControlledThread.Join(thread);
            Assert.True(ran);
        });
    }

    [Fact]
    public void StartOfParameterizedThreadPassesArgument()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            object? captured = null;
            var thread = ControlledThread.Create((ParameterizedThreadStart)(o => captured = o));

            ControlledThread.Start(thread, "payload");
            ControlledThread.Join(thread);

            Assert.Equal("payload", captured);
        });
    }

    [Fact]
    public void MultipleJoinsAllObserveCompletion()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var count = 0;
            var thread = ControlledThread.Create(() => count++);

            ControlledThread.Start(thread);
            ControlledThread.Join(thread);
            ControlledThread.Join(thread);
            ControlledThread.Join(thread);

            // The body runs exactly once; every Join simply observes the already-terminated thread.
            Assert.Equal(1, count);
        });
    }

    [Fact]
    public void JoinWithTimeoutReturnsTrueOnCompletion()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ran = false;
            var thread = ControlledThread.Create(() => ran = true);
            ControlledThread.Start(thread);

            Assert.True(ControlledThread.Join(thread, 1000));
            Assert.True(ControlledThread.Join(thread, TimeSpan.FromSeconds(1)));
            Assert.True(ran);
        });
    }

    [Fact]
    public void BodyExceptionFaultsItsOperationAndJoinObservesTheTerminatedThread()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var reached = false;
            var thread = ControlledThread.Create(() =>
            {
                reached = true;
                throw new InvalidOperationException("boom");
            });

            ControlledThread.Start(thread);

            // As in .NET, the exception is unhandled: it faults the thread's controlled operation (a
            // replayed run is Faulted). Join never surfaces a thread's exception; it observes the
            // terminated thread without throwing or hanging.
            ControlledThread.Join(thread);
            Assert.True(reached);
            Assert.True(ControlledThread.Join(thread, 0));
            Assert.Contains(
                coordinator.Scheduler.CaptureStatus(),
                static status => status.State == Runtime.Scheduling.SimulationOperationState.Faulted);
        });
    }

    [Fact]
    public void StartedThreadRejectsSecondStart()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var thread = ControlledThread.Create(() => { });
            ControlledThread.Start(thread);

            Assert.Throws<ThreadStateException>(() => ControlledThread.Start(thread));
        });
    }

    [Fact]
    public void StartOfUnregisteredThreadIsRejected()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            // A thread not created through the controlled surface has an unknown body; starting the real OS
            // thread would escape the single logical thread, so it is rejected precisely.
            var raw = new Thread(() => { });
            Assert.Throws<SimulationApiException>(() => ControlledThread.Start(raw));
        });
    }

    [Fact]
    public void SleepYieldAndSpinWaitAreCooperativeNoOps()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            // None of these block or consume real time inside a simulation.
            ControlledThread.Sleep(10_000);
            ControlledThread.Sleep(TimeSpan.FromHours(1));
            ControlledThread.SpinWait(1_000_000);
            Assert.False(ControlledThread.Yield());
        });
    }

    [Fact]
    public void SetPriorityIsRejectedUnderSimulation()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var thread = ControlledThread.Create(() => { });
            var ex = Assert.Throws<SimulationApiException>(
                () => ControlledThread.SetPriority(thread, ThreadPriority.Highest));
            Assert.Contains("set_Priority", ex.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void InterruptIsRejectedUnderSimulation()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var thread = ControlledThread.Create(() => { });
            Assert.Throws<SimulationApiException>(() => ControlledThread.Interrupt(thread));
        });
    }

    [Fact]
    public void ApartmentStateIsRejectedUnderSimulation()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var thread = ControlledThread.Create(() => { });
            Assert.Throws<SimulationApiException>(
                () => ControlledThread.SetApartmentState(thread, ApartmentState.STA));
            Assert.Throws<SimulationApiException>(
                () => ControlledThread.TrySetApartmentState(thread, ApartmentState.STA));
            var property = Assert.Throws<SimulationApiException>(
                () => ControlledThread.SetApartmentStateProperty(thread, ApartmentState.STA));
            Assert.Contains("set_ApartmentState", property.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CreatePreservesLogicalIdentitySurface()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var thread = ControlledThread.Create(() => { });
            thread.Name = "worker";
            thread.IsBackground = true;

            // The controlled thread is a real Thread object, so its logical identity keeps working.
            Assert.Equal("worker", thread.Name);
            Assert.True(thread.IsBackground);
            Assert.True(thread.ManagedThreadId > 0);
        });
    }

    [Fact]
    public void OutsideSimulationThreadApisFailBeforeCreatingOrRunningAThread()
    {
        Thread? thread = null;
        var ran = false;

        Exception? createException = Record.Exception(
            () => thread = ControlledThread.Create(() => ran = true));

        Assert.Null(thread);
        Assert.False(ran);
        SimulationNotActiveExceptionAssert.Equal(
            createException,
            "System.Threading.Thread..ctor");

        Exception? yieldException = Record.Exception(() => ControlledThread.Yield());
        SimulationNotActiveExceptionAssert.Equal(
            yieldException,
            "System.Threading.Thread.Yield");
    }

    [Fact]
    public void JoinOnUnstartedThreadThrowsThreadStateException()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var thread = ControlledThread.Create(() => { });

            var exception = Assert.Throws<ThreadStateException>(() => ControlledThread.Join(thread));

            Assert.IsType<ThreadStateException>(exception);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public void JoinIntegerRejectsValuesLessThanInfinite(int millisecondsTimeout)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var thread = ControlledThread.Create(() => { });
            ControlledThread.Start(thread);
            ControlledThread.Join(thread);

            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => ControlledThread.Join(thread, millisecondsTimeout));

            Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.Equal("millisecondsTimeout", exception.ParamName);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Theory]
    [InlineData(-2L)]
    [InlineData((long)int.MaxValue + 1)]
    public void JoinTimeSpanRejectsOutOfRangeValues(long milliseconds)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var thread = ControlledThread.Create(() => { });
            ControlledThread.Start(thread);
            ControlledThread.Join(thread);

            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => ControlledThread.Join(thread, TimeSpan.FromMilliseconds(milliseconds)));

            Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.Equal("timeout", exception.ParamName);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Fact]
    public void JoinZeroDoesNotPumpPendingThreadAndReportsCompletedThread()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var runs = 0;
            var thread = ControlledThread.Create(() => runs++);
            ControlledThread.Start(thread);

            Assert.False(ControlledThread.Join(thread, 0));
            Assert.Equal(0, runs);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            Assert.Equal(1, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.False(coordinator.Scheduler.IsIdle);

            Assert.Equal(1, coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken));
            Assert.Equal(1, runs);
            Assert.True(ControlledThread.Join(thread, 0));
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiniteJoinCompletingBeforeDeadlineReturnsTrue(bool useTimeSpan)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            TimeSpan? completedAt = null;
            var thread = ControlledThread.Create(() => completedAt = coordinator.Scheduler.VirtualTime);
            ControlledThread.Start(thread);

            var joined = useTimeSpan
                ? ControlledThread.Join(thread, TimeSpan.FromMilliseconds(100))
                : ControlledThread.Join(thread, 100);

            Assert.True(joined);
            Assert.Equal(TimeSpan.Zero, completedAt);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiniteJoinTimesOutAtItsVirtualDeadline(bool useTimeSpan)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var thread = ControlledThread.Create(() =>
            {
                ControlledThread.Sleep(Timeout.Infinite);
            });
            ControlledThread.Start(thread);

            var joined = useTimeSpan
                ? ControlledThread.Join(thread, TimeSpan.FromMilliseconds(100))
                : ControlledThread.Join(thread, 100);

            Assert.False(joined);
            Assert.Equal(TimeSpan.FromMilliseconds(100), coordinator.Scheduler.VirtualTime);
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(1, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.False(coordinator.Scheduler.IsIdle);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InfiniteJoinOverloadsWaitWithoutRegisteringADeadline(bool useTimeSpan)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            TimeSpan? deadlineSeenByBody = TimeSpan.MinValue;
            var thread = ControlledThread.Create(
                () => deadlineSeenByBody = coordinator.Scheduler.NextTimerDue);
            ControlledThread.Start(thread);

            var joined = useTimeSpan
                ? ControlledThread.Join(thread, Timeout.InfiniteTimeSpan)
                : ControlledThread.Join(thread, Timeout.Infinite);

            Assert.True(joined);
            Assert.Null(deadlineSeenByBody);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Theory]
    [InlineData(false, 125)]
    [InlineData(true, 250)]
    public void PositiveSleepAdvancesToExactVirtualTimestamp(bool useTimeSpan, int milliseconds)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var before = coordinator.Scheduler.VirtualTime;

            if (useTimeSpan)
            {
                ControlledThread.Sleep(TimeSpan.FromMilliseconds(milliseconds));
            }
            else
            {
                ControlledThread.Sleep(milliseconds);
            }

            Assert.Equal(TimeSpan.Zero, before);
            Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Fact]
    public void PositiveSleepRunsReadyWorkBeforeAdvancingToWakeDeadline()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var order = new List<string>();
            var timestamps = new List<TimeSpan>();
            coordinator.Scheduler.Schedule(() =>
            {
                order.Add("other-work");
                timestamps.Add(coordinator.Scheduler.VirtualTime);
            });

            ControlledThread.Sleep(100);
            order.Add("sleeper-resume");
            timestamps.Add(coordinator.Scheduler.VirtualTime);

            Assert.Equal(["other-work", "sleeper-resume"], order);
            Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(100) }, timestamps);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroSleepYieldsWithoutAdvancingVirtualTime(bool useTimeSpan)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var order = new List<string>();
            var timestamps = new List<TimeSpan>();
            coordinator.Scheduler.Schedule(() =>
            {
                order.Add("ready-work");
                timestamps.Add(coordinator.Scheduler.VirtualTime);
            });

            if (useTimeSpan)
            {
                ControlledThread.Sleep(TimeSpan.Zero);
            }
            else
            {
                ControlledThread.Sleep(0);
            }

            order.Add("sleep-return");
            timestamps.Add(coordinator.Scheduler.VirtualTime);

            Assert.Equal(["ready-work", "sleep-return"], order);
            Assert.All(timestamps, timestamp => Assert.Equal(TimeSpan.Zero, timestamp));
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Fact]
    public void YieldRunsOneReadyOperationWithoutAdvancingVirtualTime()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var order = new List<string>();
            coordinator.Scheduler.Schedule(() => order.Add("ready-work"));

            var yieldedWithReadyWork = ControlledThread.Yield();
            order.Add("yield-return");
            var yieldedWithoutReadyWork = ControlledThread.Yield();

            Assert.True(yieldedWithReadyWork);
            Assert.False(yieldedWithoutReadyWork);
            Assert.Equal(["ready-work", "yield-return"], order);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public void SleepIntegerRejectsValuesLessThanInfinite(int millisecondsTimeout)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => ControlledThread.Sleep(millisecondsTimeout));

            Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.Equal("millisecondsTimeout", exception.ParamName);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Theory]
    [InlineData(-2L)]
    [InlineData((long)int.MaxValue + 1)]
    public void SleepTimeSpanRejectsOutOfRangeValues(long milliseconds)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => ControlledThread.Sleep(TimeSpan.FromMilliseconds(milliseconds)));

            Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.Equal("timeout", exception.ParamName);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InfiniteSleepParksWithoutADeadlineAndAllowsOtherWorkToRun(bool useTimeSpan)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sleeperEntered = false;
            var sleeperResumed = false;
            var followerRan = false;
            TimeSpan? followerTimestamp = null;
            var sleeper = ControlledThread.Create(() =>
            {
                sleeperEntered = true;
                if (useTimeSpan)
                {
                    ControlledThread.Sleep(Timeout.InfiniteTimeSpan);
                }
                else
                {
                    ControlledThread.Sleep(Timeout.Infinite);
                }

                sleeperResumed = true;
            });
            var follower = ControlledThread.Create(() =>
            {
                followerRan = true;
                followerTimestamp = coordinator.Scheduler.VirtualTime;
            });

            ControlledThread.Start(sleeper);
            ControlledThread.Start(follower);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.True(sleeperEntered);
            Assert.False(sleeperResumed);
            Assert.True(followerRan);
            Assert.Equal(TimeSpan.Zero, followerTimestamp);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(1, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.False(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void ThreadStartRejectsStartWithParameter()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var runs = 0;
            var thread = ControlledThread.Create((ThreadStart)(() => runs++));

            var exception = Assert.Throws<InvalidOperationException>(
                () => ControlledThread.Start(thread, new object()));

            Assert.IsType<InvalidOperationException>(exception);
            Assert.Equal(0, runs);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);

            ControlledThread.Start(thread);
            Assert.Equal(1, coordinator.Scheduler.RunnableOperationCount);
            ControlledThread.Join(thread);

            Assert.Equal(1, runs);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    private static void AssertLoopIsEmpty(SimulationSchedulerTestHost coordinator)
    {
        Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
        Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
        Assert.Null(coordinator.Scheduler.NextTimerDue);
        Assert.True(coordinator.Scheduler.IsIdle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiniteJoinWaitsForDelayedCompletionBeforeItsDeadline(bool useTimeSpan)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            TimeSpan? completedAt = null;
            var thread = ControlledThread.Create(() =>
            {
                ControlledThread.Sleep(50);
                completedAt = coordinator.Scheduler.VirtualTime;
            });
            ControlledThread.Start(thread);

            var joined = useTimeSpan
                ? ControlledThread.Join(thread, TimeSpan.FromMilliseconds(100))
                : ControlledThread.Join(thread, 100);

            Assert.True(joined);
            Assert.Equal(TimeSpan.FromMilliseconds(50), completedAt);
            Assert.Equal(TimeSpan.FromMilliseconds(50), coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothZeroJoinOverloadsLeavePendingBodyQueued(bool useTimeSpan)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var runs = 0;
            var thread = ControlledThread.Create(() => runs++);
            ControlledThread.Start(thread);

            var joined = useTimeSpan
                ? ControlledThread.Join(thread, TimeSpan.Zero)
                : ControlledThread.Join(thread, 0);

            Assert.False(joined);
            Assert.Equal(0, runs);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            Assert.Equal(1, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.False(coordinator.Scheduler.IsIdle);

            Assert.Equal(1, coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken));
            Assert.Equal(1, runs);
            Assert.True(
                useTimeSpan
                    ? ControlledThread.Join(thread, TimeSpan.Zero)
                    : ControlledThread.Join(thread, 0));
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Fact]
    public void JoinTimeSpanAcceptsExactMaximumMillisecondsBoundary()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            TimeSpan? completedAt = null;
            var thread = ControlledThread.Create(() =>
            {
                ControlledThread.Sleep(25);
                completedAt = coordinator.Scheduler.VirtualTime;
            });
            ControlledThread.Start(thread);

            var joined = ControlledThread.Join(
                thread,
                TimeSpan.FromMilliseconds(int.MaxValue));

            Assert.True(joined);
            Assert.Equal(TimeSpan.FromMilliseconds(25), completedAt);
            Assert.Equal(TimeSpan.FromMilliseconds(25), coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Fact]
    public void SleepTimeSpanAcceptsExactMaximumMillisecondsBoundary()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            TimeSpan? completedAt = null;
            var thread = ControlledThread.Create(() =>
            {
                ControlledThread.Sleep(TimeSpan.FromMilliseconds(int.MaxValue));
                completedAt = coordinator.Scheduler.VirtualTime;
            });
            ControlledThread.Start(thread);
            ControlledThread.Join(thread);

            var expected = TimeSpan.FromMilliseconds(int.MaxValue);
            Assert.Equal(expected, completedAt);
            Assert.Equal(expected, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Fact]
    public void YieldExecutesExactlyOneOfTwoReadyOperations()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var order = new List<string>();
            coordinator.Scheduler.Schedule(() => order.Add("first"));
            coordinator.Scheduler.Schedule(() => order.Add("second"));

            var yielded = ControlledThread.Yield();

            Assert.True(yielded);
            Assert.Equal(["first"], order);
            Assert.Equal(1, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.False(coordinator.Scheduler.IsIdle);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);

            Assert.Equal(1, coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken));
            Assert.Equal(["first", "second"], order);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThreadStartRejectsNullAndNonNullParametersWithoutConsumingStart(bool useNull)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var runs = 0;
            var thread = ControlledThread.Create((ThreadStart)(() => runs++));
            object? parameter = useNull ? null : new object();

            var exception = Assert.Throws<InvalidOperationException>(
                () => ControlledThread.Start(thread, parameter));

            Assert.IsType<InvalidOperationException>(exception);
            Assert.Equal(0, runs);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);

            ControlledThread.Start(thread);
            Assert.Equal(1, coordinator.Scheduler.RunnableOperationCount);
            ControlledThread.Join(thread);

            Assert.Equal(1, runs);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsEmpty(coordinator);
        });
    }
}
