using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Tasks;

/// <summary>
/// Tests for the <see cref="ControlledTask"/> static shims: combinator determinism, synchronous waits
/// that pump instead of block (and surface deadlock), continuations routed through the loop, and the
/// explicit rejection of APIs outside this shim's inventory.
/// </summary>
public sealed class ControlledTaskApiTests
{
    // Cancellation is the one completion which may arrive from another thread: Clockwork's callbacks complete the
    // waits without calling a controlled API, so the cancelling thread records no thread-affinity violation.
    [Fact]
    public void CancellationFromAnotherThreadCompletesWaitsWithoutAThreadAffinityViolation()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var cancellation = new CancellationTokenSource();
            Task delay = ControlledTask.Delay(1_000, cancellation.Token);
            Task wait = ControlledTask.WaitAsync(new TaskCompletionSource().Task, cancellation.Token);
            ControlledCancellationTokenSource.CancelAfter(cancellation, 1_000);
            var periodic = new ControlledPeriodicTimer(TimeSpan.FromSeconds(1));
            ValueTask<bool> tick = periodic.WaitForNextTickAsync(cancellation.Token);

            // The thread carries the simulation's ambient context, as a thread started from simulation work does.
            Task.Run(cancellation.Cancel, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
                .GetAwaiter()
                .GetResult();
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            periodic.Dispose();

            Assert.True(delay.IsCanceled);
            Assert.True(wait.IsCanceled);
            Assert.True(tick.IsCanceled);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
        });
    }

    [Fact]
    public void WhenAllCompletesAfterAllAntecedents()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var a = new TaskCompletionSource<int>();
            var b = new TaskCompletionSource<int>();
            var all = ControlledTask.WhenAll(a.Task, b.Task);

            Assert.False(all.IsCompleted);

            coordinator.Scheduler.Schedule(() => a.SetResult(1));
            coordinator.Scheduler.Schedule(() => b.SetResult(2));
            coordinator.Scheduler.DrainUntil(() => all.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal([1, 2], all.Result);
        });
    }

    [Fact]
    public void WhenAnyReportsTheDeterministicFirstCompleter()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var a = new TaskCompletionSource<int>();
            var b = new TaskCompletionSource<int>();
            var any = ControlledTask.WhenAny(a.Task, b.Task);

            // b is completed first on the loop, so WhenAny must resolve to b, deterministically.
            coordinator.Scheduler.Schedule(() => b.SetResult(2));
            coordinator.Scheduler.Schedule(() => a.SetResult(1));
            coordinator.Scheduler.DrainUntil(() => any.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Same(b.Task, any.Result);
        });
    }

    [Fact]
    public void WaitPumpsUntilCompletionWithoutBlocking()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var tcs = new TaskCompletionSource();
            coordinator.Scheduler.Schedule(() => tcs.SetResult());

            ControlledTask.Wait(tcs.Task);

            Assert.True(tcs.Task.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void WaitThrowsAggregateExceptionOnFault()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var tcs = new TaskCompletionSource();
            var boom = new InvalidTimeZoneException("boom");
            coordinator.Scheduler.Schedule(() => tcs.SetException(boom));

#pragma warning disable xUnit1051 // The overload without a token is the subject under test.
            var ex = Assert.Throws<AggregateException>(() => ControlledTask.Wait(tcs.Task));
#pragma warning restore xUnit1051
            Assert.Same(boom, ex.InnerException);
        });
    }

    [Fact]
    public void ResultPumpsUntilCompletion()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var tcs = new TaskCompletionSource<int>();
            coordinator.Scheduler.Schedule(() => tcs.SetResult(42));

            var value = ControlledTask.Result(tcs.Task);

            Assert.Equal(42, value);
        });
    }

    [Fact]
    public void WaitOnANeverCompletingTaskSurfacesDeadlock()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var tcs = new TaskCompletionSource();
#pragma warning disable xUnit1051 // The overload without a token is the subject under test.
            Assert.Throws<SimulationSynchronousWaitDeadlockException>(() => ControlledTask.Wait(tcs.Task));
