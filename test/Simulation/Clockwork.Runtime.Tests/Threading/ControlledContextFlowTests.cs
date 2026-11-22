using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;
#pragma warning disable SYSLIB0051
#pragma warning disable CS0618

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

/// <summary>Focused coverage for controlled execution- and synchronization-context rewrite targets.</summary>
public sealed class ControlledContextFlowTests
{
    [Fact]
    public void ExecutionContextCaptureAndRunFlowUserAsyncLocalsWithoutAliasingTheControlledStrand()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ambient = new AsyncLocal<int> { Value = 5 };
            ExecutionContext captured = Assert.IsType<ExecutionContext>(ControlledExecutionContext.Capture());
            ExecutionContext copy = ControlledExecutionContext.CreateCopy(captured);
            var outerStrand = SimulationSynchronizationFlow.CurrentId;
            var seen = -1;
            var seenStrand = SimulationSynchronizationFlow.None;

            ambient.Value = 9;
            ControlledExecutionContext.Run(
                captured,
                _ =>
                {
                    seen = ambient.Value;
                    seenStrand = SimulationSynchronizationFlow.CurrentId;
                },
                state: null);

            Assert.Equal(5, seen);
            Assert.Equal(outerStrand, seenStrand);
            Assert.Equal(9, ambient.Value);
            ControlledExecutionContext.Dispose(copy);
        });
    }

    [Fact]
    public void ExecutionContextSuppressAndRestoreFlowMatchBclSemantics()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.False(ControlledExecutionContext.IsFlowSuppressed());

            AsyncFlowControl control = ControlledExecutionContext.SuppressFlow();
            Assert.True(ControlledExecutionContext.IsFlowSuppressed());
            Assert.Null(ControlledExecutionContext.Capture());

            ControlledExecutionContext.RestoreFlow();
            Assert.False(ControlledExecutionContext.IsFlowSuppressed());

            // RestoreFlow consumes the suppression, so the returned control must not be undone again.
            GC.KeepAlive(control);
        });
    }

    [Fact]
    public void ExecutionContextRestorePreservesTheActiveSimulationAndControlledStrand()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ambient = new AsyncLocal<int> { Value = 5 };
            ExecutionContext captured = Assert.IsType<ExecutionContext>(ControlledExecutionContext.Capture());
            var expectedStrand = SimulationSynchronizationFlow.CurrentId;
            ambient.Value = 9;

            ControlledExecutionContext.Restore(captured);

            Assert.True(SimulationTaskRuntime.IsSimulationActive);
            Assert.Equal(5, ambient.Value);
            Assert.Equal(expectedStrand, SimulationSynchronizationFlow.CurrentId);
        });
    }

    [Fact]
    public void ExecutionContextsCapturedOutsideSimulationReestablishSimulationIdentityForRunAndRestore()
    {
        var outsideAmbient = new AsyncLocal<int> { Value = 5 };
        ExecutionContext outside = Assert.IsType<ExecutionContext>(ExecutionContext.Capture());
        ExecutionContext outsideCopy = outside.CreateCopy();
        outsideAmbient.Value = 0;

        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            outsideAmbient.Value = 9;
            SimulationExecutionSnapshot expected = Assert.IsType<SimulationExecutionSnapshot>(SimulationExecutionContext.Current);
            long strand = SimulationSynchronizationFlow.CurrentId;

            ControlledExecutionContext.Run(
                outside,
                _ =>
                {
                    Assert.Equal(5, outsideAmbient.Value);
                    Assert.True(SimulationTaskRuntime.IsSimulationActive);
                    Assert.Equal(expected, SimulationExecutionContext.Current);
                    Assert.Equal(strand, SimulationSynchronizationFlow.CurrentId);
                    AutoResetEvent handle = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: true);
                    Assert.True(ControlledWaitHandle.WaitOne(handle, 0));
                },
                state: null);

            Assert.Equal(9, outsideAmbient.Value);
            ControlledExecutionContext.Restore(outsideCopy);
            Assert.Equal(5, outsideAmbient.Value);
            Assert.True(SimulationTaskRuntime.IsSimulationActive);
            Assert.Equal(expected, SimulationExecutionContext.Current);
            Assert.Equal(strand, SimulationSynchronizationFlow.CurrentId);
            ManualResetEvent restoredHandle = ControlledEventWaitHandle.CreateManualResetEvent(initialState: true);
            Assert.True(ControlledWaitHandle.WaitOne(restoredHandle, 0));
        });
    }

    [Fact]
    public void NewSafeOperationsFlowUserContextButReceiveDistinctControlledStrands()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ambient = new AsyncLocal<int> { Value = 5 };
            var values = new List<(int Value, long Strand)>();

            ControlledThreadPool.QueueUserWorkItem(_ => values.Add((ambient.Value, SimulationSynchronizationFlow.CurrentId)));
            ControlledTask.Run(() => values.Add((ambient.Value, SimulationSynchronizationFlow.CurrentId)));
            ambient.Value = 9;

            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.Equal(2, values.Count);
            Assert.All(values, entry =>
            {
                Assert.Equal(5, entry.Value);
                Assert.NotEqual(SimulationSynchronizationFlow.None, entry.Strand);
            });
            Assert.NotEqual(values[0].Strand, values[1].Strand);
        });
    }

    [Fact]
    public void ScheduledContinuationsHonorSafeAndUnsafeExecutionContextFlow()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ambient = new AsyncLocal<int> { Value = 5 };
            var safe = -1;
            var unsafeValue = -1;

            SimulationTaskRuntime.ScheduleContinuation(
                Task.CompletedTask,
                () => safe = ambient.Value,
                "test.safe-continuation",
                flowExecutionContext: true);
            SimulationTaskRuntime.ScheduleContinuation(
                Task.CompletedTask,
                () => unsafeValue = ambient.Value,
                "test.unsafe-continuation",
                flowExecutionContext: false);
            ambient.Value = 9;

            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.Equal(5, safe);
            Assert.Equal(0, unsafeValue);
        });
    }

    [Fact]
    public void SynchronizationContextIsLogicalAndPostAndSendNeverInvokeCustomDispatch()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var context = new EscapingSynchronizationContext();
            var postRan = false;
            var sendRan = false;

            ControlledSynchronizationContext.SetSynchronizationContext(context);
            try
            {
                Assert.Same(context, ControlledSynchronizationContext.Current());
                Assert.Same(context, ControlledSynchronizationContext.CreateCopy(context));
                Assert.False(ControlledSynchronizationContext.IsWaitNotificationRequired(context));

                ControlledSynchronizationContext.OperationStarted(context);
                ControlledSynchronizationContext.OperationCompleted(context);
                ControlledSynchronizationContext.Post(
                    context,
                    _ =>
                    {
                        postRan = true;
                        context.ObservePostContext();
                    },
                    state: null);
                ControlledSynchronizationContext.Send(context, _ => sendRan = true, state: null);

                Assert.True(sendRan);
                Assert.False(postRan);
                Assert.Equal(0, context.PostCalls);
                Assert.Equal(0, context.SendCalls);
                Assert.Equal(1, context.Started);
                Assert.Equal(1, context.Completed);

                coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
                Assert.True(postRan);
                Assert.Null(context.ObservedPostContext);
            }
            finally
            {
                ControlledSynchronizationContext.SetSynchronizationContext(null);
            }
        });
    }

    [Fact]
    public void SynchronizationContextRawWaitIsRejectedBeforeBlocking()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ex = Assert.Throws<SimulationApiException>(
                () => ControlledSynchronizationContext.Wait(new SynchronizationContext(), [IntPtr.Zero], waitAll: false, millisecondsTimeout: 0));
            Assert.Equal("System.Threading.SynchronizationContext.Wait", ex.ApiName);
        });
    }

    [Fact]
    public void SynchronizationContextIsStrandScopedAndDoesNotFlowWithExecutionContext()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var first = new SynchronizationContext();
            var second = new SynchronizationContext();
            ControlledSynchronizationContext.SetSynchronizationContext(first);
            ExecutionContext captured = Assert.IsType<ExecutionContext>(ExecutionContext.Capture());

            ControlledSynchronizationContext.SetSynchronizationContext(second);
            ExecutionContext.Run(
                captured,
                _ => Assert.Same(second, ControlledSynchronizationContext.Current()),
                state: null);

            SynchronizationContext? childContext = first;
            ControlledThreadPool.QueueUserWorkItem(_ => childContext = ControlledSynchronizationContext.Current());
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.Null(childContext);
            ControlledSynchronizationContext.SetSynchronizationContext(first);
            Assert.Same(first, ControlledSynchronizationContext.Current());
            ControlledSynchronizationContext.SetSynchronizationContext(null);
            Assert.Null(ControlledSynchronizationContext.Current());
        });
    }

    [Fact]
    public void SynchronizationContextRegistryIsWeaklyScopedToRuntimeObjects()
    {
        var registry = typeof(ControlledSynchronizationContext).GetField(
            "Contexts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(registry);
        Assert.True(registry.FieldType.IsGenericType);
        Assert.Equal(typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>), registry.FieldType.GetGenericTypeDefinition());
        Assert.Equal(typeof(SimulationRuntimeIdentity), registry.FieldType.GetGenericArguments()[0]);
    }

    [Fact]
    public void NewContextEntriesRequireAnActiveSimulationBeforeValidatingArguments()
    {
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(ControlledExecutionContext.Capture),
            "System.Threading.ExecutionContext.Capture");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledExecutionContext.Run(null!, null!, null)),
            "System.Threading.ExecutionContext.Run");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledExecutionContext.SuppressFlow()),
            "System.Threading.ExecutionContext.SuppressFlow");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(ControlledExecutionContext.RestoreFlow),
            "System.Threading.ExecutionContext.RestoreFlow");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledExecutionContext.IsFlowSuppressed()),
            "System.Threading.ExecutionContext.IsFlowSuppressed");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledExecutionContext.Restore(null!)),
            "System.Threading.ExecutionContext.Restore");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledExecutionContext.CreateCopy(null!)),
            "System.Threading.ExecutionContext.CreateCopy");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledExecutionContext.Dispose(null!)),
            "System.Threading.ExecutionContext.Dispose");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledExecutionContext.GetObjectData(null!, null!, default)),
            "System.Threading.ExecutionContext.GetObjectData");

        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(ControlledSynchronizationContext.Current),
            "System.Threading.SynchronizationContext.get_Current");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledSynchronizationContext.SetSynchronizationContext(null)),
            "System.Threading.SynchronizationContext.SetSynchronizationContext");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledSynchronizationContext.CreateCopy(null!)),
            "System.Threading.SynchronizationContext.CreateCopy");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledSynchronizationContext.IsWaitNotificationRequired(null!)),
            "System.Threading.SynchronizationContext.IsWaitNotificationRequired");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledSynchronizationContext.OperationStarted(null!)),
            "System.Threading.SynchronizationContext.OperationStarted");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledSynchronizationContext.OperationCompleted(null!)),
            "System.Threading.SynchronizationContext.OperationCompleted");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledSynchronizationContext.Post(null!, null!, null)),
            "System.Threading.SynchronizationContext.Post");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledSynchronizationContext.Send(null!, null!, null)),
            "System.Threading.SynchronizationContext.Send");
        SimulationNotActiveExceptionAssert.Equal(
            Record.Exception(() => ControlledSynchronizationContext.Wait(null!, null!, false, 0)),
            "System.Threading.SynchronizationContext.Wait");
    }
#pragma warning restore SYSLIB0051
#pragma warning restore CS0618

    private sealed class EscapingSynchronizationContext : SynchronizationContext
    {
        public int PostCalls { get; private set; }

        public int SendCalls { get; private set; }

        public int Started { get; private set; }

        public int Completed { get; private set; }

        public SynchronizationContext? ObservedPostContext { get; private set; }

        public override void Post(SendOrPostCallback d, object? state) => PostCalls++;

        public override void Send(SendOrPostCallback d, object? state) => SendCalls++;

        public override void OperationStarted() => Started++;

        public override void OperationCompleted() => Completed++;

        public override SynchronizationContext CreateCopy() => this;

        public void ObservePostContext() => ObservedPostContext = ControlledSynchronizationContext.Current();
    }
}
