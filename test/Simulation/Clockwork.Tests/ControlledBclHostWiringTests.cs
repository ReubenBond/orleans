using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Tests;

/// <summary>
/// <para>
/// Proves the deterministic BCL rules host wiring: <see cref="SimulationCluster"/> registers a
/// <see cref="SimulationRuntimeEnvironment"/> for its runtime so ordinary code whose direct BCL
/// calls have been redirected to the deterministic shims observes virtual, per-node-isolated,
/// replayable time/identity/randomness while the cluster's ambient runtime is active - without any
/// dependency injection or manual service plumbing.
/// </para>
/// <para>
/// These tests call the shims directly (as instrumented code would after rewriting) from work
/// scheduled on a node's ambient-integrated queue. Outside a simulation, controlled entry points fail
/// before reaching the real BCL API.
/// </para>
/// </summary>
public sealed class ControlledBclHostWiringTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch.AddDays(10);

    [Fact]
    public async Task NodeWorkObservesSimulatedUtcTimeThroughClockShim()
    {
        await using var cluster = new SimulationCluster(1, Start);
        var node = cluster.AddNode("node-1");

        DateTime captured = default;
        node.Context.SchedulerLane.Enqueue(() => captured = ControlledDateTime.GetUtcNow());
        cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.Equal(Start.UtcDateTime, captured);
        Assert.Equal(DateTimeKind.Utc, captured.Kind);
    }

    [Fact]
    public async Task NodeWorkObservesSimulatedTimestampAndTickCountThroughShims()
    {
        await using var cluster = new SimulationCluster(1, Start);
        var node = cluster.AddNode("node-1");

        long timestamp = -1;
        long tickCount64 = -1;
        node.Context.SchedulerLane.Enqueue(() =>
        {
            timestamp = ControlledStopwatch.GetTimestamp();
            tickCount64 = ControlledEnvironment.GetTickCount64();
        });
        cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        // Origin is StartDateTime, so with no time advanced both read zero elapsed - fully virtual,
        // never the host machine's real Stopwatch/Environment counters.
        Assert.Equal(0, timestamp);
        Assert.Equal(0, tickCount64);
    }

    [Fact]
    public async Task TwoNodesNeverShareSharedRandomState()
    {
        await using var cluster = new SimulationCluster(7, Start);
        var a = cluster.AddNode("node-a");
        var b = cluster.AddNode("node-b");

        int firstA = 0;
        int firstB = 0;
        a.Context.SchedulerLane.Enqueue(() => firstA = ControlledRandom.GetShared().Next());
        b.Context.SchedulerLane.Enqueue(() => firstB = ControlledRandom.GetShared().Next());
        cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        // Distinct nodes draw from independent application-domain streams, so a draw on one node does
        // not consume or perturb the other node's stream.
        Assert.NotEqual(firstA, firstB);
    }

    [Fact]
    public async Task SameSeedAndScheduleReplaysIdentityAndRandom()
    {
        static async Task<(Guid Guid, int Random)> RunOnce()
        {
            await using var cluster = new SimulationCluster(42, Start);
            var node = cluster.AddNode("node-1");

            Guid guid = default;
            int random = 0;
            node.Context.SchedulerLane.Enqueue(() =>
            {
                guid = ControlledGuid.NewGuid();
                random = ControlledRandom.GetShared().Next();
            });
            cluster.RunUntilIdle(TestContext.Current.CancellationToken);
            return (guid, random);
        }

        var first = await RunOnce();
        var second = await RunOnce();

        Assert.Equal(first.Guid, second.Guid);
        Assert.Equal(first.Random, second.Random);
        Assert.NotEqual(Guid.Empty, first.Guid);
    }

    [Fact]
    public async Task DifferentSeedsDiverge()
    {
        static async Task<Guid> RunWithSeed(int seed)
        {
            await using var cluster = new SimulationCluster(seed, Start);
            var node = cluster.AddNode("node-1");

            Guid guid = default;
            node.Context.SchedulerLane.Enqueue(() => guid = ControlledGuid.NewGuid());
            cluster.RunUntilIdle(TestContext.Current.CancellationToken);
            return guid;
        }

        Assert.NotEqual(await RunWithSeed(1), await RunWithSeed(2));
    }

    [Fact]
    public async Task ClusterCarriesItsRuntimeEnvironment()
    {
        var cluster = new SimulationCluster(1, Start);
        _ = cluster.AddNode("node-1");

        Assert.Same(cluster.RuntimeEnvironment, cluster.RuntimeIdentity.Environment);

        await cluster.DisposeAsync();

        Assert.False(SimulationExecutionContext.IsActive);
    }

    [Fact]
    public void ClockShimRequiresAnActiveSimulation()
    {
        var exception = Assert.Throws<SimulationNotActiveException>(() => _ = ControlledDateTime.GetUtcNow());

        Assert.Equal(
            "Controlled APIs may only be invoked while a Clockwork simulation is active.",
            exception.Message);
    }
}
