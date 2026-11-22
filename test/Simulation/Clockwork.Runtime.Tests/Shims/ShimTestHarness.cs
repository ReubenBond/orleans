using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Tests.Shims;

/// <summary>
/// Test helpers for driving the deterministic shims: entering a complete active simulation scope
/// (optionally with a node) and building a default
/// <see cref="SimulationRuntimeEnvironment"/> with a controllable virtual clock.
/// </summary>
internal static class ShimTestHarness
{
    public const string DefaultNodeAddress = "10.0.0.1";

    /// <summary>The fixed virtual origin used by the default test environment.</summary>
    public static readonly DateTimeOffset Origin = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static SimulationRuntimeIdentity NewRuntime(int seed = 12345, string? description = null) =>
        new(Guid.NewGuid(), seed, description);

    public static MutableClock CreateClock(DateTimeOffset? start = null) => new(start ?? Origin);

    public static TestEnvironment CreateEnvironment(
        MutableClock clock,
        int simulationSeed = 12345,
        TimeZoneInfo? localTimeZone = null,
        int processorCount = SimulationRuntimeEnvironment.DefaultProcessorCount) =>
        new(clock, simulationSeed, localTimeZone ?? TimeZoneInfo.Utc, processorCount);

    /// <summary>
    /// Runs <paramref name="body"/> inside an active simulation with the given environment registered
    /// and the default node entered. Tears everything down afterwards.
    /// </summary>
    public static T RunInSimulation<T>(
        TestEnvironment environment,
        Func<T> body,
        string? nodeAddress = DefaultNodeAddress,
        SimulationRuntimeIdentity? runtime = null)
    {
        var activeRuntime = runtime ?? NewRuntime();
        using var scheduler = new SimulationScheduler(
            activeRuntime,
            new SimulationSeedAuthority(environment.SimulationSeed),
            environment.Clock.UtcNow,
            environment.LocalTimeZone,
            processorCount: environment.ProcessorCount);
        environment.Clock.Bind(scheduler);

        // The test thread is the simulation's controlling thread: it calls controlled APIs and drives the scheduler.
        using (scheduler.EnterControlScope())
        using (SimulationExecutionContext.EnterRuntime(activeRuntime))
        {
            if (nodeAddress is null)
            {
                return body();
            }

            using (SimulationExecutionContext.EnterNode(new SimulationNodeIdentity(nodeAddress)))
            {
                return body();
            }
        }
    }

    public static void RunInSimulation(
        TestEnvironment environment,
        Action body,
        string? nodeAddress = DefaultNodeAddress,
        SimulationRuntimeIdentity? runtime = null)
    {
        RunInSimulation<object?>(
            environment,
            () =>
            {
                body();
                return null;
            },
            nodeAddress,
            runtime);
    }

    /// <summary>A simple mutable virtual clock for tests.</summary>
    internal sealed class MutableClock(DateTimeOffset start)
    {
        private DateTimeOffset _utcNow = start;
        private SimulationScheduler? _scheduler;

        public DateTimeOffset UtcNow => _scheduler?.UtcNow ?? _utcNow;

        public void Advance(TimeSpan delta)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
            if (_scheduler is null)
            {
                _utcNow += delta;
            }
            else
            {
                _scheduler.AdvanceVirtualTimeTo(_scheduler.VirtualTime + delta);
            }
        }

        public void Bind(SimulationScheduler scheduler) => _scheduler = scheduler;
    }

    internal sealed class TestEnvironment : ISimulationRuntimeEnvironment
    {
        private readonly SimulationRuntimeEnvironment _inner;

        public TestEnvironment(
            MutableClock clock,
            int simulationSeed,
            TimeZoneInfo localTimeZone,
            int processorCount)
        {
            Clock = clock;
            SimulationSeed = simulationSeed;
            LocalTimeZone = localTimeZone;
            ProcessorCount = processorCount;
            _inner = new SimulationRuntimeEnvironment(
                new SimulationSeedAuthority(simulationSeed),
                () => clock.UtcNow,
                localTimeZone,
                Origin,
                processorCount);
        }

        public MutableClock Clock { get; }

        public int SimulationSeed { get; }

        public TimeZoneInfo LocalTimeZone { get; }

        public int ProcessorCount { get; }

        public DateTimeOffset GetUtcNow(SimulationNodeIdentity? node) => _inner.GetUtcNow(node);

        public TimeZoneInfo GetLocalTimeZone(SimulationNodeIdentity? node) => _inner.GetLocalTimeZone(node);

        public int GetProcessorCount(SimulationNodeIdentity? node) => _inner.GetProcessorCount(node);

        public long GetTimestamp(SimulationNodeIdentity? node) => _inner.GetTimestamp(node);

        public long GetTickCount64(SimulationNodeIdentity? node) => _inner.GetTickCount64(node);

        public System.Random GetSharedRandom(SimulationNodeIdentity? node) => _inner.GetSharedRandom(node);

        public System.Random CreateUnseededRandom(SimulationNodeIdentity? node) => _inner.CreateUnseededRandom(node);

        public void FillIdentityBytes(SimulationNodeIdentity? node, Span<byte> destination) =>
            _inner.FillIdentityBytes(node, destination);

        public void FillCryptoRandomBytes(SimulationNodeIdentity? node, Span<byte> destination) =>
            _inner.FillCryptoRandomBytes(node, destination);
    }
}
