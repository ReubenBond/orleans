using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Tests;

/// <summary>
/// <para>
/// Covers the runtime policy ambient-context integration wired into <see cref="SimulationCluster"/>
/// and <see cref="SimulationNodeContext"/>:
/// callbacks executed through an ambient-integrated <see cref="SimulationSchedulerLane"/> observe the
/// correct <see cref="SimulationExecutionContext"/> (runtime + node identity), two nodes never
/// observe each other's identity, and the ambient context is fully torn down again once the
/// driving call returns.
/// </para>
/// </summary>
public sealed class SimulationAmbientContextIntegrationTests
{
    [Fact]
    public async Task NodeCallbackObservesTheCorrectRuntimeAndNodeIdentity()
    {
        await using var cluster = new SimulationCluster(seed: 1);
        var node = cluster.AddNode("node-1");

        SimulationExecutionSnapshot? captured = null;
        node.Context.SchedulerLane.Enqueue(() => captured = SimulationExecutionContext.Current);

        cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(cluster.RuntimeIdentity.Id, captured!.Runtime.Id);
        Assert.NotNull(captured.Node);
        Assert.Equal("node-1", captured.Node!.Address);
    }

    [Fact]
    public async Task ClusterLevelCallbackObservesTheRuntimeButNoNodeIdentity()
    {
        await using var cluster = new SimulationCluster(seed: 1);
        _ = cluster.AddNode("node-1");

        SimulationExecutionSnapshot? captured = null;
        cluster.SchedulerLane.Enqueue(() => captured = SimulationExecutionContext.Current);

        cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(cluster.RuntimeIdentity.Id, captured!.Runtime.Id);
        Assert.Null(captured.Node);
    }

    [Fact]
    public async Task TwoNodesNeverObserveEachOthersAmbientNodeIdentity()
    {
        await using var cluster = new SimulationCluster(seed: 1);
        var nodeA = cluster.AddNode("node-a");
        var nodeB = cluster.AddNode("node-b");

        var observedAddressesInsideA = new List<string?>();
        var observedAddressesInsideB = new List<string?>();

        // Interleave several callbacks on each node so that neither node running "second" could
        // accidentally inherit stale ambient state left over from the other.
        for (var i = 0; i < 5; i++)
        {
            nodeA.Context.SchedulerLane.Enqueue(() => observedAddressesInsideA.Add(SimulationExecutionContext.Current?.Node?.Address));
            nodeB.Context.SchedulerLane.Enqueue(() => observedAddressesInsideB.Add(SimulationExecutionContext.Current?.Node?.Address));
        }

        cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.Equal(5, observedAddressesInsideA.Count);
        Assert.Equal(5, observedAddressesInsideB.Count);
        Assert.All(observedAddressesInsideA, address => Assert.Equal("node-a", address));
        Assert.All(observedAddressesInsideB, address => Assert.Equal("node-b", address));
    }

    [Fact]
    public async Task AmbientContextIsFullyClearedOnTheCallingThreadAfterRunUntilIdleReturns()
    {
        await using var cluster = new SimulationCluster(seed: 1);
        var node = cluster.AddNode("node-1");

        node.Context.SchedulerLane.Enqueue(() => { });

        Assert.False(SimulationExecutionContext.IsActive);
        cluster.RunUntilIdle(TestContext.Current.CancellationToken);
        Assert.False(SimulationExecutionContext.IsActive);
    }

    [Fact]
    public async Task AmbientContextIsFullyClearedOnTheCallingThreadAfterRunUntilReturns()
    {
        await using var cluster = new SimulationCluster(seed: 1);
        var node = cluster.AddNode("node-1");

        var executed = false;
        node.Context.SchedulerLane.EnqueueAfter(() => executed = true, TimeSpan.FromSeconds(1));

        Assert.Equal(SimulationExecutionReason.ConditionMet, cluster.RunUntil(() => executed, TestContext.Current.CancellationToken).Reason);
        Assert.False(SimulationExecutionContext.IsActive);
    }

    [Fact]
    public async Task CustomNodeGetsClusterAndNodeAmbientContext()
    {
        await using var cluster = new SimulationCluster(seed: 7);
        CustomNode node = cluster.AddCustomNode(
            "custom-node-1",
            context => new CustomNode("custom-node-1", context));

        SimulationExecutionSnapshot? capturedOnClusterQueue = null;
        cluster.SchedulerLane.Enqueue(() => capturedOnClusterQueue = SimulationExecutionContext.Current);

        var observedActiveInsideNodeCallback = true; // Overwritten below; starts true so a missed callback fails loudly.
        node.Context.SchedulerLane.Enqueue(() => observedActiveInsideNodeCallback = SimulationExecutionContext.IsActive);

        cluster.RunUntilIdle(TestContext.Current.CancellationToken);
        node.Context.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.NotNull(capturedOnClusterQueue);
        Assert.Equal(cluster.RuntimeIdentity.Id, capturedOnClusterQueue!.Runtime.Id);

        Assert.True(observedActiveInsideNodeCallback);
    }

    [Fact]
    public async Task SimpleNodeGetsFullAmbientIntegration()
    {
        await using var cluster = new SimulationCluster(seed: 7);
        var node = cluster.AddNode("node-1");

        var observedActiveInsideNodeCallback = false;
        node.Context.SchedulerLane.Enqueue(() => observedActiveInsideNodeCallback = SimulationExecutionContext.IsActive);

        cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.True(observedActiveInsideNodeCallback);
    }

    private sealed class CustomNode(string address, SimulationNodeContext context) : SimulationNode
    {
        public override SimulationNodeContext Context { get; } = context;

        public override string NetworkAddress { get; } = address;

        public override bool IsInitialized => true;
    }
}
