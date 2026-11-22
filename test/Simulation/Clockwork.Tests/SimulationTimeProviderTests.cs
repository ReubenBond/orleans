using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Tests;

public sealed class SimulationTimeProviderTests
{
    [Fact]
    public void TimeQueriesUseSchedulerTimelineAndTimeProviderSemantics()
    {
        var (scheduler, _, provider) = CreateComponents();

        Assert.Equal(scheduler.UtcNow, provider.GetUtcNow());
        Assert.Equal(scheduler.UtcNow.Ticks, provider.GetTimestamp());
        Assert.Equal(TimeSpan.TicksPerSecond, provider.TimestampFrequency);
        Assert.Same(TimeZoneInfo.Utc, provider.LocalTimeZone);
        Assert.Equal("1970-01-01T00:00:00.000", provider.ToString());

        Advance(scheduler, TimeSpan.FromMilliseconds(1250));

        Assert.Equal(scheduler.UtcNow, provider.GetUtcNow());
        Assert.Equal(scheduler.UtcNow.Ticks, provider.GetTimestamp());
    }

    [Fact]
    public void TimeQueriesRemainAvailableAfterLaneDetachmentWhileTimerCreationFails()
    {
        var (scheduler, queue, provider) = CreateComponents();

        _ = queue.Detach();

        Assert.Equal(scheduler.UtcNow, provider.GetUtcNow());
        Assert.Equal(scheduler.UtcNow.Ticks, provider.GetTimestamp());
        Assert.Throws<ObjectDisposedException>(
            () => provider.CreateTimer(
                static _ => { },
                null,
                TimeSpan.Zero,
                Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public void TimerFiresWhenSimulatedTimeReachesDueTime()
    {
        var (scheduler, queue, provider) = CreateComponents();
        var fireCount = 0;
        using var timer = provider.CreateTimer(_ => fireCount++, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

        Assert.False(queue.RunOnce(TestContext.Current.CancellationToken));
        Advance(scheduler, TimeSpan.FromSeconds(5));

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void PeriodicTimerReschedulesItself()
    {
        var (scheduler, queue, provider) = CreateComponents();
        var fireCount = 0;
        using var timer = provider.CreateTimer(_ => fireCount++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        Advance(scheduler, TimeSpan.FromSeconds(1));
        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Advance(scheduler, TimeSpan.FromSeconds(2));
        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));

        Assert.Equal(2, fireCount);
    }

    [Fact]
    public void DisposedTimerDoesNotFire()
    {
        var (scheduler, queue, provider) = CreateComponents();
        var fired = false;
        var timer = provider.CreateTimer(_ => fired = true, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        timer.Dispose();
        Advance(scheduler, TimeSpan.FromSeconds(1));

        Assert.False(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.False(fired);
    }

    [Fact]
    public void CallbackChangeToPeriodicReplacesAutomaticReschedule()
    {
        var (scheduler, queue, provider) = CreateComponents();
        var fireCount = 0;
        ITimer? timer = null;
        timer = provider.CreateTimer(
            _ =>
            {
                fireCount++;
                if (fireCount == 1)
                {
                    _ = timer!.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
                }
            },
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10));
        using (timer)
        {
            Advance(scheduler, TimeSpan.FromSeconds(1));
            Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
            AssertSinglePendingTimer(queue, scheduler.UtcNow + TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));

            Advance(scheduler, TimeSpan.FromSeconds(3));
            Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
            Assert.Equal(2, fireCount);
            AssertSinglePendingTimer(queue, scheduler.UtcNow + TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

            Advance(scheduler, TimeSpan.FromSeconds(2));
            Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
            Assert.Equal(3, fireCount);
            AssertSinglePendingTimer(queue, scheduler.UtcNow + TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void CallbackChangeToOneShotLeavesOnlyTheReplacementPending()
    {
        var (scheduler, queue, provider) = CreateComponents();
        var fireCount = 0;
        ITimer? timer = null;
        timer = provider.CreateTimer(
            _ =>
            {
                fireCount++;
                if (fireCount == 1)
                {
                    _ = timer!.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
                }
            },
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        using (timer)
        {
            Advance(scheduler, TimeSpan.FromSeconds(1));
            Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
            AssertSinglePendingTimer(queue, scheduler.UtcNow + TimeSpan.FromSeconds(3), TimeSpan.Zero);

            Advance(scheduler, TimeSpan.FromSeconds(3));
            Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
            Assert.Equal(2, fireCount);
            Assert.DoesNotContain(
                queue.CaptureScheduledItems(),
                static item => item.Kind == "timer");
            Assert.False(queue.RunOnce(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void CallbackChangeToInfiniteSuppressesAutomaticReschedule()
    {
        var (scheduler, queue, provider) = CreateComponents();
        var fireCount = 0;
        ITimer? timer = null;
        timer = provider.CreateTimer(
            _ =>
            {
                fireCount++;
                _ = timer!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            },
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        using (timer)
        {
            Advance(scheduler, TimeSpan.FromSeconds(1));
            Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));

            Assert.Equal(1, fireCount);
            Assert.DoesNotContain(
                queue.CaptureScheduledItems(),
                static item => item.Kind == "timer");
            Advance(scheduler, TimeSpan.FromSeconds(10));
            Assert.False(queue.RunOnce(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void RepeatedCallbackChangesLeaveOnlyTheLastReplacementPending()
    {
        var (scheduler, queue, provider) = CreateComponents();
        var fireCount = 0;
        ITimer? timer = null;
        timer = provider.CreateTimer(
            _ =>
            {
                fireCount++;
                if (fireCount == 1)
                {
                    _ = timer!.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
                    _ = timer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6));
                    _ = timer.Change(TimeSpan.FromSeconds(4), Timeout.InfiniteTimeSpan);
                }
            },
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        using (timer)
        {
            Advance(scheduler, TimeSpan.FromSeconds(1));
            Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
            AssertSinglePendingTimer(queue, scheduler.UtcNow + TimeSpan.FromSeconds(4), TimeSpan.Zero);

            Advance(scheduler, TimeSpan.FromSeconds(4));
            Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
            Assert.Equal(2, fireCount);
            Assert.DoesNotContain(
                queue.CaptureScheduledItems(),
                static item => item.Kind == "timer");
            Assert.False(queue.RunOnce(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void CallbackDisposeSuppressesAutomaticReschedule()
    {
        var (scheduler, queue, provider) = CreateComponents();
        var fireCount = 0;
        ITimer? timer = null;
        timer = provider.CreateTimer(
            _ =>
            {
                fireCount++;
                timer!.Dispose();
            },
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        Advance(scheduler, TimeSpan.FromSeconds(1));
        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));

        Assert.Equal(1, fireCount);
        Assert.DoesNotContain(
            queue.CaptureScheduledItems(),
            static item => item.Kind == "timer");
        Advance(scheduler, TimeSpan.FromSeconds(10));
        Assert.False(queue.RunOnce(TestContext.Current.CancellationToken));
    }

    private static void AssertSinglePendingTimer(
        SimulationSchedulerLane queue,
        DateTimeOffset expectedDueTime,
        TimeSpan expectedPeriod)
    {
        var pending = Assert.Single(
            queue.CaptureScheduledItems(),
            static item => item.Kind == "timer");
        Assert.Equal(expectedDueTime, pending.DueTime);
        Assert.Equal(
            FormattableString.Invariant($"Timer callback (period={expectedPeriod:c})"),
            pending.Description);
    }

    private static (SimulationScheduler Scheduler, SimulationSchedulerLane Queue, SimulationTimeProvider Provider) CreateComponents()
    {
        var scheduler = SimulationTestHarness.NewScheduler();
        var queue = new SimulationSchedulerLane(scheduler, SimulationTestHarness.NewGuard(scheduler));
        return (scheduler, queue, new SimulationTimeProvider(queue));
    }

    private static void Advance(SimulationScheduler scheduler, TimeSpan delta) =>
        scheduler.AdvanceVirtualTimeTo(scheduler.VirtualTime + delta);
}
