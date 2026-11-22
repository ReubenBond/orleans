using System.Globalization;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// A simulated node which hosts one LedgerStore over <see cref="SimulatedWalStorage"/>, with per-node faults: process
/// and operating-system crashes, restarts from durable state, pauses, isolation, media damage, and local storage
/// failures, which stop the node's process (test/Durable.Storage/docs/spec/verification.md §7.2, §7.3).
/// </summary>
/// <remarks>
/// <para>
/// The node's disk holds every WAL write in flight and completes each after its own random latency on the node's lane,
/// so writes complete out of issue order and a crash cuts the WAL where some writes are durable and others are lost or,
/// in an operating-system crash, torn. A crash stops the store at once: requests in flight on it are never answered, and
/// the node answers nothing until it restarts over the storage the crash left. <see cref="CrashOnNextWalWriteAsync"/>
/// times a crash to the next WAL write, as it leaves for the disk or as it completes, so the request which issued it may
/// land without its answer. The node root's cluster claim, once published, survives every crash, as a published claim
/// does.
/// </para>
/// <para>
/// The node publishes its store to its observers whenever the store opens or serves a request, and runs its recovery
/// checks over each restarted store before it serves; <see cref="LedgerStoreInvariants"/> projects and checks what the
/// store shows. It reports each media damage to its damage observers as the damage happens, so a ledger trace logs it.
/// The node tracks itself in its environment, and its disposal stops its store, resumes it, and disarms its
/// disk.
/// </para>
/// <para>
/// <see cref="FailNextStorageOperation"/> arms a failure of the running process's next storage operation of one kind.
/// The disk reports it to the fatal-error handler and stops, as the process does (conventions.md §14): from then on the
/// node answers nothing, and the node crashes, as a process or an operating system, and restarts from what its disk
/// kept. The node root is its address, which the node registers with its environment, so the driver attributes the
/// report to the node.
/// </para>
/// </remarks>
public sealed class LedgerStoreNode : IAsyncDisposable
{
    private readonly SimulationEnvironment _environment;
    private readonly Guid _cluster;
    private readonly LedgerStoreOptions _options;
    private readonly LedgerStoreCheckpointOptions _checkpointOptions;
    private readonly SimulationRandom _random;
    private readonly SimulatedNodeRootClaim _claim = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Action<LedgerStore>> _observers = [];
    private readonly List<Func<LedgerStore, ValueTask>> _recoveryChecks = [];
    private readonly List<Action> _damageObservers = [];
    private readonly List<Exception> _injected = [];
    private CancellationTokenSource _disk = new();
    private StorageFault? _armed;
    private (Action Crash, bool Durable)? _walWriteCrash;
    private LedgerStore? _store;
    private IReadOnlyList<ulong> _recoveredFiles = [];
    private NodeId? _identity;
    private bool _starting;
    private bool _disposed;
    private int _pauses;

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerStoreNode"/> class: attaches the node to the cluster with an
    /// empty disk, and tracks it in <paramref name="environment"/>. <see cref="StartAsync"/> creates its store.
    /// </summary>
    /// <param name="environment">The simulation.</param>
    /// <param name="address">The node's network address.</param>
    /// <param name="cluster">The cluster which claims the node root.</param>
    /// <param name="options">The store's settings, or <see langword="null"/> for <see cref="DefaultOptions"/>.</param>
    /// <param name="checkpointOptions">
    /// When replicas checkpoint, or <see langword="null"/> for <see cref="DefaultCheckpointOptions"/>.
    /// </param>
    public LedgerStoreNode(
        SimulationEnvironment environment,
        string address,
        Guid cluster,
        LedgerStoreOptions? options = null,
        LedgerStoreCheckpointOptions? checkpointOptions = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        _environment = environment;
        _cluster = cluster;
        _options = options ?? DefaultOptions(address);
        _checkpointOptions = checkpointOptions ?? DefaultCheckpointOptions;
        _random = environment.CreateRandom(address);
        environment.AddStorageRoot(_options.DirectoryPath, Injected);
        Node = environment.Cluster.AddNode(address);
        Storage = Attach(new SimulatedWalStorage(_random.Next()));
        _ = environment.Track(this);
    }

