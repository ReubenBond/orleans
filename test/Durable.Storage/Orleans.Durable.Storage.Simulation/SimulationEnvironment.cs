using System.Globalization;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Testing.Simulation;
using Orleans.Testing.Simulation.Runtime.Random;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// One simulation run's shared state: its Clockwork cluster, its transport, the trace it records, the components whose
/// lifetimes it owns, and the checks it runs after every step and at teardown (test/Durable.Storage/docs/spec/verification.md §7).
/// </summary>
/// <remarks>
/// <para>
/// Clockwork runs one operation at a time on the simulation's logical thread, so the kit keeps its state without
/// locks, and a step checker reads it between steps, while no operation runs.
/// </para>
/// <para>
/// Each kit component tracks itself when it is created. Once the scenario succeeds, the driver runs
/// <see cref="TeardownAsync"/>, which drains the environment, ending every pause and healing the network so disposal
/// finds every node reachable, and then disposes the tracked components, the last created first; a component's
/// disposal cancels every timer it armed. The driver then runs the work due at that instant, without advancing virtual
/// time, and runs the teardown checks, so a check can require that nothing remains armed or unreleased.
/// </para>
/// </remarks>
public sealed class SimulationEnvironment
{
    private readonly List<IAsyncDisposable> _components = [];
    private readonly List<(string Root, Func<FatalError, bool> Injected)> _storageRoots = [];
    private bool _tearingDown;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationEnvironment"/> class over <paramref name="cluster"/>.
    /// </summary>
    /// <param name="cluster">The cluster whose seed decides every scheduling, network, and fault choice.</param>
    public SimulationEnvironment(SimulationCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        Cluster = cluster;
        Trace = new SimulationTrace(cluster.TimeProvider);
        Rpc = new SimulatedRpc(this);
        _ = Track(Rpc);
    }

    /// <summary>
    /// Gets the Clockwork cluster.
    /// </summary>
    public SimulationCluster Cluster { get; }

    /// <summary>
    /// Gets the seed which decides the run.
    /// </summary>
    public int Seed => Cluster.Seed;

    /// <summary>
    /// Gets the cluster's network: its partitions, message loss, and delays.
    /// </summary>
    public SimulationNetwork Network => Cluster.Network;

    /// <summary>
    /// Gets the transport which carries requests and answers between the cluster's nodes across its network.
    /// </summary>
    public SimulatedRpc Rpc { get; }

    /// <summary>
    /// Gets the cluster's random source.
    /// </summary>
    public SimulationRandom Random => Cluster.Random;

    /// <summary>
    /// Gets the cluster's virtual clock, as code outside the rewritten closure, such as a test's scenario, reads it.
    /// </summary>
    public TimeProvider TimeProvider => Cluster.TimeProvider;

    /// <summary>
    /// Gets the clock of code inside the rewritten closure, which the kit gives the components it hosts.
    /// </summary>
    /// <remarks>
    /// The build rewrites <see cref="TimeProvider.System"/> to Clockwork's controlled provider, which reads the
    /// simulation's virtual time; rewritten code creates timers only from it, so the kit gives it to the components it
    /// hosts.
    /// </remarks>
    public static TimeProvider Clock => TimeProvider.System;

    /// <summary>
    /// Gets the trace of the run: its workload, its injected faults, and their outcomes, on virtual time.
    /// </summary>
    public SimulationTrace Trace { get; }

    /// <summary>
    /// Gets the invariants checked after every step.
    /// </summary>
    public InvariantChecks Invariants { get; } = new();

    /// <summary>
    /// Gets the checks the driver runs once the tracked components are disposed and the work due at that instant has
    /// run.
    /// </summary>
    public InvariantChecks TeardownChecks { get; } = new();

    /// <summary>
    /// Owns <paramref name="component"/>'s lifetime: <see cref="TeardownAsync"/> disposes it.
    /// </summary>
    /// <typeparam name="T">The component's type.</typeparam>
    /// <param name="component">The component.</param>
    /// <returns><paramref name="component"/>.</returns>
    public T Track<T>(T component)
        where T : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(component);
        if (_tearingDown)
        {
            throw new InvalidOperationException("The simulation is tearing down, so it tracks no more components.");
        }

