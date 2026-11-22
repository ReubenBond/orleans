namespace Orleans.Testing.Simulation.Tests;

public sealed class SimulationNetworkTests
{
    [Fact]
    public void LoopbackDeliveryAlwaysSucceedsRegardlessOfPartitionsOrDropRate()
    {
        var network = CreateNetwork([], seed: 1);
        network.CreatePartition("node-1", "node-1");
        network.MessageDropRate = 1.0;

        Assert.Equal(DeliveryStatus.Success, network.CheckDelivery("node-1", "node-1"));
    }

    [Fact]
    public void PartitionTakesPrecedenceOverRandomDrop()
    {
        var network = CreateNetwork([], seed: 1);
        network.MessageDropRate = 0; // Would never drop on its own.
        network.CreatePartition("node-1", "node-2");

        Assert.Equal(DeliveryStatus.Partitioned, network.CheckDelivery("node-1", "node-2"));
    }

    [Fact]
    public void SameSeedProducesTheSameSequenceOfDropDecisions()
    {
        var firstDecisions = CollectDropDecisions(seed: 7);
        var secondDecisions = CollectDropDecisions(seed: 7);

        Assert.Equal(firstDecisions, secondDecisions);

        // A 50% drop rate over many trials should drop at least once and succeed at least once,
        // otherwise the test would not actually be exercising both branches.
        Assert.Contains(DeliveryStatus.Dropped, firstDecisions);
        Assert.Contains(DeliveryStatus.Success, firstDecisions);
    }

    [Fact]
    public void DifferentSeedsCanProduceDifferentDropDecisionSequences()
    {
        var firstDecisions = CollectDropDecisions(seed: 7);
        var secondDecisions = CollectDropDecisions(seed: 99);

        Assert.NotEqual(firstDecisions, secondDecisions);
    }

    [Fact]
    public void DelayDefaultsAreZeroAndDoNotConsumeRandomness()
    {
        var random = new SimulationRandom(seed: 1);
        var comparisonRandom = new SimulationRandom(seed: 1);
        var network = new SimulationNetwork(() => [], random);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(TimeSpan.Zero, network.GetMessageDelay());
        }