    /// <summary>
    /// Gets the checkpoint settings a node uses by default: frequent checkpoints, so recovery starts from them.
    /// </summary>
    public static LedgerStoreCheckpointOptions DefaultCheckpointOptions { get; } = new()
    {
        CheckpointInterval = TimeSpan.FromMilliseconds(100),
        CheckpointReplayBytes = 16 * 512,
        MaxCheckpointFiles = 4,
    };

    /// <summary>
    /// Gets the Clockwork node.
    /// </summary>
    public SimulationNode Node { get; }

    /// <summary>
    /// Gets the node's network address.
    /// </summary>
    public string Address => Node.NetworkAddress;

    /// <summary>
    /// Gets the node identity, which the first <see cref="StartAsync"/> creates.
    /// </summary>
    public NodeId NodeId => _identity ?? throw new InvalidOperationException(Invariant($"{Address} has never started."));

    /// <summary>
    /// Gets the node's disk as its current process observes it; each crash replaces it with the disk a restart finds.
    /// </summary>
    public SimulatedWalStorage Storage { get; private set; }

    /// <summary>
    /// Gets the store of the node's running process.
    /// </summary>
    public LedgerStore Store => _store ?? throw new InvalidOperationException(Invariant($"{Address} is down."));

    /// <summary>
    /// Gets a value indicating whether the node's process is running: started, and not stopped by a local storage
    /// failure.
    /// </summary>
    public bool IsUp => _store is not null && !Storage.IsStopped;

    /// <summary>
    /// Gets the number of times the node's process has started.
    /// </summary>
    public int Incarnation { get; private set; }

    /// <summary>
    /// Gets the number of times a local storage failure stopped the node's process.
    /// </summary>
    public int FailStops { get; private set; }

    /// <summary>
    /// Gets a task which completes once the node's process has restarted after its last local storage failure.
    /// </summary>
    public Task FailStopped { get; private set; } = Task.CompletedTask;


    /// <summary>
    /// Gets or sets the shortest time a WAL write stays in flight before the disk completes it.
    /// </summary>
    public TimeSpan MinDiskLatency { get; set; }

    /// <summary>
    /// Gets or sets the longest time a WAL write stays in flight before the disk completes it.
    /// </summary>
    public TimeSpan MaxDiskLatency { get; set; } = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// Returns the store settings a node uses by default: small pages and files, so a short run rolls WAL files.
    /// </summary>
    /// <param name="address">The node's address, which names its root.</param>
    /// <returns>The settings.</returns>
    public static LedgerStoreOptions DefaultOptions(string address) => new()
    {
        DirectoryPath = address,
        PageSize = 512,
        FileSize = 64 * 512,
        WriteWindowPages = 8,
        PreallocateFiles = false,
    };

    /// <summary>
    /// Creates <paramref name="count"/> nodes named <c>replica-{i}</c> in <paramref name="environment"/> and starts
    /// each.
    /// </summary>
    /// <param name="environment">The simulation.</param>
    /// <param name="cluster">The cluster which claims each node root.</param>
    /// <param name="count">The number of nodes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="lease">Optional lease settings overriding <see cref="LedgerStoreOptions.LeaseDuration"/>.</param>
    /// <returns>The started nodes.</returns>
    public static async Task<LedgerStoreNode[]> StartNodesAsync(
        SimulationEnvironment environment,
        Guid cluster,
        int count,
        CancellationToken cancellationToken = default,
        LedgerLeaseOptions? lease = null)
    {
        var nodes = new LedgerStoreNode[count];
        for (var index = 0; index < count; index++)
        {
            var options = DefaultOptions(Invariant($"replica-{index}"));
            if (lease is not null)
            {
                options = options with { LeaseDuration = lease.LeaseDuration };
            }

            nodes[index] = new LedgerStoreNode(environment, options.DirectoryPath, cluster, options);
            await nodes[index].StartAsync(cancellationToken);
        }

        return nodes;
    }