#pragma warning restore xUnit1051
        });
    }

    [Fact]
    public void WaitAllPumpsUntilAllComplete()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var a = new TaskCompletionSource();
            var b = new TaskCompletionSource();
            coordinator.Scheduler.Schedule(() => a.SetResult());
            coordinator.Scheduler.Schedule(() => b.SetResult());

            ControlledTask.WaitAll(a.Task, b.Task);

            Assert.True(a.Task.IsCompleted && b.Task.IsCompleted);
        });
    }

    [Fact]
    public void WaitAnyReturnsIndexOfFirstCompleted()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var a = new TaskCompletionSource();
            var b = new TaskCompletionSource();
            coordinator.Scheduler.Schedule(() => b.SetResult());

            var index = ControlledTask.WaitAny(a.Task, b.Task);

            Assert.Equal(1, index);
        });
    }

    [Fact]
    public void ContinueWithRunsAfterAntecedentThroughTheLoop()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var tcs = new TaskCompletionSource<int>();
            var ranInline = false;
            var continuation = ControlledTask.ContinueWith(tcs.Task, t => ranInline = false);

            coordinator.Scheduler.Schedule(() =>
            {
                tcs.SetResult(5);
                ranInline = continuation.IsCompleted;
            });
            coordinator.Scheduler.DrainUntil(
                () => continuation.IsCompleted,
                "test",
                TestContext.Current.CancellationToken);

            Assert.False(ranInline);
            Assert.True(continuation.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void ContinueWithFuncProducesResult()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var tcs = new TaskCompletionSource<int>();
            var continuation = ControlledTask.ContinueWith(tcs.Task, t => ((Task<int>)t).Result * 2);

            coordinator.Scheduler.Schedule(() => tcs.SetResult(21));
            coordinator.Scheduler.DrainUntil(
                () => continuation.IsCompleted,
                "test",
                TestContext.Current.CancellationToken);

            Assert.Equal(42, continuation.Result);
        });
    }

    [Fact]
    public void ContinueWithRunsEvenWhenAntecedentFaults()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var tcs = new TaskCompletionSource<int>();
            var observedFault = false;
            var continuation = ControlledTask.ContinueWith(tcs.Task, t => observedFault = t.IsFaulted);

            coordinator.Scheduler.Schedule(() => tcs.SetException(new InvalidTimeZoneException()));
            coordinator.Scheduler.DrainUntil(
                () => continuation.IsCompleted,
                "test",
                TestContext.Current.CancellationToken);

            Assert.True(observedFault);
            Assert.True(continuation.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public async Task AContinuationTheRuntimeQueuesFailsTheOperationInsteadOfEscaping()
    {
        // The BCL queues a gate's continuation instead of running it inline when the stack is nearly exhausted. A gate
        // which runs its continuations asynchronously makes the BCL queue it deterministically.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queued = new ManualResetEventSlim();
        Task continuation = gate.Task.ContinueWith(
            _ => queued.Wait(TestContext.Current.CancellationToken),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var exception = Assert.Throws<SimulationApiException>(() => ControlledTask.CompleteGate(
            gate,
            TaskStatus.RanToCompletion,
            continuation,
            "System.Threading.Tasks.Task.ContinueWith"));

        Assert.Contains("the runtime queued the continuation", exception.Message, StringComparison.Ordinal);
        Assert.False(continuation.IsCompleted);
        queued.Set();
        await continuation;
    }

#pragma warning disable xUnit1051 // The token is part of the overload under test; no asynchronous wait occurs.
    public static TheoryData<Func<Task>> DelayCalls =>
        new()
        {
            () => ControlledTask.Delay(100),
            () => ControlledTask.Delay(TimeSpan.FromMilliseconds(100)),
            () => ControlledTask.Delay(100, CancellationToken.None),
            () => ControlledTask.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None),
            () => ControlledTask.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System),
            () => ControlledTask.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, CancellationToken.None),
        };
#pragma warning restore xUnit1051

    [Theory]
    [MemberData(nameof(DelayCalls))]
    public void EveryDelayOverloadCompletesOnVirtualTime(Func<Task> delay)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Task task = delay();
            Assert.False(task.IsCompleted);
            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test.delay", TestContext.Current.CancellationToken);
            Assert.True(task.IsCompletedSuccessfully);
            Assert.Equal(TimeSpan.FromMilliseconds(100), coordinator.Scheduler.VirtualTime);
        });
    }

    [Fact]
    public void RunQueuesBodyAsControlledWorkAndCompletes()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ran = false;
            var task = ControlledTask.Run(() => { ran = true; });

            // The body is queued, not run inline: nothing has executed until the loop is pumped.
            Assert.False(ran);
            Assert.False(task.IsCompleted);

            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.True(ran);
            Assert.True(task.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void RunOfFuncReturnsResultDeterministically()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var task = ControlledTask.Run(() => 42);
            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test", TestContext.Current.CancellationToken);
            Assert.Equal(42, task.Result);
        });
    }

    [Fact]
    public void RunOfAsyncFuncUnwrapsInnerTask()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var inner = new TaskCompletionSource<int>();
            var task = ControlledTask.Run(() => inner.Task);

            // The outer task must not complete until the unwrapped inner task completes.
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.False(task.IsCompleted);

            coordinator.Scheduler.Schedule(() => inner.SetResult(7));
            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal(7, task.Result);
        });
    }

    [Fact]
    public void RunPropagatesTheBodyFault()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var boom = new InvalidTimeZoneException("boom");
            var task = ControlledTask.Run(() => throw boom);
            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal(TaskStatus.Faulted, task.Status);
            Assert.Same(boom, task.Exception!.InnerException);
        });
    }

    [Fact]
    public void RunWithAlreadyCanceledTokenCancelsAndDoesNotRunBody()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            var ran = false;
            var task = ControlledTask.Run(() => { ran = true; }, cts.Token);
            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.False(ran);
            Assert.Equal(TaskStatus.Canceled, task.Status);
        });
    }

    [Fact]
#pragma warning disable xUnit1051 // Each exact overload, including those without tokens, is the subject under test.
    public void RunOutsideSimulationFailsBeforeInvokingTheDelegate()
    {
        Assert.False(SimulationTaskRuntime.IsSimulationActive);
        var ran = false;

        Exception? exception = Record.Exception(() =>
        {
            _ = ControlledTask.Run(
                () => ran = true,
                TestContext.Current.CancellationToken);
        });

        Assert.False(ran);
        SimulationNotActiveExceptionAssert.Equal(
            exception,
            "System.Threading.Tasks.Task.Run");
    }
