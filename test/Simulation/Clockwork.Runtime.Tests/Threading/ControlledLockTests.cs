using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

/// <summary>
/// Tests for <see cref="ControlledLock"/>, the controlled stand-in for <see cref="System.Threading.Lock"/>.
/// Inside a simulation the lock is modelled on the controlled monitor kernel (mutual exclusion,
/// reentrancy, <see cref="ControlledLock.Scope"/> disposal releasing exactly once). Controlled entry
/// points require an active simulation.
/// </summary>
public sealed class ControlledLockTests
{
    [Fact]
    public void EnterScopeAcquiresAndDisposeReleases()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new ControlledLock();

            Assert.False(gate.IsHeldByCurrentThread);
            var scope = gate.EnterScope();
            Assert.True(gate.IsHeldByCurrentThread);
            scope.Dispose();
            Assert.False(gate.IsHeldByCurrentThread);
        });
    }

    [Fact]
    public void EnterExitTrackReentrancy()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new ControlledLock();

            gate.Enter();
            gate.Enter();
            gate.Exit();
            Assert.True(gate.IsHeldByCurrentThread);
            gate.Exit();
            Assert.False(gate.IsHeldByCurrentThread);
        });
    }

    [Fact]
    public void TryEnterContendedReturnsFalse()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new ControlledLock();
            var tookIt = true;

            gate.Enter();
            var contender = ControlledThread.Create(() => tookIt = gate.TryEnter());
            ControlledThread.Start(contender);
            ControlledThread.Join(contender);
            gate.Exit();

            Assert.False(tookIt);
        });
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new ControlledLock();
            var scope = gate.EnterScope();
            scope.Dispose();
            scope.Dispose();
            Assert.False(gate.IsHeldByCurrentThread);
        });
    }

    [Fact]
    public void OutsideSimulationFailsBeforeCreatingLock()
    {
        ControlledLock? gate = null;

        Exception? exception = Record.Exception(() => gate = new ControlledLock());

        Assert.Null(gate);
        SimulationNotActiveExceptionAssert.Equal(exception, "System.Threading.Lock..ctor");
    }

    [Fact]
    public void SafeThreadPoolCallbackDoesNotInheritLockOwnership()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new ControlledLock();
            var nestedWasOwner = true;
            var nestedAcquired = true;

            gate.Enter();
            var accepted = ControlledThreadPool.QueueUserWorkItem(
                _ =>
                {
                    nestedWasOwner = gate.IsHeldByCurrentThread;
                    nestedAcquired = gate.TryEnter();
                    if (nestedAcquired)
                    {
                        gate.Exit();
                    }
                },
                state: null);

            Assert.True(accepted);
            Assert.Equal(1, coordinator.Scheduler.RunnableOperationCount);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.True(gate.IsHeldByCurrentThread);
            gate.Exit();
            Assert.False(gate.IsHeldByCurrentThread);
            Assert.False(nestedWasOwner);
            Assert.False(nestedAcquired);
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Theory]
    [InlineData((int)QueuedTaskVariant.TaskRun)]
    [InlineData((int)QueuedTaskVariant.TaskFactoryStartNew)]
    [InlineData((int)QueuedTaskVariant.ContinueWith)]
    public void QueuedTaskWorkDoesNotInheritLockOwnership(int variantValue)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var variant = (QueuedTaskVariant)variantValue;
            var gate = new ControlledLock();
            var nestedWasOwner = true;
            var nestedAcquired = true;
            var antecedent = new TaskCompletionSource();

            void ProbeOwnership()
            {
                nestedWasOwner = gate.IsHeldByCurrentThread;
                nestedAcquired = gate.TryEnter();
                if (nestedAcquired)
                {
                    gate.Exit();
                }
            }

            gate.Enter();
            Task task = variant switch
            {
                QueuedTaskVariant.TaskRun => ControlledTask.Run(ProbeOwnership),
                QueuedTaskVariant.TaskFactoryStartNew =>
                    ControlledTaskFactory.StartNew(Task.Factory, ProbeOwnership),
                QueuedTaskVariant.ContinueWith =>
                    ControlledTask.ContinueWith(antecedent.Task, _ => ProbeOwnership()),
                _ => throw new ArgumentOutOfRangeException(nameof(variantValue)),
            };

            Assert.False(task.IsCompleted);
            if (variant == QueuedTaskVariant.ContinueWith)
            {
                antecedent.SetResult();
            }

            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal(TaskStatus.RanToCompletion, task.Status);
            Assert.True(gate.IsHeldByCurrentThread);
            gate.Exit();
            Assert.False(gate.IsHeldByCurrentThread);
            Assert.False(nestedWasOwner);
            Assert.False(nestedAcquired);
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    private enum QueuedTaskVariant
    {
        TaskRun,
        TaskFactoryStartNew,
        ContinueWith,
    }

    [Theory]
    [InlineData((int)NestedQueueVariant.SafeWaitCallback)]
    [InlineData((int)NestedQueueVariant.SafeGeneric)]
    [InlineData((int)NestedQueueVariant.UnsafeWaitCallback)]
    [InlineData((int)NestedQueueVariant.UnsafeGeneric)]
    [InlineData((int)NestedQueueVariant.UnsafeWorkItem)]
    [InlineData((int)NestedQueueVariant.TaskRun)]
    [InlineData((int)NestedQueueVariant.TaskFactoryStartNew)]
    [InlineData((int)NestedQueueVariant.ContinueWith)]
    public void NestedQueuedOperationsUseDistinctLockOwners(int variantValue)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var variant = (NestedQueueVariant)variantValue;
            var gate = new ControlledLock();
            var outerOwnedBeforeInner = false;
            var outerOwnedAfterInner = false;
            var outerOwnedAfterExit = true;
            var innerWasOwner = true;
            var innerAcquired = true;
            var innerOwnedAfterTry = true;
            QueuedOperation? innerOperation = null;

            var outerOperation = QueueOperation(variant, () =>
            {
                gate.Enter();
                outerOwnedBeforeInner = gate.IsHeldByCurrentThread;

                innerOperation = QueueOperation(variant, () =>
                {
                    innerWasOwner = gate.IsHeldByCurrentThread;
                    innerAcquired = gate.TryEnter();
                    if (innerAcquired)
                    {
                        gate.Exit();
                    }

                    innerOwnedAfterTry = gate.IsHeldByCurrentThread;
                });

                coordinator.Scheduler.DrainUntil(
                    () => innerOperation.IsCompleted,
                    "nested queued Lock ownership probe",
                    TestContext.Current.CancellationToken);

                outerOwnedAfterInner = gate.IsHeldByCurrentThread;
                gate.Exit();
                outerOwnedAfterExit = gate.IsHeldByCurrentThread;
            });

            Assert.False(outerOperation.IsCompleted);
            coordinator.Scheduler.DrainUntil(
                () => outerOperation.IsCompleted,
                "outer queued Lock ownership probe",
                TestContext.Current.CancellationToken);

            AssertQueuedOperationCompleted(outerOperation);
            Assert.NotNull(innerOperation);
            AssertQueuedOperationCompleted(innerOperation);
            Assert.True(outerOwnedBeforeInner);
            Assert.False(innerWasOwner);
            Assert.False(innerAcquired);
            Assert.False(innerOwnedAfterTry);
            Assert.True(outerOwnedAfterInner);
            Assert.False(outerOwnedAfterExit);
            Assert.False(gate.IsHeldByCurrentThread);
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    private static QueuedOperation QueueOperation(NestedQueueVariant variant, Action callback)
    {
        var operation = new QueuedOperation(variant);

        void Execute()
        {
            callback();
            operation.CallbackCount++;
        }

        switch (variant)
        {
            case NestedQueueVariant.SafeWaitCallback:
                operation.WasAccepted = ControlledThreadPool.QueueUserWorkItem(
                    static state => ((Action)state!)(),
                    (Action)Execute);
                break;
            case NestedQueueVariant.SafeGeneric:
                operation.WasAccepted = ControlledThreadPool.QueueUserWorkItem(
                    static action => action(),
                    (Action)Execute,
                    preferLocal: true);
                break;
            case NestedQueueVariant.UnsafeWaitCallback:
                operation.WasAccepted = ControlledThreadPool.UnsafeQueueUserWorkItem(
                    static state => ((Action)state!)(),
                    (Action)Execute);
                break;
            case NestedQueueVariant.UnsafeGeneric:
                operation.WasAccepted = ControlledThreadPool.UnsafeQueueUserWorkItem(
                    static action => action(),
                    (Action)Execute,
                    preferLocal: false);
                break;
            case NestedQueueVariant.UnsafeWorkItem:
                operation.WasAccepted = ControlledThreadPool.UnsafeQueueUserWorkItem(
                    new DelegateWorkItem(Execute),
                    preferLocal: true);
                break;
            case NestedQueueVariant.TaskRun:
                operation.Completion = ControlledTask.Run(() =>
                {
                    Execute();
                    return QueuedOperation.ExpectedResult;
                });
                break;
            case NestedQueueVariant.TaskFactoryStartNew:
                operation.Completion = ControlledTaskFactory.StartNew(Task.Factory, () =>
                {
                    Execute();
                    return QueuedOperation.ExpectedResult;
                });
                break;
            case NestedQueueVariant.ContinueWith:
                var antecedent = new TaskCompletionSource<int>();
                operation.Antecedent = antecedent.Task;
                operation.Completion = ControlledTask.ContinueWith<int, int>(
                    antecedent.Task,
                    completed =>
                    {
                        operation.ObservedAntecedentResult = completed.Result;
                        Execute();
                        return QueuedOperation.ExpectedResult;
                    });
                antecedent.SetResult(QueuedOperation.ExpectedAntecedentResult);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }

        return operation;
    }

    private static void AssertQueuedOperationCompleted(QueuedOperation operation)
    {
        Assert.True(operation.IsCompleted);
        Assert.Equal(1, operation.CallbackCount);

        switch (operation.Variant)
        {
            case NestedQueueVariant.SafeWaitCallback:
            case NestedQueueVariant.SafeGeneric:
            case NestedQueueVariant.UnsafeWaitCallback:
            case NestedQueueVariant.UnsafeGeneric:
            case NestedQueueVariant.UnsafeWorkItem:
                Assert.Equal(true, operation.WasAccepted);
                break;
            case NestedQueueVariant.TaskRun:
            case NestedQueueVariant.TaskFactoryStartNew:
                var completion = operation.Completion;
                Assert.NotNull(completion);
                Assert.Equal(TaskStatus.RanToCompletion, completion.Status);
                Assert.Equal(QueuedOperation.ExpectedResult, completion.Result);
                break;
            case NestedQueueVariant.ContinueWith:
                var antecedent = operation.Antecedent;
                Assert.NotNull(antecedent);
                Assert.Equal(TaskStatus.RanToCompletion, antecedent.Status);
                Assert.Equal(QueuedOperation.ExpectedAntecedentResult, antecedent.Result);
                Assert.Equal(QueuedOperation.ExpectedAntecedentResult, operation.ObservedAntecedentResult);
                var continuation = operation.Completion;
                Assert.NotNull(continuation);
                Assert.Equal(TaskStatus.RanToCompletion, continuation.Status);
                Assert.Equal(QueuedOperation.ExpectedResult, continuation.Result);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation.Variant, null);
        }
    }

    private enum NestedQueueVariant
    {
        SafeWaitCallback,
        SafeGeneric,
        UnsafeWaitCallback,
        UnsafeGeneric,
        UnsafeWorkItem,
        TaskRun,
        TaskFactoryStartNew,
        ContinueWith,
    }

    private sealed class QueuedOperation(NestedQueueVariant variant)
    {
        public const int ExpectedAntecedentResult = 37;
        public const int ExpectedResult = 73;

        public NestedQueueVariant Variant { get; } = variant;

        public bool? WasAccepted { get; set; }

        public int CallbackCount { get; set; }

        public int? ObservedAntecedentResult { get; set; }

        public Task<int>? Antecedent { get; set; }

        public Task<int>? Completion { get; set; }

        public bool IsCompleted => Variant switch
        {
            NestedQueueVariant.SafeWaitCallback
                or NestedQueueVariant.SafeGeneric
                or NestedQueueVariant.UnsafeWaitCallback
                or NestedQueueVariant.UnsafeGeneric
                or NestedQueueVariant.UnsafeWorkItem => CallbackCount == 1,
            NestedQueueVariant.TaskRun
                or NestedQueueVariant.TaskFactoryStartNew
                or NestedQueueVariant.ContinueWith => Completion?.IsCompleted == true,
            _ => throw new ArgumentOutOfRangeException(nameof(Variant)),
        };
    }

    private sealed class DelegateWorkItem(Action execute) : IThreadPoolWorkItem
    {
        public void Execute() => execute();
    }
}