    /// <summary>
    /// Calls <paramref name="observer"/> with the node's store whenever the store opens or serves a request, before the
    /// request's answer leaves the node.
    /// </summary>
    /// <param name="observer">The observer.</param>
    public void OnPublished(Action<LedgerStore> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _observers.Add(observer);
    }

    /// <summary>
    /// Runs <paramref name="check"/> over each restarted store after it recovers and before it serves.
    /// </summary>
    /// <param name="check">The check, which reports what it finds through the state its invariants read.</param>
    public void OnRecovered(Func<LedgerStore, ValueTask> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _recoveryChecks.Add(check);
    }

    /// <summary>
    /// Calls <paramref name="observer"/> whenever <see cref="Damage()"/> or <see cref="Damage(WalPage)"/> damages the
    /// node's media, as the damage happens.
    /// </summary>
    /// <param name="observer">The observer.</param>
    public void OnDamaged(Action observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _damageObservers.Add(observer);
    }

    /// <summary>
    /// Returns whether <paramref name="store"/> is the store of the node's running process.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <returns><see langword="true"/> when the store's process still runs.</returns>
    public bool IsCurrent(LedgerStore store) => ReferenceEquals(store, _store) && !Storage.IsStopped;

    /// <summary>
    /// Starts the node's process: creates its store on the first start, and otherwise recovers it from the disk the
    /// last crash left and runs the recovery checks before it serves.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task which completes once the store serves.</returns>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_store is not null || _starting)
        {
            throw new InvalidOperationException(Invariant($"{Address} is already running."));
        }

        _starting = true;
        try
        {
            // The WAL files of earlier opens, whose crash regions this start's recovery classifies once and for all.
            var recovered = _identity is null ? [] : Storage.ListFiles(LedgerStore.WalPath(_options.DirectoryPath));
            var store = await LedgerStore.OpenAsync(
                Storage,
                _options,
                _cluster,
                create: _identity is null,
                _checkpointOptions,
                SimulationEnvironment.Clock,
                _claim,
                cancellationToken);
            _identity ??= store.NodeId;
            Incarnation++;
            Publish(store);
            if (Incarnation > 1)
            {
                foreach (var check in _recoveryChecks)
                {
                    await check(store);
                }
            }

            _store = store;
            _recoveredFiles = recovered;
            _environment.Trace.Record(Invariant(
                $"start {Address}: incarnation {Incarnation} lists [{string.Join(", ", store.Replicas)}]"));
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>
    /// Crashes the node's process, or its operating system when <paramref name="operatingSystem"/> is set: the disk
    /// keeps what a restart finds, and the process answers nothing more.
    /// </summary>
    /// <param name="operatingSystem">Whether the operating system crashes, losing or tearing the writes in flight.</param>
    /// <returns>A task which completes once the crashed process's resources are released.</returns>
    public async Task CrashAsync(bool operatingSystem)
    {
        var store = Store;
        _store = null;
        _armed = null;
        _walWriteCrash = null;
        var inFlight = Storage.PendingWriteCount;
        var disk = _disk;
        Storage = Attach(Storage.Crash(operatingSystem));
        await disk.CancelAsync();
        disk.Dispose();
        _environment.Trace.Record(Invariant($"crash {Address} ({(operatingSystem ? "operating system" : "process")}) with {inFlight} writes in flight"));
        try
        {
            await store.DisposeAsync();
        }
#pragma warning disable CA1031 // The crashed process's teardown releases simulated resources; its errors are the crash's.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // The crashed disk fails the WAL's pending writes and its background work.
            _environment.Trace.Record(Invariant($"crashed process of {Address} stopped with {exception.GetType().Name}"));
        }
    }

