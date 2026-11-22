using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Runtime;

namespace Orleans.Durable.Storage;

/// <summary>
/// Resolves node identities to the silos serving them (orleans.md §5.3, R1): it caches the <c>node-registry</c> register
/// and re-reads it on each membership change and when a mapping is shown stale, by a placement refusal against membership
/// or a <c>NotThisNode</c> answer.
/// </summary>
/// <remarks>
/// <para>
/// Re-reads are single-flight: requests made before a re-read is dispatched share it, and one made while it runs leads to
/// one more. Requested re-reads are dispatched at most once per <see cref="RereadInterval"/>; one the rate defers runs when
/// the interval ends rather than being dropped, and a failed re-read is retried at the same rate until one succeeds. While
/// the cache is stale against membership, an active silo having no entry, the resolver re-reads on a jittered backoff from
/// <see cref="FirstBackoff"/> up to <see cref="RereadInterval"/>, so a new registration is seen quickly. A re-read a
/// membership change requests waits up to <see cref="MembershipJitter"/>, so every silo's re-reads spread out.
/// </para>
/// <para>
/// Resolutions answer from the cached value until a re-read replaces it, and the value only advances: the register is
/// linearizable and the re-reads are sequential. A retired node resolves to the silo its entry names, so it serves its
/// replicas until its retirement gate opens (orleans.md §5.8), across restarts of its silo, which register it retired; it
/// is never active again.
/// </para>
/// </remarks>
internal sealed partial class NodeRegistryResolver : ILifecycleParticipant<ISiloLifecycle>, IAsyncDisposable
{
    /// <summary>The shortest interval between the dispatches of two requested reads of the register.</summary>
    public static readonly TimeSpan RereadInterval = TimeSpan.FromSeconds(1);

    /// <summary>The first interval of the backoff on which a cache stale against membership is re-read.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest wait a membership change adds before its re-read is dispatched.</summary>
    public static readonly TimeSpan MembershipJitter = TimeSpan.FromMilliseconds(250);

    private readonly Func<CancellationToken, ValueTask<IConditionalRegister>> _registerFactory;
    private readonly IClusterMembershipService _membership;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposing = new();
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource<NodeRegistryValue> _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NodeRegistryValue? _current;
    private bool _pending;
    private bool _jitter;
    private bool _started;
    private bool _disposed;
    private Task? _running;
    private Task? _watch;

    // Read and written only by the re-read loop.
    private IConditionalRegister? _register;
    private MetaStoreVersion? _currentVersion;
    private long? _lastDispatch;
    private TimeSpan _backoff = FirstBackoff;

    /// <summary>Initializes a new instance of the <see cref="NodeRegistryResolver"/> class.</summary>
    /// <param name="metaStore">The MetaStore holding the cluster's registers.</param>
    /// <param name="options">The node's options, which name the cluster.</param>
    /// <param name="membership">The silo's membership view.</param>
    /// <param name="timeProvider">The clock which paces re-reads.</param>
    /// <param name="logger">The logger for failed re-reads.</param>
    public NodeRegistryResolver(IMetaStore metaStore, DurableStorageOptions options, IClusterMembershipService membership, TimeProvider timeProvider, ILogger<NodeRegistryResolver> logger)
        : this(cancellationToken => metaStore.GetRegisterAsync(options.Cluster, NodeRegistryValue.RegisterName, cancellationToken), membership, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(metaStore);
        ArgumentNullException.ThrowIfNull(options);
    }

    /// <summary>Initializes a new instance of the <see cref="NodeRegistryResolver"/> class over a register.</summary>
    internal NodeRegistryResolver(Func<CancellationToken, ValueTask<IConditionalRegister>> register, IClusterMembershipService membership, TimeProvider timeProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _registerFactory = register;
        _membership = membership;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Gets the newest registry value the resolver read or was given (<see cref="Advance"/>), or <see langword="null"/>
    /// before either.
    /// </summary>
    public NodeRegistryValue? Current => Volatile.Read(ref _current);

    /// <inheritdoc/>
    public void Participate(ISiloLifecycle observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _ = observer.Subscribe(
            nameof(NodeRegistryResolver),
            ServiceLifecycleStage.RuntimeInitialize,
            _ =>
            {
                Start();
                return Task.CompletedTask;
            },
            _ => DisposeAsync().AsTask());
    }

    /// <summary>Starts the first read of the register and the re-reads on membership changes.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            using (ExecutionContext.SuppressFlow())
            {
                _watch = Task.Run(WatchMembershipAsync, CancellationToken.None);
            }
        }

