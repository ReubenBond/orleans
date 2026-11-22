using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Runtime.Tests.Shims;

/// <summary>
/// Semantic conformance tests for <see cref="ControlledRandom"/>: shared-stream stability and
/// isolation, unseeded independence, seeded-seed preservation, same-seed replay, per-node isolation,
/// stream-domain independence, inherited/virtual API surface, active missing-context failure, and
/// inactive-simulation rejection.
/// </summary>
public sealed class ControlledRandomTests
{
    [Fact]
    public void SharedRandomSupportsConcurrentEscapedDrawsWithoutCorruptingState()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
        System.Random shared = ShimTestHarness.RunInSimulation(env, ControlledRandom.GetShared);
        var values = new int[256];

        Parallel.For(0, values.Length, index => values[index] = shared.Next());

        Assert.All(values, value => Assert.InRange(value, 0, int.MaxValue - 1));
        Assert.True(values.Distinct().Count() > 1);
    }

    [Fact]
    public void SharedReturnsAStableInstanceForTheSameNode()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());

        var same = ShimTestHarness.RunInSimulation(env, () =>
            ReferenceEquals(ControlledRandom.GetShared(), ControlledRandom.GetShared()));

        Assert.True(same);
    }

    [Fact]
    public void SharedStreamAdvancesAcrossDraws()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());

        var (first, second) = ShimTestHarness.RunInSimulation(env, () =>
        {
            var shared = ControlledRandom.GetShared();
            return (shared.Next(), shared.Next());
        });

        // Overwhelmingly likely to differ; this asserts the stream advances rather than repeating.
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SameSeedAndScheduleReplaysSharedDraws()
    {
        int Draw()
        {
            var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
            return ShimTestHarness.RunInSimulation(env, () => ControlledRandom.GetShared().Next());
        }

        Assert.Equal(Draw(), Draw());
    }

    [Fact]
    public void DifferentNodesGetIsolatedSharedStreams()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());

        var a = ShimTestHarness.RunInSimulation(env, () => ControlledRandom.GetShared().Next(), nodeAddress: "10.0.0.1");
        var b = ShimTestHarness.RunInSimulation(env, () => ControlledRandom.GetShared().Next(), nodeAddress: "10.0.0.2");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void UnseededConstructionsAreIndependentInstances()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());

        var distinct = ShimTestHarness.RunInSimulation(env, () =>
        {
            var r1 = ControlledRandom.CreateUnseeded();
            var r2 = ControlledRandom.CreateUnseeded();
            return !ReferenceEquals(r1, r2);
        });

        Assert.True(distinct);
    }

    [Fact]
    public void UnseededConstructionsProduceDifferentSequences()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());

        var (a, b) = ShimTestHarness.RunInSimulation(env, () =>
            (ControlledRandom.CreateUnseeded().Next(), ControlledRandom.CreateUnseeded().Next()));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void UnseededSequenceReplaysUnderAFixedSchedule()
    {
        int[] Draw()
        {
            var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
            return ShimTestHarness.RunInSimulation(env, () =>
            {
                var first = ControlledRandom.CreateUnseeded().Next();
                var second = ControlledRandom.CreateUnseeded().Next();
                return new[] { first, second };
            });
        }

        Assert.Equal(Draw(), Draw());
    }

    [Fact]
    public void SeededConstructionPreservesTheCallerSeedExactly()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());

        var shimmed = ShimTestHarness.RunInSimulation(env, () => ControlledRandom.CreateSeeded(4242).Next());
        var reference = new System.Random(4242).Next();

        Assert.Equal(reference, shimmed);
    }

    [Fact]
    public void SeededConstructionUsesTheExplicitSeed()
    {
        var environment = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
        var value = ShimTestHarness.RunInSimulation(
            environment,
            () => ControlledRandom.CreateSeeded(99).Next());

        Assert.Equal(new System.Random(99).Next(), value);
    }

    [Fact]
    public void InheritedAndVirtualRandomSurfaceIsDeterministic()
    {
        // Exercise a representative spread of the Random API surface (virtual/inherited members) on the
        // deterministic instance and confirm identical replay.
        object[] Draw()
        {
            var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
            return ShimTestHarness.RunInSimulation(env, () =>
            {
                var r = ControlledRandom.GetShared();
                var buffer = new byte[8];
                r.NextBytes(buffer);
                return new object[]
                {
                    r.Next(),
                    r.Next(100),
                    r.Next(10, 20),
                    r.NextInt64(),
                    r.NextInt64(1000),
                    r.NextInt64(5, 50),
                    r.NextSingle(),
                    r.NextDouble(),
                    Convert.ToHexString(buffer),
                };
            });
        }

        Assert.Equal(Draw(), Draw());
    }

    [Fact]
    public void ConsumingRandomDoesNotPerturbTheIdentityStream()
    {
        // Drawing application randomness must not change the GUID/identity stream: different domains.
        Guid WithoutDraw()
        {
            var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
            return ShimTestHarness.RunInSimulation(env, ControlledGuid.NewGuid);
        }

        Guid WithDraw()
        {
            var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
            return ShimTestHarness.RunInSimulation(env, () =>
            {
                _ = ControlledRandom.GetShared().Next();
                _ = ControlledRandom.CreateUnseeded().Next();
                return ControlledGuid.NewGuid();
            });
        }

        Assert.Equal(WithoutDraw(), WithDraw());
    }

    [Fact]
    public void OutsideSimulationRandomShimsRequireActiveSimulation()
    {
        Assert.False(Orleans.Testing.Simulation.Runtime.Execution.SimulationExecutionContext.IsActive);
        System.Random? random = null;

        Exception? sharedException = Record.Exception(() => random = ControlledRandom.GetShared());
        Assert.Null(random);
        SimulationNotActiveExceptionAssert.Equal(sharedException, "System.Random.Shared");

        Exception? unseededException = Record.Exception(() => random = ControlledRandom.CreateUnseeded());
        Assert.Null(random);
        SimulationNotActiveExceptionAssert.Equal(unseededException, "System.Random..ctor()");

        Exception? seededException = Record.Exception(() => random = ControlledRandom.CreateSeeded(7));
        Assert.Null(random);
        SimulationNotActiveExceptionAssert.Equal(seededException, "System.Random..ctor(Int32)");
    }
}