        Assert.Equal(TimeSpan.Zero, network.BaseMessageDelay);
        Assert.Equal(TimeSpan.Zero, network.MaxJitter);
        Assert.Equal(comparisonRandom.Next(), random.Next());
    }

    [Fact]
    public void BaseDelayWithoutJitterDoesNotConsumeRandomness()
    {
        var random = new SimulationRandom(seed: 2);
        var comparisonRandom = new SimulationRandom(seed: 2);
        var network = new SimulationNetwork(() => [], random)
        {
            BaseMessageDelay = TimeSpan.FromMilliseconds(10),
        };

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(network.BaseMessageDelay, network.GetMessageDelay());
        }

        Assert.Equal(comparisonRandom.Next(), random.Next());
    }

    [Fact]
    public void JitterWithoutBaseDelayIsBoundedAndReproducible()
    {
        var maxJitter = TimeSpan.FromMilliseconds(5);
        var first = CreateNetwork([], seed: 3);
        var second = CreateNetwork([], seed: 3);
        first.MaxJitter = maxJitter;
        second.MaxJitter = maxJitter;
        var observedNonzeroJitter = false;

        for (var i = 0; i < 20; i++)
        {
            var firstDelay = first.GetMessageDelay();
            var secondDelay = second.GetMessageDelay();

            Assert.Equal(firstDelay, secondDelay);
            Assert.InRange(firstDelay, TimeSpan.Zero, maxJitter);
            observedNonzeroJitter |= firstDelay > TimeSpan.Zero;
        }

        Assert.True(observedNonzeroJitter);
    }

    [Fact]
    public void BaseDelayAndJitterAreBoundedAndReproducible()
    {
        var baseDelay = TimeSpan.FromMilliseconds(10);
        var maxJitter = TimeSpan.FromMilliseconds(5);

        SimulationNetwork Build(int seed)
        {
            var network = CreateNetwork([], seed);
            network.BaseMessageDelay = baseDelay;
            network.MaxJitter = maxJitter;
            return network;
        }

        var first = Build(seed: 3);
        var second = Build(seed: 3);

        for (var i = 0; i < 20; i++)
        {
            var firstDelay = first.GetMessageDelay();
            var secondDelay = second.GetMessageDelay();

            Assert.Equal(firstDelay, secondDelay);
            Assert.InRange(firstDelay, baseDelay, baseDelay + maxJitter);
        }
    }

    [Fact]
    public void NegativeDelaySettingsAreRejectedWithoutChangingConfiguration()
    {
        var network = CreateNetwork([], seed: 1);
        var baseException = Assert.Throws<ArgumentOutOfRangeException>(
            () => network.BaseMessageDelay = TimeSpan.FromTicks(-1));
        var jitterException = Assert.Throws<ArgumentOutOfRangeException>(
            () => network.MaxJitter = TimeSpan.FromTicks(-1));

        Assert.Equal(nameof(SimulationNetwork.BaseMessageDelay), baseException.ParamName);
        Assert.Equal(nameof(SimulationNetwork.MaxJitter), jitterException.ParamName);
        Assert.Equal(TimeSpan.Zero, network.BaseMessageDelay);
        Assert.Equal(TimeSpan.Zero, network.MaxJitter);
    }

    [Fact]
    public void DelaySettingsRejectCombinedValuesThatWouldOverflow()
    {
        var baseFirst = CreateNetwork([], seed: 1);
        baseFirst.BaseMessageDelay = TimeSpan.MaxValue;

        var jitterException = Assert.Throws<ArgumentOutOfRangeException>(
            () => baseFirst.MaxJitter = TimeSpan.FromTicks(1));

        Assert.Equal(nameof(SimulationNetwork.MaxJitter), jitterException.ParamName);
        Assert.Equal(TimeSpan.Zero, baseFirst.MaxJitter);
        Assert.Equal(TimeSpan.MaxValue, baseFirst.GetMessageDelay());

        var jitterFirst = CreateNetwork([], seed: 1);
        jitterFirst.MaxJitter = TimeSpan.MaxValue;

        var baseException = Assert.Throws<ArgumentOutOfRangeException>(
            () => jitterFirst.BaseMessageDelay = TimeSpan.FromTicks(1));

        Assert.Equal(nameof(SimulationNetwork.BaseMessageDelay), baseException.ParamName);
        Assert.Equal(TimeSpan.Zero, jitterFirst.BaseMessageDelay);
    }

    [Fact]
    public void DirectionalLinkLatencyOnlyOverridesConfiguredDirectionAndOtherLinksUseGlobalDelay()
    {
        var network = CreateNetwork([], seed: 1);
        network.BaseMessageDelay = TimeSpan.FromMilliseconds(5);
        network.SetLinkLatency("node-1", "node-2", TimeSpan.FromMilliseconds(50), TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMilliseconds(50), network.GetMessageDelay("node-1", "node-2"));
        Assert.Equal(TimeSpan.FromMilliseconds(5), network.GetMessageDelay("node-2", "node-1"));
        Assert.Equal(TimeSpan.FromMilliseconds(5), network.GetMessageDelay("node-1", "node-3"));
    }

    [Fact]
    public void BidirectionalLinkLatencySetsBothDirections()
    {
        var network = CreateNetwork([], seed: 1);

        network.SetBidirectionalLinkLatency(
            "node-1",
            "node-2",
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(5));

        Assert.Equal(
            new LinkLatency(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(5)),
            network.GetLinkLatency("node-1", "node-2"));
        Assert.Equal(
            new LinkLatency(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(5)),
            network.GetLinkLatency("node-2", "node-1"));
    }

    [Fact]
    public void LoopbackDelayIsZeroWithoutConsumingRandomnessRegardlessOfOverrides()
    {
        var random = new SimulationRandom(seed: 2);
        var comparisonRandom = new SimulationRandom(seed: 2);
        var network = new SimulationNetwork(() => [], random)
        {
            BaseMessageDelay = TimeSpan.FromMilliseconds(10),
            MaxJitter = TimeSpan.FromMilliseconds(5),
        };
        network.SetLinkLatency("node-1", "node-1", TimeSpan.FromMilliseconds(999), TimeSpan.FromMilliseconds(999));

        Assert.Equal(TimeSpan.Zero, network.GetMessageDelay("node-1", "node-1"));
        Assert.Equal(comparisonRandom.Next(), random.Next());
    }

    [Fact]
    public void InvalidLinkLatencyIsRejectedWithoutChangingDirectionalOrBidirectionalState()
    {
        var network = CreateNetwork([], seed: 1);
        var original = new LinkLatency(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(2));
        network.SetLinkLatency("node-1", "node-2", original.BaseDelay, original.MaxJitter);

        var negativeBase = Assert.Throws<ArgumentOutOfRangeException>(
            () => network.SetLinkLatency("node-1", "node-2", TimeSpan.FromTicks(-1), TimeSpan.Zero));
        var negativeJitter = Assert.Throws<ArgumentOutOfRangeException>(
            () => network.SetLinkLatency("node-1", "node-2", TimeSpan.Zero, TimeSpan.FromTicks(-1)));
        var overflow = Assert.Throws<ArgumentOutOfRangeException>(
            () => network.SetBidirectionalLinkLatency("node-1", "node-2", TimeSpan.MaxValue, TimeSpan.FromTicks(1)));

        Assert.Equal("baseDelay", negativeBase.ParamName);
        Assert.Equal("maxJitter", negativeJitter.ParamName);
        Assert.Equal("baseDelay", overflow.ParamName);
        Assert.Equal(original, network.GetLinkLatency("node-1", "node-2"));
        Assert.Null(network.GetLinkLatency("node-2", "node-1"));
    }

    [Fact]
    public void GetClearClearAllAndOrderedSnapshotOperationsAreDeterministic()
    {
        var network = CreateNetwork([], seed: 1);
        var first = new LinkLatency(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(1));
        var second = new LinkLatency(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(2));
        var third = new LinkLatency(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(3));
        network.SetLinkLatency("node-2", "node-1", third.BaseDelay, third.MaxJitter);
        network.SetLinkLatency("node-1", "node-3", second.BaseDelay, second.MaxJitter);
        network.SetLinkLatency("node-1", "node-2", first.BaseDelay, first.MaxJitter);

        var snapshot = network.GetAllLinkLatencies();

        Assert.Equal(first, network.GetLinkLatency("node-1", "node-2"));
        Assert.Collection(
            snapshot,
            item => Assert.Equal(new LinkLatencyOverride("node-1", "node-2", first), item),
            item => Assert.Equal(new LinkLatencyOverride("node-1", "node-3", second), item),
            item => Assert.Equal(new LinkLatencyOverride("node-2", "node-1", third), item));
        Assert.True(network.ClearLinkLatency("node-1", "node-2"));
        Assert.False(network.ClearLinkLatency("node-1", "node-2"));
        Assert.Null(network.GetLinkLatency("node-1", "node-2"));

        network.ClearAllLinkLatencies();

        Assert.Empty(network.GetAllLinkLatencies());
        Assert.Equal(3, snapshot.Count);
    }

    [Fact]
    public void SeededDirectionalAndGlobalJitterTraceIsExactAndDifferentSeedsDiverge()
    {
        var first = CollectLinkDelayTrace(seed: 17);
        var second = CollectLinkDelayTrace(seed: 17);
        var different = CollectLinkDelayTrace(seed: 18);

        Assert.Equal(
            [
                1_030_373L,
                140_272L,
                1_032_540L,
                109_334L,
                1_044_064L,
                120_757L,
                1_026_149L,
                148_540L,
                1_004_036L,
                127_123L,
                1_040_816L,
                136_973L,
            ],
            first.Select(static delay => delay.Ticks));
        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
        Assert.All(
            first.Where((_, index) => index % 2 == 0),
            delay => Assert.InRange(
                delay,
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(105)));
        Assert.All(
            first.Where((_, index) => index % 2 != 0),
            delay => Assert.InRange(
                delay,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(15)));
    }

    [Fact]
    public void LinkLatencyPreservesPartitionDropAndLoopbackPrecedence()
    {
        var network = CreateNetwork([], seed: 6);
        network.MessageDropRate = 1.0;
        network.SetBidirectionalLinkLatency(
            "node-1",
            "node-2",
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);
        network.CreatePartition("node-1", "node-2");

        Assert.Equal(DeliveryStatus.Partitioned, network.CheckDelivery("node-1", "node-2"));
        Assert.Equal(DeliveryStatus.Dropped, network.CheckDelivery("node-2", "node-1"));
        Assert.Equal(DeliveryStatus.Success, network.CheckDelivery("node-1", "node-1"));
        Assert.Equal(TimeSpan.Zero, network.GetMessageDelay("node-1", "node-1"));

        network.HealPartition("node-1", "node-2");

        Assert.Equal(DeliveryStatus.Dropped, network.CheckDelivery("node-1", "node-2"));
        Assert.Equal(TimeSpan.FromSeconds(1), network.GetMessageDelay("node-1", "node-2"));
    }

    [Fact]
    public void LinkLatencyMutationHooksReportStableEventOrdering()
    {
        var network = new RecordingNetwork(new SimulationRandom(seed: 7));

        network.SetLinkLatency("b", "a", TimeSpan.FromTicks(1), TimeSpan.FromTicks(2));
        network.SetBidirectionalLinkLatency("a", "c", TimeSpan.FromTicks(3), TimeSpan.FromTicks(4));
        Assert.True(network.ClearLinkLatency("b", "a"));
        Assert.False(network.ClearLinkLatency("b", "a"));
        network.ClearAllLinkLatencies();

        Assert.Equal(
            [
                "set:b->a:1+2",
                "set:a->c:3+4",
                "set:c->a:3+4",
                "clear:b->a",
                "clear-all:2",
            ],
            network.Events);
    }

    [Fact]
    public void IsolateNodeBlocksDeliveryInBothDirectionsForAllPeersAndReconnectHeals()
    {
        var nodes = new List<SimulationNode> { new TestNode("node-1"), new TestNode("node-2"), new TestNode("node-3") };
        var network = CreateNetwork(nodes, seed: 1);

        network.IsolateNode("node-1");

        Assert.True(network.IsNodeIsolated("node-1"));
        Assert.False(network.CanDeliver("node-1", "node-2"));
        Assert.False(network.CanDeliver("node-2", "node-1"));
        Assert.False(network.CanDeliver("node-1", "node-3"));
        Assert.True(network.CanDeliver("node-2", "node-3"));

        network.ReconnectNode("node-1");

        Assert.False(network.IsNodeIsolated("node-1"));
        Assert.True(network.CanDeliver("node-1", "node-2"));
        Assert.True(network.CanDeliver("node-2", "node-1"));
    }

    [Fact]
    public void HealAllPartitionsRemovesEveryPartition()
    {
        var network = CreateNetwork([], seed: 1);
        network.CreateBidirectionalPartition("node-1", "node-2");
        network.CreatePartition("node-2", "node-3");

        network.HealAllPartitions();

        Assert.True(network.CanDeliver("node-1", "node-2"));
        Assert.True(network.CanDeliver("node-2", "node-1"));
        Assert.True(network.CanDeliver("node-2", "node-3"));
    }

    private static List<DeliveryStatus> CollectDropDecisions(int seed)
    {
        var network = CreateNetwork([], seed);
        network.MessageDropRate = 0.5;

        var decisions = new List<DeliveryStatus>();
        for (var i = 0; i < 50; i++)
        {
            decisions.Add(network.CheckDelivery("node-1", "node-2"));
        }

        return decisions;
    }

    private static List<TimeSpan> CollectLinkDelayTrace(int seed)
    {
        var network = CreateNetwork([], seed);
        network.BaseMessageDelay = TimeSpan.FromMilliseconds(10);
        network.MaxJitter = TimeSpan.FromMilliseconds(5);
        network.SetLinkLatency("node-1", "node-2", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5));

        var delays = new List<TimeSpan>();
        for (var i = 0; i < 12; i++)
        {
            delays.Add(network.GetMessageDelay("node-1", i % 2 == 0 ? "node-2" : "node-3"));
        }

        return delays;
    }

    private static SimulationNetwork CreateNetwork(IReadOnlyList<SimulationNode> nodes, int seed) => new(() => nodes, new SimulationRandom(seed));

    private sealed class RecordingNetwork(SimulationRandom random)
        : SimulationNetwork(() => [], random)
    {
        public List<string> Events { get; } = [];

        protected override void OnLinkLatencySet(
            string source,
            string target,
            TimeSpan baseDelay,
            TimeSpan maxJitter) =>
            Events.Add($"set:{source}->{target}:{baseDelay.Ticks}+{maxJitter.Ticks}");

        protected override void OnLinkLatencyCleared(string source, string target) =>
            Events.Add($"clear:{source}->{target}");

        protected override void OnAllLinkLatenciesCleared(int count) =>
            Events.Add($"clear-all:{count}");
    }

    private sealed class TestNode(string address) : SimulationNode
    {
        public override string NetworkAddress { get; } = address;

        public override bool IsInitialized => true;

        public override SimulationNodeContext Context { get; } =
            SimulationTestHarness.NewNodeComponents(address).Context;
    }
}
