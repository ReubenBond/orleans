using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Tests.Tasks;

public sealed class ControlledTaskTimeoutTests
{
    [Fact]
    public void DelayZeroAndPreCanceledMatchBclPrecedence()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Same(Task.CompletedTask, ControlledTask.Delay(0));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Task canceled = ControlledTask.Delay(0, cancellation.Token);
            Assert.True(canceled.IsCanceled);
        });
    }

    [Fact]
    public void DelayCancellationCancelsDeadlineWithoutPendingWork()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var cancellation = new CancellationTokenSource();
            Task delay = ControlledTask.Delay(TimeSpan.FromMinutes(1), cancellation.Token);
            Assert.NotNull(coordinator.Scheduler.NextTimerDue);

            cancellation.Cancel();

            Assert.True(delay.IsCanceled);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
        });
    }

    [Fact]
    public void InfiniteDelayHasNoVirtualDeadline()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Task delay = ControlledTask.Delay(Timeout.Infinite, CancellationToken.None);
            Assert.False(delay.IsCompleted);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
        });
    }

    [Fact]
    public void WaitAsyncPreservesSuccessfulResult()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var source = new TaskCompletionSource<int>();
            Task<int> wait = ControlledTask.WaitAsync(source.Task, TimeSpan.FromSeconds(5));
            coordinator.Scheduler.Schedule(() => source.SetResult(42));
            coordinator.Scheduler.DrainUntil(() => wait.IsCompleted, "test.wait", TestContext.Current.CancellationToken);

            Assert.Equal(42, wait.Result);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
        });
    }

    [Fact]
    public void WaitAsyncPreservesFaultAndCancellation()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var faulted = new TaskCompletionSource();
            Task faultWait = ControlledTask.WaitAsync(faulted.Task, TimeSpan.FromSeconds(5));
            coordinator.Scheduler.Schedule(() => faulted.SetException(new FormatException("bad")));
            coordinator.Scheduler.DrainUntil(
                () => faultWait.IsCompleted,
                "test.wait",
                TestContext.Current.CancellationToken);
            Assert.IsType<FormatException>(faultWait.Exception!.InnerException);

            using var sourceCancellation = new CancellationTokenSource();
            var canceled = new TaskCompletionSource();
            Task cancelWait = ControlledTask.WaitAsync(canceled.Task, TimeSpan.FromSeconds(5));
            coordinator.Scheduler.Schedule(() => canceled.SetCanceled(sourceCancellation.Token));
            coordinator.Scheduler.DrainUntil(
                () => cancelWait.IsCompleted,
                "test.wait",
                TestContext.Current.CancellationToken);
            Assert.True(cancelWait.IsCanceled);
        });
    }

    [Fact]
    public void WaitAsyncTimeoutFaultsAndRemovesReadinessWaiter()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var source = new TaskCompletionSource();
            Task wait = ControlledTask.WaitAsync(source.Task, TimeSpan.FromMilliseconds(25));

            coordinator.Scheduler.DrainUntil(() => wait.IsCompleted, "test.wait", TestContext.Current.CancellationToken);

            Assert.IsType<TimeoutException>(wait.Exception!.InnerException);
            Assert.Equal(TimeSpan.FromMilliseconds(25), coordinator.Scheduler.VirtualTime);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitAsyncCancellationBeatsFutureTimeout()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var source = new TaskCompletionSource();
            using var cancellation = new CancellationTokenSource();
            Task wait = ControlledTask.WaitAsync(
                source.Task,
                TimeSpan.FromSeconds(5),
                TimeProvider.System,
                cancellation.Token);

            cancellation.Cancel();

            Assert.True(wait.IsCanceled);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
        });
    }

    [Fact]
    public void WaitAsyncCompletedAndZeroTimeoutFastPathsMatchBcl()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Task completed = Task.FromResult(1);
            Assert.Same(completed, ControlledTask.WaitAsync(completed, TimeSpan.Zero));

            var source = new TaskCompletionSource();
            Task timedOut = ControlledTask.WaitAsync(source.Task, TimeSpan.Zero);
            Assert.IsType<TimeoutException>(timedOut.Exception!.InnerException);
        });
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
#pragma warning disable xUnit1051 // The tokenless overload is the API under test.
    public void DelayRejectsInvalidMilliseconds(int milliseconds)
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(
            coordinator,
            () => Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ControlledTask.Delay(milliseconds); }));
    }
#pragma warning restore xUnit1051

    [Fact]
#pragma warning disable xUnit1051 // The provider-only overloads are the APIs under test.
    public void ProviderOverloadsValidateTimeoutBeforeRejectingCustomProvider()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var provider = new UnsupportedTimeProvider();
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = ControlledTask.Delay(TimeSpan.FromMilliseconds(-2), provider); });
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = ControlledTask.WaitAsync(new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(-2), provider); });
        });
    }
#pragma warning restore xUnit1051

    private sealed class UnsupportedTimeProvider : TimeProvider;
}
