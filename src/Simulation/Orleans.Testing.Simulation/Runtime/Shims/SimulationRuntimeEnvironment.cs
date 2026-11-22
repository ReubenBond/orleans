using System.Collections.Concurrent;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;

namespace Orleans.Testing.Simulation.Runtime.Shims;

/// <summary>
/// <para>
/// The default <see cref="ISimulationRuntimeEnvironment"/> a simulation host registers. It turns the
/// host's virtual clock and <see cref="SimulationSeedAuthority"/> into the deterministic services the
/// BCL shims consume, keeping per-node mutable state isolated and drawing randomness only from the
/// <see cref="SimulationSeedDomain.Application"/> and <see cref="SimulationSeedDomain.Identity"/>
/// domains so it never perturbs the scheduler, network, or fault-injection streams.
/// </para>
/// <para>
/// <b>Time.</b> Virtual UTC comes from the host-supplied provider. High-resolution timestamps
/// (<see cref="GetTimestamp"/>) and tick counts (<see cref="GetTickCount64"/>) are derived from that
/// same virtual instant relative to a fixed origin, so both are fully deterministic and
/// machine-independent - the companion clock shims measure elapsed time against the same virtual
/// timeline rather than the machine's real <see cref="System.Diagnostics.Stopwatch"/> frequency.
/// </para>
/// <para>
/// <b>Randomness streams (per node).</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="GetSharedRandom"/> returns one stable instance per node, seeded from the application
/// domain - the simulation analogue of <see cref="System.Random.Shared"/>.
/// </description></item>
/// <item><description>
/// <see cref="CreateUnseededRandom"/> returns a fresh instance per construction, seeded from a
/// per-node monotonic construction counter - reproducible under a fixed schedule, never shared.
/// </description></item>
/// <item><description>
/// <see cref="FillIdentityBytes"/> advances a separate per-node identity stream (identity domain), so
/// GUID generation never perturbs application randomness.
/// </description></item>
/// </list>
/// <para>
/// <b>Processor count.</b> <see cref="GetProcessorCount"/> returns one simulated processor count for every node,
/// fixed when the runtime is created (see <see cref="DefaultProcessorCount"/>).
/// </para>
/// </summary>
public sealed class SimulationRuntimeEnvironment : ISimulationRuntimeEnvironment
{
    /// <summary>
    /// The simulated processor count a runtime uses unless its host configures another: 4. It is big enough that the
    /// work the BCL sizes by <see cref="Environment.ProcessorCount"/> (a <c>Parallel</c> loop's default degree of
    /// parallelism, a <c>Partitioner</c>'s range size) runs on several workers whose interleavings the scheduler
    /// explores; small enough to keep the schedule space and the cost of each run bounded; and the same on every host,
    /// so a seed replays on any machine.
    /// </summary>
    internal const int DefaultProcessorCount = 4;

    /// <summary>The stable site-id prefix for the per-node shared random stream.</summary>
    private const string SharedRandomSite = "clockwork.random.shared/";

    /// <summary>The stable site-id prefix for per-node unseeded <c>new Random()</c> constructions.</summary>
    private const string UnseededRandomSite = "clockwork.random.unseeded/";

    /// <summary>The stable site-id prefix for the per-node identity byte stream.</summary>
    private const string IdentitySite = "clockwork.identity/";

    /// <summary>The stable site-id prefix for the per-node controlled crypto-random stream.</summary>
    private const string CryptoRandomSite = "clockwork.crypto.insecure/";

    /// <summary>The key used for cluster-level execution not scoped to a node.</summary>
    private const string ClusterNodeKey = "<cluster>";

    private readonly SimulationSeedAuthority _seedAuthority;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly DateTimeOffset _origin;
    private readonly int _processorCount;
    private readonly ConcurrentDictionary<string, NodeStreams> _nodeStreams = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="SimulationRuntimeEnvironment"/> class.</summary>
    /// <param name="seedAuthority">The seed authority deriving per-node deterministic seeds.</param>
    /// <param name="utcNowProvider">A provider for the current virtual UTC instant.</param>
    /// <param name="localTimeZone">The local time zone simulated nodes observe.</param>
    /// <param name="origin">
    /// The fixed virtual origin that <see cref="GetTickCount64"/> and <see cref="GetTimestamp"/> are
    /// measured from. Must be less than or equal to every instant the clock will return.
    /// </param>
    /// <param name="processorCount">The simulated processor count every node observes; must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="processorCount"/> is zero or negative.</exception>
    public SimulationRuntimeEnvironment(
        SimulationSeedAuthority seedAuthority,
        Func<DateTimeOffset> utcNowProvider,
        TimeZoneInfo localTimeZone,
        DateTimeOffset origin,
        int processorCount)
    {
        ArgumentNullException.ThrowIfNull(seedAuthority);
        ArgumentNullException.ThrowIfNull(utcNowProvider);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processorCount);