        RequestReread();
    }

    /// <summary>Returns the registry value last read, reading the register first when no read has succeeded yet.</summary>
    /// <param name="cancellationToken">Ends the caller's wait only.</param>
    public Task<NodeRegistryValue> GetAsync(CancellationToken cancellationToken = default)
    {
        if (Current is { } current)
        {
            return Task.FromResult(current);
        }

        RequestReread();
        return _firstRead.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Returns the address of the silo the cached registry names for <paramref name="node"/>, active or retired, or
    /// <see langword="null"/> when it names none.
    /// </summary>
    public SiloAddress? Resolve(NodeId node) => Current?.Find(node)?.Silo;

    /// <summary>Returns the node the cached registry maps <paramref name="silo"/> to, or <see langword="null"/>.</summary>
    public NodeId? NodeOf(SiloAddress silo) => Current?.FindBySilo(silo)?.Node;

    /// <summary>
    /// Schedules a re-read of the register, sharing one not yet dispatched, no sooner than <see cref="RereadInterval"/>
    /// after the last dispatch. Resolutions answer from the cached value until the re-read replaces it.
    /// </summary>
    public void RequestReread() => Request(jitter: false);

    /// <summary>
    /// Schedules a re-read, as <see cref="RequestReread"/> does, only when the cached value is stale against membership:
    /// it is absent, or an active silo has no entry, which a pending registration or a successor's would give it. Otherwise
    /// a re-read could not change a refusal, and the next membership change requests one.
    /// </summary>
    public void RequestRereadIfStale()
    {
        if (IsStale(Current))
        {
            RequestReread();
        }
    }

    /// <summary>Stops re-reading and waits for the read in flight and the membership watch to end.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? running;
        Task? watch;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            running = _running;
            watch = _watch;
        }

        await _disposing.CancelAsync().ConfigureAwait(false);
        foreach (var task in new[] { running, watch })
        {
            if (task is not null)
            {
                await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        _ = _firstRead.TrySetException(new ObjectDisposedException(nameof(NodeRegistryResolver)));
        _disposing.Dispose();
    }

    /// <summary>Returns whether <paramref name="registry"/> is absent, or names no entry for some active silo.</summary>
    internal bool IsStale(NodeRegistryValue? registry)
    {
        if (registry is null)
        {
            return true;
        }

        foreach (var member in _membership.CurrentSnapshot.Members.Values)
        {
            if (member.Status == SiloStatus.Active && registry.FindBySilo(member.SiloAddress) is null)
            {
                return true;
            }
        }

        return false;
    }

    private void Request(bool jitter)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _pending = true;
            _jitter |= jitter;
            if (_running is null)
            {
                // The re-reads serve every caller, so they run in none's execution context (conventions.md §12).
                using (ExecutionContext.SuppressFlow())
                {
                    _running = Task.Run(RereadAsync, CancellationToken.None);
                }
            }
        }
    }

    /// <summary>Reads the register while a re-read is pending or the cache is stale, at the rates the class states.</summary>
    private async Task RereadAsync()
    {
        var stopping = _disposing.Token;
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                var stale = IsStale(Current);
                if (_disposed || !_pending && !stale)
                {
                    _running = null;
                    return;
                }

                var interval = stale ? _backoff * (0.5 + Random.Shared.NextDouble() / 2) : RereadInterval;
                wait = _lastDispatch is { } last ? interval - _timeProvider.GetElapsedTime(last) : TimeSpan.Zero;
                if (_jitter)
                {
                    wait = TimeSpan.FromTicks(Math.Max(wait.Ticks, (long)(MembershipJitter.Ticks * Random.Shared.NextDouble())));
                }
            }

            try
            {
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _timeProvider, stopping).ConfigureAwait(false);
                }

                lock (_lock)
                {
                    // Requests from here on need a read dispatched after them.
                    _pending = false;
                    _jitter = false;
                }

                _lastDispatch = _timeProvider.GetTimestamp();
                await ReadAsync(stopping).ConfigureAwait(false);
                _backoff = IsStale(Current) ? TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, RereadInterval.Ticks)) : FirstBackoff;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // The loop's head ends it.
            }
            catch (Exception failure)
            {
                LogRereadFailed(_logger, failure);
                lock (_lock)
                {
                    _pending = true;
                }
            }
        }
    }

    /// <summary>Reads the register once, decoding its value unless its version is the cached value's.</summary>
    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        _register ??= await _registerFactory(cancellationToken).ConfigureAwait(false);
        using var entry = await _register.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (entry is not null && entry.Version == _currentVersion && Current is not null)
        {
            return;
        }

        var value = entry is null
            ? NodeRegistryValue.Empty
            : NodeRegistryCodec.Decode(entry.Value.AsReadOnlySequence()) switch
            {
                NodeRegistryValue decoded => decoded,
                InvalidDataException rejection => throw rejection,
                null => throw new UnreachableException(),
            };
        Advance(value);
        if (Current?.Revision == value.Revision)
        {
            _currentVersion = entry?.Version;
        }
    }

    /// <summary>
    /// Raises the cached value to <paramref name="value"/>, a value of the register read or written by a write which
    /// landed, when it is newer: the controller grain gives its silo's resolver each value its registry committer reads or
    /// writes, so the nodes its controller observes resolve on that silo too (orleans.md §5.3). The cached value only
    /// advances, by revision, which every write increments (OR-I2).
    /// </summary>
    public void Advance(NodeRegistryValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var current = Volatile.Read(ref _current);
        while (current is null || value.Revision > current.Revision)
        {
            var seen = Interlocked.CompareExchange(ref _current, value, current);
            if (ReferenceEquals(seen, current))
            {
                break;
            }

            current = seen;
        }

        _ = _firstRead.TrySetResult(Current!);
    }

    /// <summary>Requests a re-read on each membership change until the resolver stops, restarting a watch which fails.</summary>
    private async Task WatchMembershipAsync()
    {
        var stopping = _disposing.Token;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await foreach (var snapshot in _membership.MembershipUpdates.WithCancellation(stopping).ConfigureAwait(false))
                {
                    Request(jitter: true);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure)
            {
                LogWatchFailed(_logger, failure);
            }

            try
            {
                await Task.Delay(RereadInterval, _timeProvider, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the node registry failed; the read is retried.")]
    private static partial void LogRereadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watching membership for node registry re-reads failed; the watch restarts.")]
    private static partial void LogWatchFailed(ILogger logger, Exception exception);
}
