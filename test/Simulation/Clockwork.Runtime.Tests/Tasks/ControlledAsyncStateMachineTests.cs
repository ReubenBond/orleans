using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

namespace Orleans.Testing.Simulation.Runtime.Tests.Tasks;

/// <summary>
/// <para>
/// End-to-end tests of the controlled async pipeline that do <b>not</b> depend on the Cecil rewriter:
/// each test hand-authors an <see cref="IAsyncStateMachine"/> exactly the way the C# compiler would -
/// a controlled builder field plus controlled awaiter fields - and drives it through the coordinator's
/// loop. This proves the runtime semantics (suspend/resume, success/fault/cancel, ConfigureAwait(false)
/// staying controlled, Task.Yield) independently of the rewriting pass that will later produce these
/// state machines automatically.
/// </para>
/// </summary>
public sealed class ControlledAsyncStateMachineTests
{
    [Fact]
    public void AwaitedIncompleteTasksResumeDeterministicallyThroughTheLoop()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var a = new TaskCompletionSource<int>();
            var b = new TaskCompletionSource<int>();

            var result = AddAsync(a.Task, b.Task);

            // Suspended at the first await: nothing has completed the antecedents yet.
            Assert.False(result.IsCompleted);

            coordinator.Scheduler.Schedule(() => a.SetResult(3));
            coordinator.Scheduler.Schedule(() => b.SetResult(4));
            coordinator.Scheduler.DrainUntil(() => result.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal(TaskStatus.RanToCompletion, result.Status);
            Assert.Equal(7, result.Result);
        });
    }

    [Fact]
    public void SynchronouslyCompletedAntecedentsNeverSuspend()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var result = AddAsync(Task.FromResult(10), Task.FromResult(5));

            // Both awaits saw IsCompleted == true, so the method ran to completion inside Start.
            Assert.True(result.IsCompleted);
            Assert.Equal(15, result.Result);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void FaultedAntecedentPropagatesTheOriginalException()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var a = new TaskCompletionSource<int>();
            var b = new TaskCompletionSource<int>();
            var result = AddAsync(a.Task, b.Task);

            var boom = new InvalidTimeZoneException("boom");
            coordinator.Scheduler.Schedule(() => a.SetException(boom));
            coordinator.Scheduler.DrainUntil(() => result.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal(TaskStatus.Faulted, result.Status);
            Assert.Same(boom, result.Exception!.InnerException);
        });
    }

    [Fact]
    public void CancelledAntecedentCancelsTheResultingTask()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var a = new TaskCompletionSource<int>();
            var b = new TaskCompletionSource<int>();
            var result = AddAsync(a.Task, b.Task);

            coordinator.Scheduler.Schedule(() => a.SetCanceled());
            coordinator.Scheduler.DrainUntil(() => result.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal(TaskStatus.Canceled, result.Status);
        });
    }

    [Fact]
    public void ContinuationNeverRunsInlineOnAntecedentCompletion()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var a = new TaskCompletionSource<int>();
            var result = AddAsync(a.Task, Task.FromResult(1));

            var resumedInline = false;
            coordinator.Scheduler.Schedule(() =>
            {
                a.SetResult(41);

                // Completing the antecedent must NOT synchronously resume the state machine; the
                // continuation is queued on the loop and only runs on a later pump.
                resumedInline = result.IsCompleted;
            });

            coordinator.Scheduler.DrainUntil(() => result.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.False(resumedInline);
            Assert.Equal(42, result.Result);
        });
    }

    [Fact]
    public void ConfigureAwaitFalseStaysControlled()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var a = new TaskCompletionSource<int>();
            var result = ConfiguredAddOneAsync(a.Task);

            Assert.False(result.IsCompleted);

            // The only way this resumes is through the coordinator's loop. If ConfigureAwait(false) had
            // escaped to the thread pool, pumping the loop alone would never complete it.
            coordinator.Scheduler.Schedule(() => a.SetResult(99));
            coordinator.Scheduler.DrainUntil(() => result.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal(100, result.Result);
        });
    }

    [Fact]
    public void YieldSuspendsThenResumesThroughTheLoop()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var result = YieldThenReturnAsync();

            // Yield always suspends, so the result is not complete until the loop runs the continuation.
            Assert.False(result.IsCompleted);
            Assert.Equal(1, coordinator.Scheduler.RunnableOperationCount);

            coordinator.Scheduler.DrainUntil(() => result.IsCompleted, "test", TestContext.Current.CancellationToken);
            Assert.Equal(7, result.Result);
        });
    }

    // ---- Hand-written state machines mirroring compiler output ----

    private static Task<int> AddAsync(Task<int> a, Task<int> b)
    {
        var stateMachine = new AddStateMachine
        {
            State = -1,
            Builder = ControlledAsyncTaskMethodBuilder<int>.Create(),
            A = a,
            B = b,
        };
        stateMachine.Builder.Start(ref stateMachine);
        return stateMachine.Builder.Task;
    }

    private static Task<int> ConfiguredAddOneAsync(Task<int> a)
    {
        var stateMachine = new ConfiguredAddOneStateMachine
        {
            State = -1,
            Builder = ControlledAsyncTaskMethodBuilder<int>.Create(),
            A = a,
        };
        stateMachine.Builder.Start(ref stateMachine);
        return stateMachine.Builder.Task;
    }

    private static Task<int> YieldThenReturnAsync()
    {
        var stateMachine = new YieldStateMachine
        {
            State = -1,
            Builder = ControlledAsyncTaskMethodBuilder<int>.Create(),
        };
        stateMachine.Builder.Start(ref stateMachine);
        return stateMachine.Builder.Task;
    }

    private struct AddStateMachine : IAsyncStateMachine
    {
        public int State;
        public ControlledAsyncTaskMethodBuilder<int> Builder;
        public Task<int> A;
        public Task<int> B;
        private int _sum;
        private ControlledTaskAwaiter<int> _awaiter;

        public void MoveNext()
        {
            try
            {
                switch (State)
                {
                    case -1:
                        _awaiter = new ControlledTaskAwaiter<int>(A);
                        if (!_awaiter.IsCompleted)
                        {
                            State = 0;
                            Builder.AwaitUnsafeOnCompleted(ref _awaiter, ref this);
                            return;
                        }

                        goto case 0;
                    case 0:
                        _sum = _awaiter.GetResult();
                        _awaiter = new ControlledTaskAwaiter<int>(B);
                        if (!_awaiter.IsCompleted)
                        {
                            State = 1;
                            Builder.AwaitUnsafeOnCompleted(ref _awaiter, ref this);
                            return;
                        }

                        goto case 1;
                    case 1:
                        _sum += _awaiter.GetResult();
                        Builder.SetResult(_sum);
                        return;
                }
            }
            catch (Exception ex)
            {
                Builder.SetException(ex);
            }
        }

        public readonly void SetStateMachine(IAsyncStateMachine stateMachine) =>
            Builder.SetStateMachine(stateMachine);
    }

    private struct ConfiguredAddOneStateMachine : IAsyncStateMachine
    {
        public int State;
        public ControlledAsyncTaskMethodBuilder<int> Builder;
        public Task<int> A;
        private ControlledConfiguredTaskAwaiter<int> _awaiter;

        public void MoveNext()
        {
            try
            {
                int value;
                if (State == -1)
                {
                    _awaiter = new ControlledConfiguredTaskAwaitable<int>(A, continueOnCapturedContext: false)
                        .GetAwaiter();
                    if (!_awaiter.IsCompleted)
                    {
                        State = 0;
                        Builder.AwaitUnsafeOnCompleted(ref _awaiter, ref this);
                        return;
                    }
                }

                value = _awaiter.GetResult();
                Builder.SetResult(value + 1);
            }
            catch (Exception ex)
            {
                Builder.SetException(ex);
            }
        }

        public readonly void SetStateMachine(IAsyncStateMachine stateMachine) =>
            Builder.SetStateMachine(stateMachine);
    }

    private struct YieldStateMachine : IAsyncStateMachine
    {
        public int State;
        public ControlledAsyncTaskMethodBuilder<int> Builder;
        private ControlledYieldAwaiter _awaiter;

        public void MoveNext()
        {
            try
            {
                if (State == -1)
                {
                    _awaiter = new ControlledYieldAwaitable().GetAwaiter();
                    if (!_awaiter.IsCompleted)
                    {
                        State = 0;
                        Builder.AwaitUnsafeOnCompleted(ref _awaiter, ref this);
                        return;
                    }
                }

                _awaiter.GetResult();
                Builder.SetResult(7);
            }
            catch (Exception ex)
            {
                Builder.SetException(ex);
            }
        }

        public readonly void SetStateMachine(IAsyncStateMachine stateMachine) =>
            Builder.SetStateMachine(stateMachine);
    }
}