#pragma warning restore xUnit1051

    [Fact]
    public void TaskFactoryStartNewQueuesBodyAsControlledWork()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ran = false;
            var task = ControlledTaskFactory.StartNew(Task.Factory, () => { ran = true; });

            // The body is queued, not run inline.
            Assert.False(ran);
            Assert.False(task.IsCompleted);

            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.True(ran);
            Assert.True(task.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void TaskFactoryStartNewOfFuncReturnsResult()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var task = ControlledTaskFactory.StartNew(Task.Factory, () => 99);
            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test", TestContext.Current.CancellationToken);
            Assert.Equal(99, task.Result);
        });
    }

    [Fact]
    public void TaskFactoryStateAndFullSchedulerFormsRunOnControlledStrands()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            long actionStrand = SimulationSynchronizationFlow.None;
            long actionState = 0;
            var action = ControlledTaskFactory.StartNew(
                Task.Factory,
                state =>
                {
                    actionState = (long)state!;
                    actionStrand = SimulationSynchronizationFlow.CurrentId;
                },
                17L,
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);
            var result = ControlledTaskFactory.StartNew(
                Task.Factory,
                state => (Value: (int)state!, Strand: SimulationSynchronizationFlow.CurrentId),
                42,
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);

            coordinator.Scheduler.DrainUntil(
                () => action.IsCompleted && result.IsCompleted,
                "test",
                TestContext.Current.CancellationToken);

            Assert.Equal(17L, actionState);
            Assert.Equal(17L, action.AsyncState);
            Assert.NotEqual(SimulationSynchronizationFlow.None, actionStrand);
            Assert.Equal(42, result.Result.Value);
            Assert.Equal(42, result.AsyncState);
            Assert.NotEqual(SimulationSynchronizationFlow.None, result.Result.Strand);
            Assert.True(action.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void GenericTaskFactoryStateFormPreservesStateAndResult()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var factory = new TaskFactory<int>();
            var result = ControlledTaskFactory.StartNew(
                factory,
                state => (int)state! * 2,
                21,
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);

            coordinator.Scheduler.DrainUntil(() => result.IsCompleted, "test", TestContext.Current.CancellationToken);
            Assert.Equal(42, result.Result);
            Assert.Equal(21, result.AsyncState);
        });
    }

    [Fact]
    public void TaskFactoryRejectsUnsupportedSchedulerAndOptions()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var schedulers = new ConcurrentExclusiveSchedulerPair();
            Assert.Throws<SimulationApiException>((Action)(() =>
            {
                _ = ControlledTaskFactory.StartNew(
                    Task.Factory,
                    () => { },
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    schedulers.ExclusiveScheduler);
            }));
            Assert.Throws<SimulationApiException>((Action)(() =>
            {
                _ = ControlledTaskFactory.StartNew(
                    Task.Factory,
                    () => { },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }));
            Assert.Throws<SimulationApiException>((Action)(() =>
            {
                _ = ControlledTaskFactory.StartNew(
                    Task.Factory,
                    () => { },
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }));
            schedulers.Complete();
        });
    }

    [Fact]
    public void TaskFactoryPreCancellationIsImmediateAndUnmatchedCancellationFaults()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var ran = false;
            Task canceled = ControlledTaskFactory.StartNew(
                Task.Factory,
                () => ran = true,
                cts.Token,
                TaskCreationOptions.None,
                TaskScheduler.Default);
            Task faulted = ControlledTaskFactory.StartNew(
                Task.Factory,
                () => throw new OperationCanceledException(),
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);

            Assert.True(canceled.IsCanceled);
            Assert.False(ran);
            coordinator.Scheduler.DrainUntil(() => faulted.IsCompleted, "test", TestContext.Current.CancellationToken);
            Assert.Equal(TaskStatus.Faulted, faulted.Status);
            Assert.IsType<OperationCanceledException>(faulted.Exception!.InnerException);
        });
    }

    [Fact]
    public void TaskFactoryStartNewRejectsAttachedToParent()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ex = Assert.Throws<SimulationApiException>(() =>
            {
                // Exercise the options-only overload independently from the full scheduler form.
#pragma warning disable xUnit1051
                _ = ControlledTaskFactory.StartNew(Task.Factory, () => { }, TaskCreationOptions.AttachedToParent);
#pragma warning restore xUnit1051
            });
            Assert.Contains("AttachedToParent", ex.Message);
        });
    }

    [Fact]
    public void GenericContinueWithProjectsAntecedentResult()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var source = new TaskCompletionSource<int>();
            var projected = ControlledTask.ContinueWith(source.Task, t => t.Result.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Assert.False(projected.IsCompleted);

            coordinator.Scheduler.Schedule(() => source.SetResult(7));
            coordinator.Scheduler.DrainUntil(() => projected.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal("7", projected.Result);
        });
    }

    [Fact]
    public void GenericContinueWithActionObservesTypedAntecedent()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var source = new TaskCompletionSource<int>();
            var seen = 0;
            var continuation = ControlledTask.ContinueWith(source.Task, t => { seen = t.Result; });

            coordinator.Scheduler.Schedule(() => source.SetResult(11));
            coordinator.Scheduler.DrainUntil(
                () => continuation.IsCompleted,
                "test",
                TestContext.Current.CancellationToken);

            Assert.Equal(11, seen);
            Assert.True(continuation.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void TaskFactoryStartNewOutsideSimulationFailsBeforeInvokingDelegate()
    {
        Assert.False(SimulationTaskRuntime.IsSimulationActive);
        var ran = false;
        Task<int>? task = null;

        Exception? exception = Record.Exception(() =>
        {
            task = ControlledTaskFactory.StartNew(
                Task.Factory,
                () =>
                {
                    ran = true;
                    return 5;
                });
        });

        Assert.False(ran);
        Assert.Null(task);
        SimulationNotActiveExceptionAssert.Equal(
            exception,
            "System.Threading.Tasks.TaskFactory.StartNew");
    }

    [Fact]
    public async Task TaskFactoryStateAndCustomSchedulerOutsideSimulationFailBeforeInvokingDelegate()
    {
        Assert.False(SimulationTaskRuntime.IsSimulationActive);
        var schedulers = new ConcurrentExclusiveSchedulerPair();
        var ran = false;
        Task<int>? task = null;

        Exception? exception = Record.Exception(() =>
        {
            task = ControlledTaskFactory.StartNew(
                Task.Factory,
                state =>
                {
                    ran = true;
                    return (int)state! * 2;
                },
                6,
                TestContext.Current.CancellationToken,
                TaskCreationOptions.None,
                schedulers.ExclusiveScheduler);
        });

        Assert.False(ran);
        Assert.Null(task);
        SimulationNotActiveExceptionAssert.Equal(
            exception,
            "System.Threading.Tasks.TaskFactory.StartNew");
        schedulers.Complete();
        await schedulers.Completion;
    }

    [Theory]
    [InlineData((int)QueuedTaskBodyVariant.TaskRunAction)]
    [InlineData((int)QueuedTaskBodyVariant.TaskRunFunction)]
    [InlineData((int)QueuedTaskBodyVariant.TaskFactoryStartNewAction)]
    [InlineData((int)QueuedTaskBodyVariant.TaskFactoryStartNewFunction)]
    public void QueuedTaskBodiesCaptureEnqueueTimeExecutionContext(int variantValue)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var variant = (QueuedTaskBodyVariant)variantValue;
            var ambient = new System.Threading.AsyncLocal<int> { Value = 5 };
            var seen = -1;
            Task<int>? resultTask = null;
            Task task;

            switch (variant)
            {
                case QueuedTaskBodyVariant.TaskRunAction:
                    task = ControlledTask.Run(() => seen = ambient.Value);
                    break;
                case QueuedTaskBodyVariant.TaskRunFunction:
                    resultTask = ControlledTask.Run(() =>
                    {
                        seen = ambient.Value;
                        return 42;
                    });
                    task = resultTask;
                    break;
                case QueuedTaskBodyVariant.TaskFactoryStartNewAction:
                    task = ControlledTaskFactory.StartNew(Task.Factory, () => seen = ambient.Value);
                    break;
                case QueuedTaskBodyVariant.TaskFactoryStartNewFunction:
                    resultTask = ControlledTaskFactory.StartNew(Task.Factory, () =>
                    {
                        seen = ambient.Value;
                        return 42;
                    });
                    task = resultTask;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variantValue));
            }

            Assert.False(task.IsCompleted);
            Assert.Equal(-1, seen);
            ambient.Value = 9;

            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal(5, seen);
            Assert.Equal(TaskStatus.RanToCompletion, task.Status);
            if (resultTask is not null)
            {
                Assert.Equal(42, resultTask.GetAwaiter().GetResult());
            }

            Assert.Equal(9, ambient.Value);
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Theory]
    [InlineData((int)ContinuationVariant.NonGenericAntecedentAction)]
    [InlineData((int)ContinuationVariant.NonGenericAntecedentResult)]
    [InlineData((int)ContinuationVariant.GenericAntecedentAction)]
    [InlineData((int)ContinuationVariant.GenericAntecedentResult)]
    public void ContinueWithCapturesRegistrationExecutionContext(int variantValue)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var variant = (ContinuationVariant)variantValue;
            var ambient = new System.Threading.AsyncLocal<int> { Value = 5 };
            var seen = -1;
            var antecedent = new TaskCompletionSource();
            var genericAntecedent = new TaskCompletionSource<int>();
            Task<int>? resultTask = null;
            Task continuation;

            switch (variant)
            {
                case ContinuationVariant.NonGenericAntecedentAction:
                    continuation = ControlledTask.ContinueWith(
                        antecedent.Task,
                        _ => seen = ambient.Value);
                    break;
                case ContinuationVariant.NonGenericAntecedentResult:
                    resultTask = ControlledTask.ContinueWith(
                        antecedent.Task,
                        _ =>
                        {
                            seen = ambient.Value;
                            return 42;
                        });
                    continuation = resultTask;
                    break;
                case ContinuationVariant.GenericAntecedentAction:
                    continuation = ControlledTask.ContinueWith(
                        genericAntecedent.Task,
                        _ => seen = ambient.Value);
                    break;
                case ContinuationVariant.GenericAntecedentResult:
                    resultTask = ControlledTask.ContinueWith(
                        genericAntecedent.Task,
                        task =>
                        {
                            seen = ambient.Value;
                            return task.Result * 2;
                        });
                    continuation = resultTask;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variantValue));
            }

            Assert.False(continuation.IsCompleted);
            Assert.Equal(-1, seen);
            ambient.Value = 9;

            bool generic = variant is ContinuationVariant.GenericAntecedentAction
                or ContinuationVariant.GenericAntecedentResult;
            if (generic)
            {
                genericAntecedent.SetResult(21);
            }
            else
            {
                antecedent.SetResult();
            }

            coordinator.Scheduler.DrainUntil(
                () => continuation.IsCompleted,
                "test",
                TestContext.Current.CancellationToken);

            Assert.Equal(5, seen);
            Assert.Equal(TaskStatus.RanToCompletion, continuation.Status);
            Assert.Equal(
                TaskStatus.RanToCompletion,
                generic ? genericAntecedent.Task.Status : antecedent.Task.Status);
            if (resultTask is not null)
            {
                Assert.Equal(42, resultTask.GetAwaiter().GetResult());
            }

            Assert.Equal(9, ambient.Value);
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    private enum QueuedTaskBodyVariant
    {
        TaskRunAction,
        TaskRunFunction,
        TaskFactoryStartNewAction,
        TaskFactoryStartNewFunction,
    }

    private enum ContinuationVariant
    {
        NonGenericAntecedentAction,
        NonGenericAntecedentResult,
        GenericAntecedentAction,
        GenericAntecedentResult,
    }

    [Theory]
    [InlineData((int)StartNewOceDelegateShape.Action, (int)StartNewOceCase.Bare)]
    [InlineData((int)StartNewOceDelegateShape.Action, (int)StartNewOceCase.UnmatchedRequested)]
    [InlineData((int)StartNewOceDelegateShape.Action, (int)StartNewOceCase.MatchingNotRequested)]
    [InlineData((int)StartNewOceDelegateShape.Action, (int)StartNewOceCase.MatchingRequested)]
    [InlineData((int)StartNewOceDelegateShape.Function, (int)StartNewOceCase.Bare)]
    [InlineData((int)StartNewOceDelegateShape.Function, (int)StartNewOceCase.UnmatchedRequested)]
    [InlineData((int)StartNewOceDelegateShape.Function, (int)StartNewOceCase.MatchingNotRequested)]
    [InlineData((int)StartNewOceDelegateShape.Function, (int)StartNewOceCase.MatchingRequested)]
    public void TaskFactoryStartNewClassifiesOperationCanceledExceptionLikeBcl(
        int delegateShapeValue,
        int oceCaseValue)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var delegateShape = (StartNewOceDelegateShape)delegateShapeValue;
            var oceCase = (StartNewOceCase)oceCaseValue;
            using var associatedSource = new CancellationTokenSource();
            using var unrelatedSource = new CancellationTokenSource();

            OperationCanceledException thrown;
            switch (oceCase)
            {
                case StartNewOceCase.Bare:
                    thrown = new OperationCanceledException();
                    break;
                case StartNewOceCase.UnmatchedRequested:
                    unrelatedSource.Cancel();
                    thrown = new OperationCanceledException(unrelatedSource.Token);
                    break;
                case StartNewOceCase.MatchingNotRequested:
                case StartNewOceCase.MatchingRequested:
                    thrown = new OperationCanceledException(associatedSource.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(oceCaseValue));
            }

            void ThrowAction()
            {
                if (oceCase == StartNewOceCase.MatchingRequested)
                {
                    associatedSource.Cancel();
                }

                throw thrown;
            }

            int ThrowFunction()
            {
                ThrowAction();
                return 42;
            }

            Task task;