        _seedAuthority = seedAuthority;
        _utcNowProvider = utcNowProvider;
        _localTimeZone = localTimeZone;
        _origin = origin;
        _processorCount = processorCount;
    }

    /// <inheritdoc/>
    public DateTimeOffset GetUtcNow(SimulationNodeIdentity? node) => _utcNowProvider();

    /// <inheritdoc/>
    public TimeZoneInfo GetLocalTimeZone(SimulationNodeIdentity? node) => _localTimeZone;

    /// <inheritdoc/>
    public int GetProcessorCount(SimulationNodeIdentity? node) => _processorCount;

    /// <inheritdoc/>
    public long GetTimestamp(SimulationNodeIdentity? node)
    {
        // Virtual high-resolution timestamp measured in 100 ns ticks since the fixed origin. The
        // companion Stopwatch.GetElapsedTime shim measures against this same timeline, so elapsed
        // durations are exact and machine-independent regardless of the host Stopwatch frequency.
        var elapsed = _utcNowProvider() - _origin;
        return elapsed.Ticks;
    }

    /// <inheritdoc/>
    public long GetTickCount64(SimulationNodeIdentity? node)
    {
        var elapsedMs = (_utcNowProvider() - _origin).Ticks / TimeSpan.TicksPerMillisecond;
        return elapsedMs;
    }

    /// <inheritdoc/>
    public System.Random GetSharedRandom(SimulationNodeIdentity? node) => GetStreams(node).Shared;

    /// <inheritdoc/>
    public System.Random CreateUnseededRandom(SimulationNodeIdentity? node)
    {
        var streams = GetStreams(node);
        var ordinal = Interlocked.Increment(ref streams.UnseededCount);
        var seed = _seedAuthority.GetSiteSeed(
            SimulationSeedDomain.Application,
            $"{UnseededRandomSite}{streams.Key}/{ordinal}");
        return new System.Random(seed);
    }

    /// <inheritdoc/>
    public void FillIdentityBytes(SimulationNodeIdentity? node, Span<byte> destination)
    {
        var streams = GetStreams(node);
        lock (streams.IdentityGate)
        {
            streams.Identity.NextBytes(destination);
        }
    }

    /// <inheritdoc/>
    public void FillCryptoRandomBytes(SimulationNodeIdentity? node, Span<byte> destination)
    {
        var streams = GetStreams(node);
        lock (streams.CryptoRandomGate)
        {
            streams.CryptoRandom.NextBytes(destination);
        }
    }

    private NodeStreams GetStreams(SimulationNodeIdentity? node)
    {
        var key = node?.Address ?? ClusterNodeKey;
        return _nodeStreams.GetOrAdd(key, static (k, self) => self.CreateStreams(k), this);
    }

    private NodeStreams CreateStreams(string key)
    {
        var sharedSeed = _seedAuthority.GetSiteSeed(SimulationSeedDomain.Application, SharedRandomSite + key);
        var identitySeed = _seedAuthority.GetSiteSeed(SimulationSeedDomain.Identity, IdentitySite + key);
        var cryptoRandomSeed = _seedAuthority.GetSiteSeed(SimulationSeedDomain.Identity, CryptoRandomSite + key);
        return new NodeStreams(
            key,
            shared: new SynchronizedRandom(sharedSeed),
            identity: new System.Random(identitySeed),
            cryptoRandom: new System.Random(cryptoRandomSeed));
    }

    private sealed class NodeStreams(string key, System.Random shared, System.Random identity, System.Random cryptoRandom)
    {
        public string Key { get; } = key;

        public System.Random Shared { get; } = shared;

        public System.Random Identity { get; } = identity;

        public object IdentityGate { get; } = new();

        public System.Random CryptoRandom { get; } = cryptoRandom;

        public object CryptoRandomGate { get; } = new();

        public int UnseededCount;
    }

    private sealed class SynchronizedRandom(int seed) : System.Random
    {
        private readonly object _gate = new();
        private readonly System.Random _inner = new(seed);

        public override int Next()
        {
            lock (_gate)
            {
                return _inner.Next();
            }
        }

        public override int Next(int maxValue)
        {
            lock (_gate)
            {
                return _inner.Next(maxValue);
            }
        }

        public override int Next(int minValue, int maxValue)
        {
            lock (_gate)
            {
                return _inner.Next(minValue, maxValue);
            }
        }

        public override long NextInt64()
        {
            lock (_gate)
            {
                return _inner.NextInt64();
            }
        }

        public override long NextInt64(long maxValue)
        {
            lock (_gate)
            {
                return _inner.NextInt64(maxValue);
            }
        }

        public override long NextInt64(long minValue, long maxValue)
        {
            lock (_gate)
            {
                return _inner.NextInt64(minValue, maxValue);
            }
        }

        public override float NextSingle()
        {
            lock (_gate)
            {
                return _inner.NextSingle();
            }
        }

        public override double NextDouble()
        {
            lock (_gate)
            {
                return _inner.NextDouble();
            }
        }

        public override void NextBytes(byte[] buffer)
        {
            lock (_gate)
            {
                _inner.NextBytes(buffer);
            }
        }

        public override void NextBytes(Span<byte> buffer)
        {
            lock (_gate)
            {
                _inner.NextBytes(buffer);
            }
        }

        protected override double Sample()
        {
            lock (_gate)
            {
                return _inner.NextDouble();
            }
        }
    }
}