        _components.Add(component);
        return component;
    }

    /// <summary>
    /// Records <paramref name="root"/> as the storage root of a simulated node, beneath which every local storage
    /// operation of that node lies, with how the node tells a failure its fault injection caused.
    /// </summary>
    /// <param name="root">The node root.</param>
    /// <param name="injected">
    /// Returns whether the node's fault injection caused a reported failure; the driver calls it between steps, outside
    /// the simulation, so it reads no rewritten state.
    /// </param>
    public void AddStorageRoot(string root, Func<FatalError, bool> injected)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(injected);
        if (_storageRoots.Exists(existing => Holds(existing.Root, root) || Holds(root, existing.Root)))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The storage root '{root}' overlaps another node's."), nameof(root));
        }

        _storageRoots.Add((root, injected));
    }

    /// <summary>
    /// Returns the storage root which holds <paramref name="path"/>, naming the node whose local storage operation a
    /// report of a failure at the path came from (conventions.md §14), or <see langword="null"/> when no node's root
    /// holds it.
    /// </summary>
    /// <param name="path">The path a reported operation named.</param>
    /// <returns>The node root, or <see langword="null"/>.</returns>
    public string? StorageRootOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _storageRoots.Find(root => Holds(root.Root, path)).Root;
    }

    /// <summary>
    /// Returns whether the node whose root is <paramref name="root"/> injected the reported <paramref name="error"/>: only
    /// an injected failure stops a simulated process.
    /// </summary>
    /// <param name="root">The node root, as <see cref="StorageRootOf"/> returned it.</param>
    /// <param name="error">The reported failure.</param>
    /// <returns><see langword="true"/> when the node's fault injection caused the failure.</returns>
    public bool WasInjected(string root, FatalError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return _storageRoots.Find(existing => existing.Root == root).Injected is { } injected && injected(error);
    }

    /// <summary>
    /// Adds a check which the driver runs at teardown, such as a check that no work outlives its owner.
    /// </summary>
    /// <param name="name">The check's name.</param>
    /// <param name="check">The predicate, returning <see langword="null"/> when it holds.</param>
    public void AddTeardownCheck(string name, Func<string?> check) => TeardownChecks.Add(name, check);

    /// <summary>
    /// Drains the environment, resuming every node and healing every partition, and then disposes every tracked
    /// component, the last tracked first.
    /// </summary>
    /// <returns>A task which completes once every component is disposed.</returns>
    public async Task TeardownAsync()
    {
        _tearingDown = true;
        Trace.Record("teardown");
        Network.HealAllPartitions();
        foreach (var node in Cluster.Nodes)
        {
            node.Resume();
        }

        for (var index = _components.Count - 1; index >= 0; index--)
        {
            await _components[index].DisposeAsync();
        }
    }

    /// <summary>
    /// Returns a random source for one component, derived from the seed and <paramref name="site"/> alone, so a
    /// component's choices do not depend on the order in which components are created or on other components' draws.
    /// </summary>
    /// <param name="site">The component's stable name.</param>
    /// <returns>The random source.</returns>
    public SimulationRandom CreateRandom(string site) =>
        new(Cluster.SeedAuthority.GetSiteSeed(SimulationSeedDomain.Buggify, site));

    /// <summary>
    /// Runs <paramref name="action"/> on <paramref name="lane"/> after <paramref name="delay"/>, unless
    /// <paramref name="cancellationToken"/> is cancelled first, in which case <paramref name="cancelled"/> runs instead.
    /// </summary>
    /// <remarks>
    /// The wait runs on the cluster's lane, which is never suspended, so a suspended target delays only the action. A
    /// timer counts whole milliseconds, so the delay is rounded up to one. Cancellation disarms the timer.
    /// </remarks>
    internal void Schedule(SimulationSchedulerLane lane, TimeSpan delay, Action action, CancellationToken cancellationToken, Action? cancelled = null)
    {
        var wait = WholeMilliseconds(delay);
        Cluster.SchedulerLane.Enqueue(() => _ = RunAsync());

        async Task RunAsync()
        {
            try
            {
                await Task.Delay(wait, Clock, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled?.Invoke();
                return;
            }

            lane.Enqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled?.Invoke();
                }
                else
                {
                    action();
                }
            });
        }
    }

    private static bool Holds(string root, string path) =>
        path.StartsWith(root, StringComparison.Ordinal) && (path.Length == root.Length || path[root.Length] is '/' or '\\');

    /// <summary>
    /// Rounds <paramref name="delay"/> up to whole milliseconds, which is what a timer waits.
    /// </summary>
    internal static TimeSpan WholeMilliseconds(TimeSpan delay) =>
        delay.Ticks % TimeSpan.TicksPerMillisecond is var fraction and > 0
            ? delay + TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond - fraction)
            : delay;
}
