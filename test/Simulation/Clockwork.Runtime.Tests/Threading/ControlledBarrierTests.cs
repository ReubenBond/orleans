using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

#pragma warning disable xUnit1051 // Exact timeout overloads are the subject under test.

public sealed class ControlledBarrierTests
{
    [Fact]
    public void ConstructorsPropertiesAndAllSignalOverloadsFollowPhaseSemantics()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var phases = new List<(long Phase, int Remaining)>();
            var barrier = new ControlledBarrier(1, b => phases.Add((b.CurrentPhaseNumber, b.ParticipantsRemaining)));

            Assert.Equal(0, barrier.CurrentPhaseNumber);
            Assert.Equal(1, barrier.ParticipantCount);
            Assert.Equal(1, barrier.ParticipantsRemaining);

            barrier.SignalAndWait();
            barrier.SignalAndWait(CancellationToken.None);
            Assert.True(barrier.SignalAndWait(0));
            Assert.True(barrier.SignalAndWait(0, CancellationToken.None));
            Assert.True(barrier.SignalAndWait(TimeSpan.Zero));
            Assert.True(barrier.SignalAndWait(TimeSpan.Zero, CancellationToken.None));

            Assert.Equal(6, barrier.CurrentPhaseNumber);
            Assert.Equal([(0L, 0), (1L, 0), (2L, 0), (3L, 0), (4L, 0), (5L, 0)], phases);
        });
    }

    [Fact]
    public void AddAndRemoveParticipantsAdjustTheCurrentPhase()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var barrier = new ControlledBarrier(0);
            Assert.Equal(0, barrier.ParticipantCount);
            Assert.Equal(0, barrier.ParticipantsRemaining);
            Assert.Equal(0, barrier.AddParticipant());
            Assert.Equal(0, barrier.AddParticipants(2));
            Assert.Equal(3, barrier.ParticipantCount);

            barrier.RemoveParticipant();
            barrier.RemoveParticipants(1);
            Assert.Equal(1, barrier.ParticipantCount);
            Assert.True(barrier.SignalAndWait(0));

            barrier.RemoveParticipant();
            Assert.Throws<ArgumentOutOfRangeException>(() => barrier.RemoveParticipant());
            Assert.Equal("participantCount", Assert.Throws<ArgumentOutOfRangeException>(
                () => barrier.AddParticipants(0)).ParamName);
            Assert.Equal("participantCount", Assert.Throws<ArgumentOutOfRangeException>(
                () => barrier.RemoveParticipants(0)).ParamName);
        });
    }

    [Fact]
    public void RemoveParticipantsDoesNotMutateCountsWhenArrivedParticipantsWouldBeRemoved()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var barrier = new ControlledBarrier(2);
            Thread arrived = ControlledThread.Create(() => barrier.SignalAndWait());
            ControlledThread.Start(arrived);
            Pump();

            Assert.Equal(2, barrier.ParticipantCount);
            Assert.Equal(1, barrier.ParticipantsRemaining);
            Assert.Throws<InvalidOperationException>(() => barrier.RemoveParticipants(2));
            Assert.Equal(2, barrier.ParticipantCount);
            Assert.Equal(1, barrier.ParticipantsRemaining);

            barrier.SignalAndWait();
            ControlledThread.Join(arrived);
        });
    }

    [Fact]
    public void WaitersObservePhaseCompletionTimeoutCancellationAndPostPhaseFailure()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var barrier = new ControlledBarrier(2);
            bool? workerResult = null;
            Thread worker = ControlledThread.Create(() => workerResult = barrier.SignalAndWait(100));
            ControlledThread.Start(worker);
            Assert.True(barrier.SignalAndWait(100));
            ControlledThread.Join(worker);
            Assert.True(workerResult);

            Assert.False(barrier.SignalAndWait(10));
            Assert.Equal(2, barrier.ParticipantsRemaining);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => barrier.SignalAndWait(10, cancellation.Token));

            var faulting = new ControlledBarrier(1, _ => throw new InvalidOperationException("post phase"));
            BarrierPostPhaseException exception = Assert.Throws<BarrierPostPhaseException>(() => faulting.SignalAndWait());
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        });
    }

    [Fact]
    public void ScheduledOperationCanCompletePhaseAndCancellationRetractsArrival()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var barrier = new ControlledBarrier(2);
            Exception? nestedSignal = null;
            SimulationTaskRuntime.ScheduleYield(
                () => nestedSignal = Record.Exception(() => barrier.SignalAndWait(0)),
                "ControlledBarrierTests.SameStrand",
                flowExecutionContext: true);

            Assert.True(barrier.SignalAndWait(10));
            Assert.Null(nestedSignal);
            Assert.Equal(2, barrier.ParticipantsRemaining);

            using var cancellation = new CancellationTokenSource();
            Exception? canceled = null;
            Thread waiter = ControlledThread.Create(() =>
            {
                try
                {
                    barrier.SignalAndWait(100, cancellation.Token);
                }
                catch (Exception exception)
                {
                    canceled = exception;
                }
            });
            Thread canceler = ControlledThread.Create(cancellation.Cancel);
            ControlledThread.Start(waiter);
            ControlledThread.Start(canceler);
            ControlledThread.Join(waiter);
            ControlledThread.Join(canceler);

            Assert.IsAssignableFrom<OperationCanceledException>(canceled);
            Assert.Equal(2, barrier.ParticipantsRemaining);
        });
    }

    [Fact]
    public void CompletedPhaseWinsCancellationRequestedByPostPhaseAction()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var cancellation = new CancellationTokenSource();
            var barrier = new ControlledBarrier(2, _ => cancellation.Cancel());
            bool? waiterResult = null;
            Exception? waiterException = null;
            Thread waiter = ControlledThread.Create(() =>
            {
                try
                {
                    waiterResult = barrier.SignalAndWait(100, cancellation.Token);
                }
                catch (Exception exception)
                {
                    waiterException = exception;
                }
            });

            ControlledThread.Start(waiter);
            Pump();
            Assert.Equal(1, barrier.ParticipantsRemaining);

            Assert.True(barrier.SignalAndWait(100));
            ControlledThread.Join(waiter);

            Assert.Null(waiterException);
            Assert.True(waiterResult);
            Assert.Equal(1, barrier.CurrentPhaseNumber);
            Assert.Equal(2, barrier.ParticipantsRemaining);
        });
    }

    [Fact]
    public void ValidationDisposalAndInactiveGuardsMatchControlledSurface()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Equal("participantCount", Assert.Throws<ArgumentOutOfRangeException>(
                () => new ControlledBarrier(-1)).ParamName);
            Assert.Equal("participantCount", Assert.Throws<ArgumentOutOfRangeException>(
                () => new ControlledBarrier(32768)).ParamName);

            var barrier = new ControlledBarrier(1);
            Assert.Equal("millisecondsTimeout", Assert.Throws<ArgumentOutOfRangeException>(
                () => barrier.SignalAndWait(-2)).ParamName);
            Assert.Equal("timeout", Assert.Throws<ArgumentOutOfRangeException>(
                () => barrier.SignalAndWait(TimeSpan.FromDays(1000))).ParamName);
            barrier.Dispose();
            Assert.Throws<ObjectDisposedException>(() => barrier.SignalAndWait());
            Assert.Throws<ObjectDisposedException>(() => barrier.AddParticipant());
        });

        ControlledBarrier? created = null;
        Exception? exception = Record.Exception(() => created = new ControlledBarrier(1));
        Assert.Null(created);
        SimulationNotActiveExceptionAssert.Equal(exception, "System.Threading.Barrier..ctor");
    }

    private static void Pump()
    {
        var timer = ControlledSemaphoreSlim.Create(0);
        Assert.False(ControlledSemaphoreSlim.Wait(timer, 1));
    }
}
