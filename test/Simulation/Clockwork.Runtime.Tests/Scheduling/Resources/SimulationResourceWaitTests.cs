using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

namespace Orleans.Testing.Simulation.Runtime.Tests.Scheduling.Resources;

/// <summary>
/// Coverage of the scheduler's atomic resource wait/wake primitives: a waiting operation yields the
/// baton and later resumes exactly once when signaled, in deterministic FIFO order, with no lost,
/// duplicate, or stale wakeups, across one and many resources.
/// </summary>
public sealed class SimulationResourceWaitTests
{
    private static SimulationPauseReason Reason(string tag) => SimulationPauseReason.ResourceWait(tag);

    [Fact]
    public void WaitYieldsTheBatonAndSignalResumesWithSignaledOutcome()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        var log = new List<string>();
        var outcome = (SimulationWaitOutcome?)null;

        var waiter = scheduler.Schedule("waiter", () =>
        {
            log.Add("wait");
            outcome = scheduler.WaitOnResource(resource, Reason("m"));
            log.Add("woke");
        });
        scheduler.Schedule("signaler", () =>
        {
            log.Add("signal");
            scheduler.SignalOne(resource);
        });

        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal(["wait", "signal", "woke"], log);
        Assert.Equal(SimulationWaitOutcome.Signaled, outcome);
        Assert.Equal(SimulationOperationState.Completed, waiter.State);
        Assert.Equal(0, resource.WaiterCount);
    }

    [Fact]
    public void WaitingOperationIsPausedOnResourceUntilSignaled()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        var op = scheduler.Schedule("waiter", () => scheduler.WaitOnResource(resource, Reason("m")));

        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(SimulationOperationState.Paused, op.State);
        Assert.Equal(SimulationOperationPauseReason.ResourceWait, op.PauseReason!.Kind);
        Assert.True(resource.HasPendingWaiters);

        // Nothing else runnable while it is parked on the resource.
        Assert.False(scheduler.RunStep(TestContext.Current.CancellationToken));

        Assert.NotNull(scheduler.SignalOne(resource));
        Assert.Equal(SimulationOperationState.Runnable, op.State);
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(SimulationOperationState.Completed, op.State);
    }

    [Fact]
    public void SignalOneWakesWaitersInDeterministicFifoOrder()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Semaphore, "sem");
        var wakeOrder = new List<string>();

        // Three waiters register in id order (op1, op2, op3) so the FIFO queue is 1,2,3.
        for (var i = 1; i <= 3; i++)
        {
            var name = $"w{i}";
            scheduler.Schedule(name, () =>
            {
                scheduler.WaitOnResource(resource, Reason("sem"));
                wakeOrder.Add(name);
            });
        }

        // Park all three.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        }

        Assert.False(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(3, resource.WaiterCount);

        // Signal one at a time; each wakes the earliest-enqueued waiter, then completes on its step.
        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(scheduler.SignalOne(resource));
            Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        }

        Assert.Equal(["w1", "w2", "w3"], wakeOrder);
        Assert.Equal(0, resource.WaiterCount);
    }

    [Fact]
    public void SignalAllWakesEveryWaiterInFifoOrder()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.ManualResetEvent, "evt");
        var wakeOrder = new List<string>();
        for (var i = 1; i <= 4; i++)
        {
            var name = $"w{i}";
            scheduler.Schedule(name, () =>
            {
                scheduler.WaitOnResource(resource, Reason("evt"));
                wakeOrder.Add(name);
            });
        }

        for (var i = 0; i < 4; i++)
        {
            Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        }

        var woken = scheduler.SignalAll(resource);

        Assert.Equal([1L, 2L, 3L, 4L], woken.Select(o => o.Id.Value));
        Assert.Equal(0, resource.WaiterCount);

        scheduler.Drain(TestContext.Current.CancellationToken);
        Assert.Equal(["w1", "w2", "w3", "w4"], wakeOrder);
    }

    [Fact]
    public void SignalOneOnResourceWithNoWaitersIsANoOp()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Custom, "r");
        Assert.Null(scheduler.SignalOne(resource));
        Assert.Empty(scheduler.SignalAll(resource));
    }

    [Fact]
    public void NoLostWakeupsWhenManyWaitersAcrossManyResourcesAreEachSignaledOnce()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        const int resourceCount = 6;
        const int waitersPerResource = 5;
        var resources = new List<SimulationResource>();
        var woke = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();

        for (var r = 0; r < resourceCount; r++)
        {
            var resource = scheduler.CreateResource(SimulationResourceKind.Semaphore, $"r{r}");
            resources.Add(resource);
            for (var w = 0; w < waitersPerResource; w++)
            {
                var key = $"r{r}-w{w}";
                scheduler.Schedule(key, () =>
                {
                    var outcome = scheduler.WaitOnResource(resource, Reason(resource.Name));
                    Assert.Equal(SimulationWaitOutcome.Signaled, outcome);
                    woke.AddOrUpdate(key, 1, (_, c) => c + 1);
                });
            }
        }

        // A coordinator operation signals each waiter exactly once.
        scheduler.Schedule("coordinator", () =>
        {
            foreach (var resource in resources)
            {
                for (var w = 0; w < waitersPerResource; w++)
                {
                    scheduler.SignalOne(resource);
                    scheduler.Yield();
                }
            }
        });

        // Bounded drive: run steps and advance until everything settles.
        var guard = 0;
        while (scheduler.RunStep(TestContext.Current.CancellationToken))
        {
            Assert.True(++guard < 100_000, "drive did not converge");
        }

        Assert.Equal(resourceCount * waitersPerResource, woke.Count);
        Assert.All(woke.Values, v => Assert.Equal(1, v));
        Assert.All(resources, r => Assert.Equal(0, r.WaiterCount));
    }

    [Fact]
    public void CancelingAWaitingOperationRemovesItsWaiterSoLaterSignalsDoNotWakeIt()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        var afterWaitRan = false;
        var op = scheduler.Schedule("waiter", () =>
        {
            scheduler.WaitOnResource(resource, Reason("m"));
            afterWaitRan = true;
        });

        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(SimulationOperationState.Paused, op.State);
        Assert.Equal(1, resource.WaiterCount);

        scheduler.Cancel(op);

        Assert.Equal(SimulationOperationState.Canceled, op.State);
        Assert.Equal(0, resource.WaiterCount);

        // A signal now finds no pending waiter and must not attempt to wake the canceled operation.
        Assert.Null(scheduler.SignalOne(resource));
        Assert.False(afterWaitRan);
    }

    [Fact]
    public void WaitOnResourceFromOutsideAnOperationThrows()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Custom, "r");
        Assert.Throws<SimulationSchedulerException>(() => scheduler.WaitOnResource(resource, Reason("r")));
    }

    [Fact]
    public void WaitOnAResourceFromAnotherSchedulerThrows()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        using var other = SchedulerTestHarness.NewScheduler();
        var foreign = other.CreateResource(SimulationResourceKind.Custom, "foreign");

        SimulationSchedulerException? caught = null;
        scheduler.Schedule("op", () =>
        {
            try
            {
                scheduler.WaitOnResource(foreign, Reason("foreign"));
            }
            catch (SimulationSchedulerException ex)
            {
                caught = ex;
            }
        });
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.NotNull(caught);
    }
}