    /// <summary>
    /// Crashes the node's process, or its operating system when <paramref name="operatingSystem"/> is set, as soon as its
    /// disk takes the running process's next WAL write, which the crash then finds in flight, or, with
    /// <paramref name="durable"/>, as soon as the disk completes that write, or after <paramref name="limit"/> when no WAL
    /// write comes by then. A process crash keeps a write in flight, and a durable write survives any crash, so the record
    /// it carries may land without an answer; an operating-system crash loses or tears a write in flight.
    /// </summary>
    /// <param name="operatingSystem">Whether the operating system crashes, losing or tearing the writes in flight.</param>
    /// <param name="durable">Whether the crash waits for the write to complete.</param>
    /// <param name="limit">How long the crash waits for a WAL write.</param>
    /// <param name="crashing">Runs just before the crash.</param>
    /// <returns>
    /// A task which completes once the crash completes, with <see langword="false"/> when the running process stopped
    /// another way first and nothing crashed.
    /// </returns>
    public Task<bool> CrashOnNextWalWriteAsync(bool operatingSystem, bool durable, TimeSpan limit, Action? crashing = null)
    {
        var store = Store;
        var crashed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fired = false;
        _walWriteCrash = (Fire, durable);
        _environment.Schedule(_environment.Cluster.SchedulerLane, limit, Fire, _lifetime.Token, () => crashed.TrySetResult(false));
        return crashed.Task;

        // The disk calls this on the node's lane as it takes the write, so the crash runs next on the cluster's lane,
        // before the disk completes the write after its latency, or as it completes the write, so the crash races the
        // answers which waited for it.
        void Fire()
        {
            if (fired)
            {
                return;
            }

            fired = true;
            _environment.Schedule(_environment.Cluster.SchedulerLane, TimeSpan.Zero, () => _ = CrashNowAsync(), _lifetime.Token, () => crashed.TrySetResult(false));
        }

        async Task CrashNowAsync()
        {
            if (!IsCurrent(store))
            {
                _ = crashed.TrySetResult(false);
                return;
            }

            try
            {
                crashing?.Invoke();
                await CrashAsync(operatingSystem);
                _ = crashed.TrySetResult(true);
            }
#pragma warning disable CA1031 // The crash's failure is its caller's outcome.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                _ = crashed.TrySetException(exception);
            }
        }
    }

    /// <summary>
    /// Arms a failure of the running process's next storage operation of kind <paramref name="fault"/>, replacing any
    /// failure armed before. The failure stops the process: the node answers nothing more, crashes, and restarts.
    /// </summary>
    /// <param name="fault">The kind of operation which fails.</param>
    public void FailNextStorageOperation(StorageFault fault)
    {
        _armed = fault;
        _environment.Trace.Record(Invariant($"arm a {fault} failure on {Address}"));
    }

    /// <summary>
    /// Disarms a storage failure armed and not yet injected.
    /// </summary>
    public void DisarmStorageFailure() => _armed = null;

    /// <summary>
    /// Suspends the node for <paramref name="duration"/>: until then it serves no request and its disk completes no
    /// write. A later pause replaces an earlier one's end.
    /// </summary>
    /// <param name="duration">How long the node is suspended.</param>
    public void Pause(TimeSpan duration)
    {
        var pause = ++_pauses;
        Node.Suspend();
        _environment.Schedule(
            _environment.Cluster.SchedulerLane,
            duration,
            () =>
            {
                if (pause == _pauses)
                {
                    Node.Resume();
                }
            },
            _lifetime.Token);
        _environment.Trace.Record(Invariant($"pause {Address} for {duration.TotalMilliseconds:0.###} ms"));
    }

    /// <summary>
    /// Partitions the node from every other node.
    /// </summary>
    public void Isolate()
    {
        _environment.Network.IsolateNode(Address);
        _environment.Trace.Record(Invariant($"isolate {Address}"));
    }

    /// <summary>
    /// Heals the node's partitions.
    /// </summary>
    public void Reconnect()
    {
        _environment.Network.ReconnectNode(Address);
        _environment.Trace.Record(Invariant($"reconnect {Address}"));
    }

    /// <summary>
    /// Damages a few bytes of one page of a WAL file which the running process's recovery classified, as media damage
    /// does, so every later read of a record on it reports corruption (wal.md §6).
    /// </summary>
    /// <returns><see langword="false"/> when the node is down or holds no such page.</returns>
    public bool Damage()
    {
        if (!IsUp)
        {
            return false;
        }

        var path = LedgerStore.WalPath(_options.DirectoryPath);
        var files = Storage.ListFiles(path).Intersect(_recoveredFiles).ToArray();
        if (files.Length == 0)
        {
            return false;
        }

        var file = _random.Choose(files);
        var pages = Storage.ReadDurable(path, file).Length / _options.PageSize;
        if (pages < 2)
        {
            return false;
        }

        // The first page is the file's header; a data page's damage faults only the records on it.
        DamagePage(path, file, 1 + _random.Next(pages - 1));
        return true;
    }

    /// <summary>
    /// Damages a few bytes of <paramref name="page"/>, a data page of a WAL file which the running process's recovery
    /// classified, as media damage does, so every later read of a record on it reports corruption (wal.md §6).
    /// </summary>
    /// <param name="page">The page.</param>
    /// <returns><see langword="false"/> when the node is down or <paramref name="page"/> is no such page.</returns>
    public bool Damage(WalPage page)
    {
        var path = LedgerStore.WalPath(_options.DirectoryPath);
        if (!IsUp || !_recoveredFiles.Contains(page.File) || !Storage.ListFiles(path).Contains(page.File)
            || Storage.ReadDurable(path, page.File).Length / _options.PageSize <= 1 + page.Index)
        {
            return false;
        }

        DamagePage(path, page.File, 1 + page.Index);
        return true;
    }

    /// <summary>
    /// Returns the durable bytes of each file of the node's WAL, which <see cref="PagesWrittenSince"/> compares with the
    /// WAL as it stands later.
    /// </summary>
    public IReadOnlyDictionary<ulong, byte[]> SnapshotWal()
    {
        var path = LedgerStore.WalPath(_options.DirectoryPath);
        return Storage.ListFiles(path).ToDictionary(static file => file, file => Storage.ReadDurable(path, file));
    }

    /// <summary>
    /// Returns the data pages of the node's WAL whose durable bytes differ from <paramref name="before"/>'s, a snapshot
    /// <see cref="SnapshotWal"/> took, or which it lacked: the pages written since, which <see cref="Damage(WalPage)"/>
    /// takes.
    /// </summary>
    public IReadOnlyList<WalPage> PagesWrittenSince(IReadOnlyDictionary<ulong, byte[]> before)
    {
        ArgumentNullException.ThrowIfNull(before);
        var size = _options.PageSize;
        List<WalPage> pages = [];
        foreach (var (file, bytes) in SnapshotWal())
        {
            var old = before.GetValueOrDefault(file, []);

            // The first page is the file's header.
            for (var index = 1; (index + 1) * size <= bytes.Length; index++)
            {
                var page = bytes.AsSpan(index * size, size);
                if ((index + 1) * size > old.Length || !page.SequenceEqual(old.AsSpan(index * size, size)))
                {
                    pages.Add(new WalPage(file, (uint)(index - 1)));
                }
            }
        }

        return pages;
    }

    /// <summary>
    /// Serves a request with the node's running store, as the node's server does, and publishes the store.
    /// </summary>
    /// <typeparam name="T">The answer's type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="acknowledged">
    /// Records what the running store acknowledged, once the store is published and before any other step.
    /// </param>
    /// <param name="discard">Releases an answer which a crash keeps from being sent.</param>
    /// <param name="failed">
    /// Observes the failure the running store answers, once the store is published and before any other step.
    /// </param>
    /// <returns>The answer.</returns>
    /// <exception cref="NodeDownException">The node's process is down, or crashed while serving the request.</exception>
    public async ValueTask<T> ServeAsync<T>(
        Func<LedgerStore, ValueTask<T>> request,
        Action<T>? acknowledged = null,
        Action<T>? discard = null,
        Action<Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var store = _store is { } running && !Storage.IsStopped ? running : throw new NodeDownException(Address);
        T result;
        try
        {
            result = await request(store);
        }
        catch (Exception exception) when (!IsCurrent(store))
        {
            throw new NodeDownException(Address, exception);
        }
        catch (Exception exception)
        {
            // A failed request may have changed the store.
            Publish(store);
            failed?.Invoke(exception);
            throw;
        }

        if (!IsCurrent(store))
        {
            discard?.Invoke(result);
            throw new NodeDownException(Address);
        }

        Publish(store);
        acknowledged?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Serves a request which answers nothing but its completion with the node's running store, and publishes the
    /// store.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="acknowledged">
    /// Records what the running store acknowledged, once the store is published and before any other step.
    /// </param>
    /// <returns>A task which completes once the store completed the request.</returns>
    /// <exception cref="NodeDownException">The node's process is down, or crashed while serving the request.</exception>
    public async ValueTask ServeAsync(Func<LedgerStore, ValueTask> request, Action? acknowledged = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var store = _store is { } running && !Storage.IsStopped ? running : throw new NodeDownException(Address);
        try
        {
            await request(store);
        }
        catch (Exception exception) when (!IsCurrent(store))
        {
            throw new NodeDownException(Address, exception);
        }
        catch
        {
            // A failed request may have changed the store.
            Publish(store);
            throw;
        }

        if (!IsCurrent(store))
        {
            throw new NodeDownException(Address);
        }

        Publish(store);
        acknowledged?.Invoke();
    }

    /// <summary>
    /// Serves a request on the node's own scheduler lane, as its own process does, so it meets the node's pauses and
    /// crashes but not the network; otherwise as <see cref="ServeAsync{T}"/> does.
    /// </summary>
    /// <typeparam name="T">The answer's type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="acknowledged">Records what the running store acknowledged.</param>
    /// <returns>The answer.</returns>
    public Task<T> ServeLocallyAsync<T>(Func<LedgerStore, ValueTask<T>> request, Action<T>? acknowledged = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _environment.Schedule(Node.Context.SchedulerLane, TimeSpan.Zero, () => _ = RunAsync(), CancellationToken.None);
        return answer.Task;

        async Task RunAsync()
        {
            try
            {
                _ = answer.TrySetResult(await ServeAsync(request, acknowledged));
            }
#pragma warning disable CA1031 // The node's failure is the caller's outcome.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                _ = answer.TrySetException(exception);
            }
        }
    }

    /// <summary>
    /// Drops <paramref name="ledger"/> on the node's running store, on its own scheduler lane, as the node's
    /// reconciliation does once its view of the ledger map revokes the ledger (ledger-replicas.md §2.3), recording the request
    /// and the store's acknowledgement in <paramref name="audit"/>.
    /// </summary>
    /// <param name="ledger">The ledger ID.</param>
    /// <param name="audit">The node's ledger.</param>
    /// <param name="acknowledged">Observes the store's acknowledgement, after the ledger records it.</param>
    /// <returns>A task which completes once the drop is durable.</returns>
    /// <exception cref="NodeDownException">The node's process is down, or crashed while dropping.</exception>
    public Task DropReplicaAsync(LedgerId ledger, ReplicaAudit audit, Action? acknowledged = null)
    {
        ArgumentNullException.ThrowIfNull(audit);
        audit.RequestDrop(ledger);
        return ServeLocallyAsync(
            async store =>
            {
                await store.DropReplicaAsync(ledger, CancellationToken.None);
                return true;
            },
            _ =>
            {
                audit.AcknowledgeDrop(ledger);
                acknowledged?.Invoke();
            });
    }

    /// <summary>
    /// Serves a request with a handle over the running store's replica of <paramref name="ledger"/>, as
    /// <see cref="ServeAsync{T}"/> does.
    /// </summary>
    /// <typeparam name="T">The answer's type.</typeparam>
    /// <param name="ledger">The ledger ID.</param>
    /// <param name="options">The replica's options.</param>
    /// <param name="request">The request.</param>
    /// <param name="acknowledged">Records what the running store acknowledged.</param>
    /// <param name="discard">Releases an answer which a crash keeps from being sent.</param>
    /// <param name="failed">Observes the failure the running store answers.</param>
    /// <returns>The answer.</returns>
    public ValueTask<T> ServeReplicaAsync<T>(
        LedgerId ledger,
        LedgerReplicaOptions options,
        Func<ILedgerReplica, ValueTask<T>> request,
        Action<T>? acknowledged = null,
        Action<T>? discard = null,
        Action<Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ServeAsync(
            async store =>
            {
                await using var replica = store.OpenReplica(ledger, options);
                return await request(replica);
            },
            acknowledged,
            discard,
            failed);
    }

    /// <summary>
    /// Stops the node: disarms its pauses, resumes it, disposes its store, and disarms its disk.
    /// </summary>
    /// <returns>A task which completes once the node holds no work.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetime.CancelAsync();
        Node.Resume();

        // A restart after a local storage failure which already began completes, and its store is disposed below; a
        // process a failure stopped crashes, as it would have.
        await FailStopped;
        if (_store is not null && Storage.IsStopped)
        {
            await CrashAsync(operatingSystem: false);
        }

        if (_store is { } store)
        {
            _store = null;

            // The store drains its WAL, whose writes the disk completes, so the disk stays armed until it closes.
            await store.DisposeAsync();
        }

        await _disk.CancelAsync();
        _disk.Dispose();
        _lifetime.Dispose();
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    /// <summary>
    /// Damages a few bytes of page <paramref name="page"/> of WAL file <paramref name="file"/>, counting its header page.
    /// </summary>
    private void DamagePage(string path, ulong file, long page)
    {
        var bytes = new byte[8];
        _random.NextBytes(bytes);
        Storage.Damage(path, file, page * _options.PageSize + _random.Next(_options.PageSize - bytes.Length), bytes);
        _environment.Trace.Record(Invariant($"damage {Address}: WAL file {file} page {page}"));
        foreach (var observer in _damageObservers)
        {
            observer();
        }
    }

    private SimulatedWalStorage Attach(SimulatedWalStorage storage)
    {
        _disk = new CancellationTokenSource();
        var disk = _disk.Token;
        storage.HoldWrites = true;
        storage.WriteFault = (_, file, offset, _) =>
            Inject(StorageFault.Write) ?? CompleteLater(storage, (name, number, position) => name is null && number == file && position == offset, disk, wal: true);
        storage.NamedWriteFault = (_, file, offset, _) =>
            Inject(StorageFault.NamedWrite) ?? CompleteLater(storage, (name, _, position) => name == file && position == offset, disk, wal: false);
        storage.TornWriteFault = (_, _, _, _) => Inject(StorageFault.TornWrite);
        storage.ReadFault = (_, _, _, _) => Inject(StorageFault.Read);
        storage.NamedReadFault = (_, _, _, _) => Inject(StorageFault.NamedRead);
        storage.DeleteFault = (_, _) => Inject(StorageFault.Delete);
        storage.NamedDeleteFault = (_, _) => Inject(StorageFault.NamedDelete);
        storage.DirectoryFlushFault = _ => Inject(StorageFault.DirectoryFlush);
        return storage;
    }

    /// <summary>
    /// Fails the running process's storage operation of kind <paramref name="fault"/> when that failure is armed, and
    /// then crashes and restarts the node, whose disk the failure stops; otherwise lets the operation run.
    /// </summary>
    /// <remarks>
    /// A process which is starting runs its open's operations unfailed, so a restart always completes.
    /// </remarks>
    private IOException? Inject(StorageFault fault)
    {
        if (_armed != fault || _store is null || _starting)
        {
            return null;
        }

        _armed = null;
        FailStops++;
        var failure = new IOException(Invariant($"An injected {fault} failure on {Address}."));
        _injected.Add(failure);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FailStopped = restarted.Task;
        _environment.Trace.Record(Invariant($"fail a {fault} on {Address}"));
        _environment.Schedule(
            _environment.Cluster.SchedulerLane,
            TimeSpan.Zero,
            () => _ = FailStopAsync(restarted),
            _lifetime.Token,
            () => restarted.TrySetResult());
        return failure;
    }

    /// <summary>
    /// Returns whether this node injected the reported <paramref name="error"/>: the simulated storage stops at every
    /// failure it injects before it reports it, as the process stops (conventions.md §14). The driver calls it between
    /// steps, outside the simulation, so it reads only the test's own record, never the rewritten storage.
    /// </summary>
    private bool Injected(FatalError error) => _injected.Exists(failure => ReferenceEquals(failure, error.Exception));

    /// <summary>
    /// Crashes the process a local storage failure stopped, as a process or an operating system, and restarts it after a
    /// short downtime, as an orchestrator does.
    /// </summary>
    private async Task FailStopAsync(TaskCompletionSource restarted)
    {
        try
        {
            await CrashAsync(operatingSystem: _random.Chance(0.5));
            await Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(20)), SimulationEnvironment.Clock, _lifetime.Token);
            if (!_disposed)
            {
                // Once begun, the restart completes; disposal waits for it.
                await StartAsync(CancellationToken.None);
            }

            _ = restarted.TrySetResult();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The node is disposed during its downtime, so it stays down.
            _ = restarted.TrySetResult();
        }
