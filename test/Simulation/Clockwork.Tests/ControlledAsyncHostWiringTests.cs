using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

namespace Orleans.Testing.Simulation.Tests;

/// <summary>
/// End-to-end host-wiring tests: a real <see cref="SimulationCluster"/> carries a controlled
/// task coordinator in its runtime, and its drive loop pumps controlled async continuations. These use
/// a hand-written async state machine (exactly the shape the C# compiler emits, with the controlled
/// builder/awaiter substituted) to prove the controlled machinery runs on the cluster's single logical
/// thread without the rewriter being involved.
/// </summary>
public sealed class ControlledAsyncHostWiringTests
{
    [Fact]
    public async Task CoordinatorIsCarriedByTheClusterRuntime()
    {
        await using var cluster = new SimulationCluster(seed: 12345, DateTimeOffset.UnixEpoch);
        _ = cluster.AddNode("node-1");

        bool active = false;
        bool resolved = false;

        // The cluster queue installs ambient runtime context while an item runs, so the controlled
        // machinery must see an active simulation and resolve this cluster's coordinator.
        cluster.SchedulerLane.Enqueue(() =>
        {
            active = SimulationTaskRuntime.IsSimulationActive;
            resolved = ReferenceEquals(
                SimulationTaskRuntime.RequireScheduler("test.scheduler").Scheduler,
                cluster.RuntimeIdentity.Scheduler);
        });

        Assert.Equal(SimulationExecutionReason.ConditionMet, cluster.RunUntil(() => active, TestContext.Current.CancellationToken).Reason);
        Assert.True(active);
        Assert.True(resolved);
    }

    [Fact]
    public async Task AwaitedControlledTaskResumesUnderClusterDrive()
    {
        await using var cluster = new SimulationCluster(seed: 12345, DateTimeOffset.UnixEpoch);
        _ = cluster.AddNode("node-1");

        var gate = new TaskCompletionSource<int>();
        Task<int>? machineTask = null;

        // Start a controlled async state machine on the cluster queue (ambient runtime active). It awaits
        // an incomplete task; the continuation must be registered on the runtime coordinator and later
        // driven by the cluster loop - never inline, never on the thread pool.
        cluster.SchedulerLane.Enqueue(() =>
        {
            machineTask = RunAwaitingMachine(gate.Task);
            Assert.False(machineTask.IsCompleted);
        });

        // Complete the awaited task on a later queue item, on the same logical thread.
        cluster.SchedulerLane.EnqueueAfter(() => gate.SetResult(21), TimeSpan.FromSeconds(1));

        Assert.Equal(
            SimulationExecutionReason.ConditionMet,
            cluster.RunUntil(() => machineTask is { IsCompleted: true }, TestContext.Current.CancellationToken).Reason);

        Assert.Equal(42, await machineTask!);
    }

    // A hand-written equivalent of `async Task<int> M(Task<int> g) { return await g * 2; }` using the
    // controlled builder and awaiter, so the whole pipeline is exercised without the rewriter.
    private static Task<int> RunAwaitingMachine(Task<int> gate)
    {
        var machine = new AwaitingStateMachine
        {
            Gate = gate,
            Builder = ControlledAsyncTaskMethodBuilder<int>.Create(),
            State = -1,
        };
        machine.Builder.Start(ref machine);
        return machine.Builder.Task;
    }

    private struct AwaitingStateMachine : IAsyncStateMachine
    {
        public int State;
        public ControlledAsyncTaskMethodBuilder<int> Builder;
        public Task<int> Gate;
        private ControlledTaskAwaiter<int> _awaiter;

        public void MoveNext()
        {
            int value;
            try
            {
                if (State != 0)
                {
                    _awaiter = new ControlledTaskAwaiter<int>(Gate);
                    if (!_awaiter.IsCompleted)
                    {
                        State = 0;
                        Builder.AwaitUnsafeOnCompleted(ref _awaiter, ref this);
                        return;
                    }
                }

                value = _awaiter.GetResult();
            }
            catch (Exception ex)
            {
                State = -2;
                Builder.SetException(ex);
                return;
            }

            State = -2;
            Builder.SetResult(value * 2);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine) => Builder.SetStateMachine(stateMachine);
    }

}