#pragma warning disable xUnit1051 // Dedicated tokens are the contract inputs under test.
            if (delegateShape == StartNewOceDelegateShape.Action)
            {
                task = oceCase == StartNewOceCase.Bare
                    ? ControlledTaskFactory.StartNew(Task.Factory, ThrowAction)
                    : ControlledTaskFactory.StartNew(Task.Factory, ThrowAction, associatedSource.Token);
            }
            else
            {
                task = oceCase == StartNewOceCase.Bare
                    ? ControlledTaskFactory.StartNew(Task.Factory, ThrowFunction)
                    : ControlledTaskFactory.StartNew(Task.Factory, ThrowFunction, associatedSource.Token);
            }
#pragma warning restore xUnit1051

            Assert.False(task.IsCompleted);
            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test", TestContext.Current.CancellationToken);

            bool shouldCancel = oceCase == StartNewOceCase.MatchingRequested;
            Assert.Equal(shouldCancel, associatedSource.IsCancellationRequested);
            AssertOceTaskCompletion(task, thrown, shouldCancel);
        });
    }

    [Theory]
    [InlineData((int)ContinueWithOceShape.NonGenericAntecedentAction, (int)TokenlessOceCase.Bare)]
    [InlineData((int)ContinueWithOceShape.NonGenericAntecedentAction, (int)TokenlessOceCase.Requested)]
    [InlineData((int)ContinueWithOceShape.NonGenericAntecedentAction, (int)TokenlessOceCase.NotRequested)]
    [InlineData((int)ContinueWithOceShape.NonGenericAntecedentResult, (int)TokenlessOceCase.Bare)]
    [InlineData((int)ContinueWithOceShape.NonGenericAntecedentResult, (int)TokenlessOceCase.Requested)]
    [InlineData((int)ContinueWithOceShape.NonGenericAntecedentResult, (int)TokenlessOceCase.NotRequested)]
    [InlineData((int)ContinueWithOceShape.GenericAntecedentAction, (int)TokenlessOceCase.Bare)]
    [InlineData((int)ContinueWithOceShape.GenericAntecedentAction, (int)TokenlessOceCase.Requested)]
    [InlineData((int)ContinueWithOceShape.GenericAntecedentAction, (int)TokenlessOceCase.NotRequested)]
    [InlineData((int)ContinueWithOceShape.GenericAntecedentResult, (int)TokenlessOceCase.Bare)]
    [InlineData((int)ContinueWithOceShape.GenericAntecedentResult, (int)TokenlessOceCase.Requested)]
    [InlineData((int)ContinueWithOceShape.GenericAntecedentResult, (int)TokenlessOceCase.NotRequested)]
    public void ContinueWithWithoutAssociatedCancellationClassifiesOceAsFault(
        int shapeValue,
        int oceCaseValue)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var shape = (ContinueWithOceShape)shapeValue;
            var oceCase = (TokenlessOceCase)oceCaseValue;
            using var thrownSource = new CancellationTokenSource();
            if (oceCase == TokenlessOceCase.Requested)
            {
                thrownSource.Cancel();
            }

            var thrown = oceCase == TokenlessOceCase.Bare
                ? new OperationCanceledException()
                : new OperationCanceledException(thrownSource.Token);
            var antecedent = new TaskCompletionSource();
            var genericAntecedent = new TaskCompletionSource<int>();
            Task continuation;
            Action completeAntecedent;

            switch (shape)
            {
                case ContinueWithOceShape.NonGenericAntecedentAction:
                    continuation = ControlledTask.ContinueWith(
                        antecedent.Task,
                        (Action<Task>)(_ => throw thrown));
                    completeAntecedent = antecedent.SetResult;
                    break;
                case ContinueWithOceShape.NonGenericAntecedentResult:
                    continuation = ControlledTask.ContinueWith(
                        antecedent.Task,
                        (Func<Task, int>)(_ => throw thrown));
                    completeAntecedent = antecedent.SetResult;
                    break;
                case ContinueWithOceShape.GenericAntecedentAction:
                    continuation = ControlledTask.ContinueWith(
                        genericAntecedent.Task,
                        (Action<Task<int>>)(_ => throw thrown));
                    completeAntecedent = () => genericAntecedent.SetResult(21);
                    break;
                case ContinueWithOceShape.GenericAntecedentResult:
                    continuation = ControlledTask.ContinueWith(
                        genericAntecedent.Task,
                        (Func<Task<int>, int>)(_ => throw thrown));
                    completeAntecedent = () => genericAntecedent.SetResult(21);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shapeValue));
            }

            Assert.False(continuation.IsCompleted);
            coordinator.Scheduler.Schedule(completeAntecedent);
            coordinator.Scheduler.DrainUntil(
                () => continuation.IsCompleted,
                "test",
                TestContext.Current.CancellationToken);

            Assert.Equal(oceCase == TokenlessOceCase.Requested, thrownSource.IsCancellationRequested);
            AssertOceTaskCompletion(continuation, thrown, shouldCancel: false);
        });
    }

    [Fact]
    public void RunOfTaskPreservesCanceledInnerTaskToken()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var innerCancellation = new CancellationTokenSource();
            innerCancellation.Cancel();