#pragma warning disable CA1031 // The restart's failure is the scenario's, which awaits it, and the node's disposal's.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _ = restarted.TrySetException(exception);
        }
    }

    /// <summary>
    /// Completes the write being issued after a random latency on the node's lane, so writes complete out of issue order
    /// and not while the node is suspended, and fires a crash which waits for the next WAL write as the write leaves or
    /// once it completes. A named file which the storage publishes at once leaves no write in flight, and its completion
    /// completes nothing.
    /// </summary>
    private Exception? CompleteLater(SimulatedWalStorage storage, Func<string?, ulong, long, bool> write, CancellationToken disk, bool wal)
    {
        var crash = wal ? _walWriteCrash : null;
        if (crash is not null)
        {
            _walWriteCrash = null;
        }

        _environment.Schedule(
            Node.Context.SchedulerLane,
            _random.NextTimeSpan(MinDiskLatency, MaxDiskLatency),
            () =>
            {
                _ = storage.CompleteWrite(write);
                if (crash is { Durable: true } completed)
                {
                    completed.Crash();
                }
            },
            disk);
        if (crash is { Durable: false } issued)
        {
            issued.Crash();
        }

        return null;
    }

    private void Publish(LedgerStore store)
    {
        foreach (var observer in _observers)
        {
            observer(store);
        }
    }

    /// <summary>
    /// Holds a node root's cluster claim beside its simulated disk, published once and kept across crashes (LS-I12).
    /// </summary>
    private sealed class SimulatedNodeRootClaim : INodeRootClaim
    {
        private byte[]? _content;

        public ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_content?.ToArray());
        }

        public ValueTask<bool> CreateIfAbsentAsync(byte[] content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_content is not null)
            {
                return ValueTask.FromResult(false);
            }

            _content = [.. content];
            return ValueTask.FromResult(true);
        }
    }
}

/// <summary>
/// Reports that a simulated node's process is down, so it sends no answer: a caller observes only its deadline.
/// </summary>
public sealed class NodeDownException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NodeDownException"/> class.
    /// </summary>
    public NodeDownException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NodeDownException"/> class.
    /// </summary>
    /// <param name="address">The node's address.</param>
    public NodeDownException(string address)
        : base(string.Create(CultureInfo.InvariantCulture, $"{address} is down."))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NodeDownException"/> class.
    /// </summary>
    /// <param name="address">The node's address.</param>
    /// <param name="innerException">The failure the crash caused.</param>
    public NodeDownException(string address, Exception innerException)
        : base(string.Create(CultureInfo.InvariantCulture, $"{address} is down."), innerException)
    {
    }
}
