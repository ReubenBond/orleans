using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Tests;

public sealed class SimulationClusterSchedulerFairnessTests
{
    [Fact]
    public async Task ClusterAndNodeQueuesCannotStarveEachOther()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-a");
        var executionOrder = new List<string>();

        EnqueueRepeatedly(cluster.SchedulerLane, "cluster", 4);
        EnqueueRepeatedly(node.Context.SchedulerLane, "node-a", 4);

        RunSteps(cluster, 8);

        Assert.Equal(
            ["cluster", "node-a", "cluster", "node-a", "cluster", "node-a", "cluster", "node-a"],
            executionOrder);

        void EnqueueRepeatedly(SimulationSchedulerLane queue, string source, int remaining)
        {
            queue.Enqueue(() =>
            {
                executionOrder.Add(source);
                if (remaining > 1)
                {
                    EnqueueRepeatedly(queue, source, remaining - 1);
                }
            });
        }
    }

    [Fact]
    public async Task SortedNodeQueuesCannotStarveEachOther()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var nodeB = cluster.AddNode("node-b");
        var nodeA = cluster.AddNode("node-a");
        var executionOrder = new List<string>();

        EnqueueRepeatedly(nodeA, 4);
        EnqueueRepeatedly(nodeB, 4);

        RunSteps(cluster, 8);

        Assert.Equal(
            ["node-a", "node-b", "node-a", "node-b", "node-a", "node-b", "node-a", "node-b"],
            executionOrder);

        void EnqueueRepeatedly(SimulationNode node, int remaining)
        {
            node.Context.SchedulerLane.Enqueue(() =>
            {
                executionOrder.Add(node.NetworkAddress);
                if (remaining > 1)
                {
                    EnqueueRepeatedly(node, remaining - 1);
                }
            });
        }
    }

    [Fact]
    public async Task NodeQueueAndControlledLoopCannotStarveEachOther()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-a");
        var executionOrder = new List<string>();

        EnqueueNode(4);
        EnqueueControlled(4);

        RunSteps(cluster, 8);

        Assert.Equal(
            ["node-a", "controlled", "node-a", "controlled", "node-a", "controlled", "node-a", "controlled"],
            executionOrder);

        void EnqueueNode(int remaining)
        {
            node.Context.SchedulerLane.Enqueue(() =>
            {
                executionOrder.Add("node-a");
                if (remaining > 1)
                {
                    EnqueueNode(remaining - 1);
                }
            });
        }

        void EnqueueControlled(int remaining)
        {
            cluster.RuntimeIdentity.Scheduler.Schedule(node: null, () =>
            {
                executionOrder.Add("controlled");
                if (remaining > 1)
                {
                    EnqueueControlled(remaining - 1);
                }
            });
        }
    }

    [Fact]
    public async Task InitialSchedulingOrderFollowsUnifiedRegistrationOrder()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var nodeB = cluster.AddNode("node-b");
        var nodeA = cluster.AddNode("node-a");
        var executionOrder = new List<string>();

        cluster.SchedulerLane.Enqueue(() => executionOrder.Add("cluster"));
        nodeB.Context.SchedulerLane.Enqueue(() => executionOrder.Add("node-b"));
        nodeA.Context.SchedulerLane.Enqueue(() => executionOrder.Add("node-a"));
        cluster.RuntimeIdentity.Scheduler.Schedule(
            node: null,
            () => executionOrder.Add("controlled"));

        RunSteps(cluster, 4);

        Assert.Equal(["cluster", "node-b", "node-a", "controlled"], executionOrder);
    }

    [Fact]
    public async Task CursorRemainsValidWhenNodesAreSuspendedAndRegistered()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var nodeA = cluster.AddNode("node-a");
        var nodeB = cluster.AddNode("node-b");
        var executionOrder = new List<string>();

        cluster.SchedulerLane.Enqueue(() => executionOrder.Add("cluster"));
        nodeA.Context.SchedulerLane.Enqueue(() => executionOrder.Add("node-a"));
        nodeB.Context.SchedulerLane.Enqueue(() => executionOrder.Add("node-b"));
        cluster.RuntimeIdentity.Scheduler.Schedule(
            node: null,
            () => executionOrder.Add("controlled"));

        Assert.True(RunOneStep(cluster));
        nodeA.Suspend();
        Assert.True(RunOneStep(cluster));

        var nodeC = cluster.AddNode("node-c");
        nodeC.Context.SchedulerLane.Enqueue(() => executionOrder.Add("node-c"));
        nodeA.Resume();

        RunSteps(cluster, 3);

        Assert.Equal(["cluster", "node-b", "controlled", "node-c", "node-a"], executionOrder);
    }

    private static void RunSteps(SimulationCluster cluster, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(RunOneStep(cluster));
        }
    }

    private static bool RunOneStep(SimulationCluster cluster) =>
        cluster.RunUntil(static () => false, TestContext.Current.CancellationToken, maxIterations: 1).StepsExecuted == 1;
}