#pragma warning disable xUnit1051 // The inner task's dedicated token is the contract input under test.
            Task inner = Task.FromCanceled(innerCancellation.Token);
#pragma warning restore xUnit1051

            Task outer = ControlledTask.Run(() => inner);
            coordinator.Scheduler.DrainUntil(() => outer.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal(TaskStatus.Canceled, inner.Status);
            Assert.Equal(TaskStatus.Canceled, outer.Status);
            Assert.True(outer.IsCanceled);
            Assert.Null(outer.Exception);
            var error = Assert.Throws<TaskCanceledException>(() => outer.GetAwaiter().GetResult());
            Assert.Equal(innerCancellation.Token, error.CancellationToken);
            AssertLoopIsClean(coordinator);
        });
    }

    [Fact]
    public void RunOfGenericTaskPreservesCanceledInnerTaskToken()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var innerCancellation = new CancellationTokenSource();
            innerCancellation.Cancel();
#pragma warning disable xUnit1051 // The inner task's dedicated token is the contract input under test.
            Task<int> inner = Task.FromCanceled<int>(innerCancellation.Token);
#pragma warning restore xUnit1051

            Task<int> outer = ControlledTask.Run(() => inner);
            coordinator.Scheduler.DrainUntil(() => outer.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal(TaskStatus.Canceled, inner.Status);
            Assert.Equal(TaskStatus.Canceled, outer.Status);
            Assert.True(outer.IsCanceled);
            Assert.Null(outer.Exception);
            var error = Assert.Throws<TaskCanceledException>(() => outer.GetAwaiter().GetResult());
            Assert.Equal(innerCancellation.Token, error.CancellationToken);
            AssertLoopIsClean(coordinator);
        });
    }

    [Fact]
    public void WaitAllNullElementMatchesBclExceptionShape()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Task[] tasks = [null!, Task.CompletedTask];

