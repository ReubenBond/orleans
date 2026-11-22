using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

namespace Orleans.Testing.Simulation.Runtime.Tests.Tasks;

/// <summary>
/// Tests for the <see cref="ControlledTaskCompletionSource"/> completion redirects: each completes the real BCL
/// completion source through the same member, so the result, the try-variants, and the source's creation options are
/// the BCL's own, and an awaited completion still resumes through the coordinator rather than inside the completion.
/// </summary>
public sealed class ControlledTaskCompletionSourceTests
{
    [Fact]
    public void CompletionMembersCompleteTheRealSource()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var result = new TaskCompletionSource<int>();
            ControlledTaskCompletionSource.SetResult(result, 42);
            Assert.Equal(42, result.Task.Result);

            var fault = new TaskCompletionSource();
            var boom = new InvalidOperationException("boom");
            ControlledTaskCompletionSource.SetException(fault, boom);
            Assert.Same(boom, fault.Task.Exception!.InnerException);

            var token = new CancellationToken(canceled: true);
            var canceled = new TaskCompletionSource<int>();
            ControlledTaskCompletionSource.SetCanceled(canceled, token);
            Assert.Equal(token, Assert.ThrowsAny<OperationCanceledException>(() => canceled.Task.GetAwaiter().GetResult()).CancellationToken);

            var copied = new TaskCompletionSource<int>();
            ControlledTaskCompletionSource.SetFromTask(copied, Task.FromResult(7));
            Assert.Equal(7, copied.Task.Result);
        });
    }

    [Fact]
    public void TryVariantsReturnTheBclOutcome()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var source = new TaskCompletionSource<int>();
            Assert.True(ControlledTaskCompletionSource.TrySetResult(source, 1));
            Assert.False(ControlledTaskCompletionSource.TrySetResult(source, 2));
            Assert.False(ControlledTaskCompletionSource.TrySetException(source, new InvalidOperationException()));
            Assert.False(ControlledTaskCompletionSource.TrySetCanceled(source));
            Assert.False(ControlledTaskCompletionSource.TrySetFromTask(source, Task.FromResult(3)));
            Assert.Equal(1, source.Task.Result);
            Assert.Throws<InvalidOperationException>(() => ControlledTaskCompletionSource.SetResult(source, 4));
            Assert.Throws<ArgumentException>(
                () => ControlledTaskCompletionSource.TrySetFromTask(new TaskCompletionSource(), new TaskCompletionSource().Task));
        });
    }

    [Fact]
    public void CreationOptionsAreTheSourcesOwn()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ControlledTaskCompletionSource.SetResult(source);

            Assert.True(source.Task.IsCompletedSuccessfully);
            Assert.Equal(TaskCreationOptions.RunContinuationsAsynchronously, source.Task.CreationOptions);
        });
    }

    [Fact]
    public void NullSourceThrowsTheRuntimeNullReferenceException()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<NullReferenceException>(() => ControlledTaskCompletionSource.SetResult(null!));
            Assert.Throws<NullReferenceException>(() => ControlledTaskCompletionSource.TrySetResult<int>(null!, 1));
        });
    }

    // Finalizers and callbacks from uninstrumented code may complete a source outside any simulation: the shims pass
    // through to the BCL member, and publish no race-detection edge without an executing operation.