#pragma warning disable xUnit1051 // The overload without a token is the subject under test.
            var error = Assert.Throws<ArgumentException>(() => ControlledTask.WaitAll(tasks));
#pragma warning restore xUnit1051

            Assert.Equal("tasks", error.ParamName);
            Assert.True(tasks[1].IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void WaitAnyNullElementMatchesBclExceptionShape()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Task[] tasks = [null!, Task.CompletedTask];

#pragma warning disable xUnit1051 // The overload without a token is the subject under test.
            var error = Assert.Throws<ArgumentException>(() => ControlledTask.WaitAny(tasks));
#pragma warning restore xUnit1051

            Assert.Equal("tasks", error.ParamName);
            Assert.True(tasks[1].IsCompletedSuccessfully);
        });
    }

    private static void AssertOceTaskCompletion(
        Task task,
        OperationCanceledException thrown,
        bool shouldCancel)
    {
        if (shouldCancel)
        {
            Assert.Equal(TaskStatus.Canceled, task.Status);
            Assert.True(task.IsCanceled);
            Assert.Null(task.Exception);
            var awaited = Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
            Assert.Equal(thrown.CancellationToken, awaited.CancellationToken);
            return;
        }

        Assert.Equal(TaskStatus.Faulted, task.Status);
        Assert.False(task.IsCanceled);
        var aggregate = Assert.IsType<AggregateException>(task.Exception);
        var inner = Assert.Single(aggregate.InnerExceptions);
        var fault = Assert.IsType<OperationCanceledException>(inner);
        Assert.Same(thrown, fault);
        Assert.Equal(thrown.CancellationToken, fault.CancellationToken);
        var awaitedFault = Assert.Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult());
        Assert.Same(thrown, awaitedFault);
        Assert.Equal(thrown.CancellationToken, awaitedFault.CancellationToken);
    }

    private static void AssertLoopIsClean(SimulationSchedulerTestHost coordinator)
    {
        Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
        Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
        Assert.Null(coordinator.Scheduler.NextTimerDue);
        Assert.True(coordinator.Scheduler.IsIdle);
    }

    private enum StartNewOceDelegateShape
    {
        Action,
        Function,
    }

    private enum StartNewOceCase
    {
        Bare,
        UnmatchedRequested,
        MatchingNotRequested,
        MatchingRequested,
    }

    private enum ContinueWithOceShape
    {
        NonGenericAntecedentAction,
        NonGenericAntecedentResult,
        GenericAntecedentAction,
        GenericAntecedentResult,
    }

    private enum TokenlessOceCase
    {
        Bare,
        Requested,
        NotRequested,
    }

    [Theory]
    [InlineData((int)StartNewOceDelegateShape.Action, (int)StartNewOceCase.Bare)]
    [InlineData((int)StartNewOceDelegateShape.Action, (int)StartNewOceCase.UnmatchedRequested)]
    [InlineData((int)StartNewOceDelegateShape.Action, (int)StartNewOceCase.MatchingNotRequested)]
    [InlineData((int)StartNewOceDelegateShape.Action, (int)StartNewOceCase.MatchingRequested)]
    [InlineData((int)StartNewOceDelegateShape.Function, (int)StartNewOceCase.Bare)]
    [InlineData((int)StartNewOceDelegateShape.Function, (int)StartNewOceCase.UnmatchedRequested)]
    [InlineData((int)StartNewOceDelegateShape.Function, (int)StartNewOceCase.MatchingNotRequested)]
    [InlineData((int)StartNewOceDelegateShape.Function, (int)StartNewOceCase.MatchingRequested)]
    public void TaskFactoryStartNewOceClassificationLeavesPublicLoopClean(
        int delegateShapeValue,
        int oceCaseValue)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var delegateShape = (StartNewOceDelegateShape)delegateShapeValue;
            var oceCase = (StartNewOceCase)oceCaseValue;
            using var associatedSource = new CancellationTokenSource();
            using var unrelatedSource = new CancellationTokenSource();

            OperationCanceledException thrown;
            switch (oceCase)
            {
                case StartNewOceCase.Bare:
                    thrown = new OperationCanceledException();
                    break;
                case StartNewOceCase.UnmatchedRequested:
                    unrelatedSource.Cancel();
                    thrown = new OperationCanceledException(unrelatedSource.Token);
                    break;
                case StartNewOceCase.MatchingNotRequested:
                case StartNewOceCase.MatchingRequested:
                    thrown = new OperationCanceledException(associatedSource.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(oceCaseValue));
            }

            void ThrowAction()
            {
                if (oceCase == StartNewOceCase.MatchingRequested)
                {
                    associatedSource.Cancel();
                }

                throw thrown;
            }

            int ThrowFunction()
            {
                ThrowAction();
                return 42;
            }

            Task task;
#pragma warning disable xUnit1051 // Dedicated tokens are the contract inputs under test.
            if (delegateShape == StartNewOceDelegateShape.Action)
            {
                task = oceCase == StartNewOceCase.Bare
                    ? ControlledTaskFactory.StartNew(Task.Factory, ThrowAction)
                    : ControlledTaskFactory.StartNew(Task.Factory, ThrowAction, associatedSource.Token);
            }
            else
            {
                task = oceCase == StartNewOceCase.Bare
                    ? ControlledTaskFactory.StartNew(Task.Factory, ThrowFunction)
                    : ControlledTaskFactory.StartNew(Task.Factory, ThrowFunction, associatedSource.Token);
            }
#pragma warning restore xUnit1051

            Assert.False(task.IsCompleted);
            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test", TestContext.Current.CancellationToken);

            bool shouldCancel = oceCase == StartNewOceCase.MatchingRequested;
            Assert.Equal(shouldCancel, associatedSource.IsCancellationRequested);
            AssertOceTaskCompletion(task, thrown, shouldCancel);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsClean(coordinator);
        });
    }

    [Theory]
    [InlineData((int)ContinueWithOceShape.NonGenericAntecedentAction, (int)TokenlessOceCase.Bare)]
    [InlineData((int)ContinueWithOceShape.NonGenericAntecedentAction, (int)TokenlessOceCase.Requested)]
    [InlineData((int)ContinueWithOceShape.NonGenericAntecedentAction, (int)TokenlessOceCase.NotRequested)]
    [InlineData((int)ContinueWithOceShape.NonGenericAntecedentResult, (int)TokenlessOceCase.Bare)]
    [InlineData((int)ContinueWithOceShape.NonGenericAntecedentResult, (int)TokenlessOceCase.Requested)]
    [InlineData((int)ContinueWithOceShape.NonGenericAntecedentResult, (int)TokenlessOceCase.NotRequested)]
    [InlineData((int)ContinueWithOceShape.GenericAntecedentAction, (int)TokenlessOceCase.Bare)]
    [InlineData((int)ContinueWithOceShape.GenericAntecedentAction, (int)TokenlessOceCase.Requested)]
    [InlineData((int)ContinueWithOceShape.GenericAntecedentAction, (int)TokenlessOceCase.NotRequested)]
    [InlineData((int)ContinueWithOceShape.GenericAntecedentResult, (int)TokenlessOceCase.Bare)]
    [InlineData((int)ContinueWithOceShape.GenericAntecedentResult, (int)TokenlessOceCase.Requested)]
    [InlineData((int)ContinueWithOceShape.GenericAntecedentResult, (int)TokenlessOceCase.NotRequested)]
    public void ContinueWithOceClassificationLeavesPublicLoopClean(
        int shapeValue,
        int oceCaseValue)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var shape = (ContinueWithOceShape)shapeValue;
            var oceCase = (TokenlessOceCase)oceCaseValue;
            using var thrownSource = new CancellationTokenSource();
            if (oceCase == TokenlessOceCase.Requested)
            {
                thrownSource.Cancel();
            }

            var thrown = oceCase == TokenlessOceCase.Bare
                ? new OperationCanceledException()
                : new OperationCanceledException(thrownSource.Token);
            var antecedent = new TaskCompletionSource();
            var genericAntecedent = new TaskCompletionSource<int>();
            Task continuation;
            Action completeAntecedent;

            switch (shape)
            {
                case ContinueWithOceShape.NonGenericAntecedentAction:
                    continuation = ControlledTask.ContinueWith(
                        antecedent.Task,
                        (Action<Task>)(_ => throw thrown));
                    completeAntecedent = antecedent.SetResult;
                    break;
                case ContinueWithOceShape.NonGenericAntecedentResult:
                    continuation = ControlledTask.ContinueWith(
                        antecedent.Task,
                        (Func<Task, int>)(_ => throw thrown));
                    completeAntecedent = antecedent.SetResult;
                    break;
                case ContinueWithOceShape.GenericAntecedentAction:
                    continuation = ControlledTask.ContinueWith(
                        genericAntecedent.Task,
                        (Action<Task<int>>)(_ => throw thrown));
                    completeAntecedent = () => genericAntecedent.SetResult(21);
                    break;
                case ContinueWithOceShape.GenericAntecedentResult:
                    continuation = ControlledTask.ContinueWith(
                        genericAntecedent.Task,
                        (Func<Task<int>, int>)(_ => throw thrown));
                    completeAntecedent = () => genericAntecedent.SetResult(21);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shapeValue));
            }

            Assert.False(continuation.IsCompleted);
            coordinator.Scheduler.Schedule(completeAntecedent);
            coordinator.Scheduler.DrainUntil(
                () => continuation.IsCompleted,
                "test",
                TestContext.Current.CancellationToken);

            Assert.Equal(oceCase == TokenlessOceCase.Requested, thrownSource.IsCancellationRequested);
            AssertOceTaskCompletion(continuation, thrown, shouldCancel: false);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsClean(coordinator);
        });
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void NullWaitElementValidationPrecedesPumpingAndLeavesLoopClean(
        bool waitAll,
        bool nullFirst)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var pending = new TaskCompletionSource();
            Task[] tasks = nullFirst
                ? [null!, pending.Task]
                : [pending.Task, null!];

            Exception? error = Record.Exception(() =>
            {
                if (waitAll)
                {
#pragma warning disable xUnit1051 // The overload without a token is the subject under test.
                    ControlledTask.WaitAll(tasks);
#pragma warning restore xUnit1051
                }
                else
                {
#pragma warning disable xUnit1051 // The overload without a token is the subject under test.
                    _ = ControlledTask.WaitAny(tasks);
#pragma warning restore xUnit1051
                }
            });

            Assert.False(pending.Task.IsCompleted);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            AssertLoopIsClean(coordinator);
            var argument = Assert.IsType<ArgumentException>(error);
            Assert.Equal("tasks", argument.ParamName);
        });
    }
}