#pragma warning disable xUnit1051 // The overloads without a token are under test; nothing waits.
    [Fact]
    public async Task CompletionOutsideSimulationPassesThroughToTheBcl()
    {
        Assert.False(SimulationTaskRuntime.IsSimulationActive);
        var token = new CancellationToken(canceled: true);
        var fault = new InvalidOperationException("boom");
        Exception[] faults = [fault];

        var source = new TaskCompletionSource();
        ControlledTaskCompletionSource.SetResult(source);
        Assert.True(source.Task.IsCompletedSuccessfully);
        Assert.False(ControlledTaskCompletionSource.TrySetResult(source));
        Assert.Throws<InvalidOperationException>(() => ControlledTaskCompletionSource.SetException(source, fault));
        Assert.Throws<InvalidOperationException>(() => ControlledTaskCompletionSource.SetException(source, faults));
        Assert.False(ControlledTaskCompletionSource.TrySetException(source, fault));
        Assert.False(ControlledTaskCompletionSource.TrySetException(source, faults));
        Assert.Throws<InvalidOperationException>(() => ControlledTaskCompletionSource.SetCanceled(source));
        Assert.Throws<InvalidOperationException>(() => ControlledTaskCompletionSource.SetCanceled(source, token));
        Assert.False(ControlledTaskCompletionSource.TrySetCanceled(source));
        Assert.False(ControlledTaskCompletionSource.TrySetCanceled(source, token));
        Assert.Throws<InvalidOperationException>(() => ControlledTaskCompletionSource.SetFromTask(source, Task.CompletedTask));
        Assert.False(ControlledTaskCompletionSource.TrySetFromTask(source, Task.CompletedTask));

        var generic = new TaskCompletionSource<int>();
        Assert.True(ControlledTaskCompletionSource.TrySetResult(generic, 1));
        Assert.Equal(1, await generic.Task);
        Assert.Throws<InvalidOperationException>(() => ControlledTaskCompletionSource.SetResult(generic, 2));
        Assert.Throws<InvalidOperationException>(() => ControlledTaskCompletionSource.SetException(generic, fault));
        Assert.Throws<InvalidOperationException>(() => ControlledTaskCompletionSource.SetException(generic, faults));
        Assert.False(ControlledTaskCompletionSource.TrySetException(generic, fault));
        Assert.False(ControlledTaskCompletionSource.TrySetException(generic, faults));
        Assert.Throws<InvalidOperationException>(() => ControlledTaskCompletionSource.SetCanceled(generic));
        Assert.Throws<InvalidOperationException>(() => ControlledTaskCompletionSource.SetCanceled(generic, token));
        Assert.False(ControlledTaskCompletionSource.TrySetCanceled(generic));
        Assert.False(ControlledTaskCompletionSource.TrySetCanceled(generic, token));
        Assert.Throws<InvalidOperationException>(() => ControlledTaskCompletionSource.SetFromTask(generic, Task.FromResult(3)));
        Assert.False(ControlledTaskCompletionSource.TrySetFromTask(generic, Task.FromResult(3)));

        var faulted = new TaskCompletionSource<int>();
        ControlledTaskCompletionSource.SetException(faulted, faults);
        Assert.Same(fault, faulted.Task.Exception!.InnerException);
        var canceled = new TaskCompletionSource();
        Assert.True(ControlledTaskCompletionSource.TrySetCanceled(canceled, token));
        Assert.True(canceled.Task.IsCanceled);
        var copied = new TaskCompletionSource<int>();
        ControlledTaskCompletionSource.SetFromTask(copied, Task.FromResult(7));
        Assert.Equal(7, await copied.Task);
    }
#pragma warning restore xUnit1051

    // With a simulation's ambient context, a completion publishes into that simulation, so a thread which carries the
    // context off the simulation's logical thread is rejected before the source completes.
    [Fact]
    public void CompletionWithAmbientContextOffTheSimulationThreadIsRejectedBeforeCompleting()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var source = new TaskCompletionSource();
        var generic = new TaskCompletionSource<int>();
        (Exception? Plain, Exception? Generic) escaped = default;

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var thread = new Thread(() => escaped = (
                Record.Exception(() => ControlledTaskCompletionSource.SetResult(source)),
                Record.Exception(() => ControlledTaskCompletionSource.TrySetResult(generic, 1))))
            {
                IsBackground = true,
            };
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The completing thread did not finish.");
        });

        Assert.Equal(
            "System.Threading.Tasks.TaskCompletionSource.SetResult",
            Assert.IsType<SimulationThreadAffinityException>(escaped.Plain).ApiName);
        Assert.Equal(
            "System.Threading.Tasks.TaskCompletionSource`1.TrySetResult",
            Assert.IsType<SimulationThreadAffinityException>(escaped.Generic).ApiName);
        Assert.False(source.Task.IsCompleted);
        Assert.False(generic.Task.IsCompleted);
    }

    [Theory]
    [InlineData(TaskCreationOptions.None)]
    [InlineData(TaskCreationOptions.RunContinuationsAsynchronously)]
    public void AwaitedCompletionResumesThroughTheCoordinatorAfterCompletion(TaskCreationOptions options)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var tcs = new TaskCompletionSource<int>(options);
            var log = new List<string>();
            var awaiter = new ControlledTaskAwaiter<int>(tcs.Task);
            awaiter.OnCompleted(() => log.Add("resumed " + awaiter.GetResult()));

            coordinator.Scheduler.Schedule(() =>
            {
                log.Add("before");
                ControlledTaskCompletionSource.SetResult(tcs, 99);
                log.Add("after");
            });
            coordinator.Scheduler.DrainUntil(() => log.Count == 3, "test", TestContext.Current.CancellationToken);

            Assert.Equal(["before", "after", "resumed 99"], log);
        });
    }
}
