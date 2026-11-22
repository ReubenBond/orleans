using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// A silo's membership view a test sets: its snapshot, and the updates each <see cref="Set"/> publishes to every watcher.
/// A watcher which <see cref="FailNextWatch"/> marks ends with an exception, as a faulted membership stream does.
/// </summary>
internal sealed class FakeMembership : IClusterMembershipService
{
    private readonly Lock _lock = new();
    private readonly List<Channel<ClusterMembershipSnapshot>> _watchers = [];
    private long _version;
    private bool _failNext;

    public ClusterMembershipSnapshot CurrentSnapshot { get; private set; } = new([], new MembershipVersion(0));

    /// <summary>Gets the number of watches started.</summary>
    public int Watches { get; private set; }

    public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => WatchAsync();

    /// <summary>Replaces the members and publishes the new snapshot.</summary>
    public void Set(params (SiloAddress Silo, SiloStatus Status)[] members)
    {
        lock (_lock)
        {
            CurrentSnapshot = new(
                members.ToImmutableDictionary(static member => member.Silo, static member => new ClusterMember(member.Silo, member.Status, member.Silo.ToString())),
                new MembershipVersion(++_version));
            foreach (var watcher in _watchers)
            {
                _ = watcher.Writer.TryWrite(CurrentSnapshot);
            }
        }
    }

    /// <summary>Ends the current watches, and the next one, with an exception.</summary>
    public void FailNextWatch()
    {
        lock (_lock)
        {
            _failNext = true;
            foreach (var watcher in _watchers)
            {
                _ = watcher.Writer.TryComplete(new InvalidOperationException("The membership stream faulted."));
            }

            _watchers.Clear();
        }
    }

    public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);

    private async IAsyncEnumerable<ClusterMembershipSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ClusterMembershipSnapshot>();
        lock (_lock)
        {
            Watches++;
            if (_failNext)
            {
                _failNext = false;
                _ = channel.Writer.TryComplete(new InvalidOperationException("The membership stream faulted."));
            }
            else
            {
                _watchers.Add(channel);
                _ = channel.Writer.TryWrite(CurrentSnapshot);
            }
        }

        await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return snapshot;
        }
    }
}

/// <summary>A fake clock which counts the timers armed on it, so a test can wait until a waiter has armed its delay.</summary>
internal sealed class CountingTimeProvider : FakeTimeProvider
{
    private int _timers;

    /// <summary>Gets the number of timers created so far.</summary>
    public int Timers => Volatile.Read(ref _timers);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // Counted once armed, so a test which waits for the count and then advances the clock fires it.
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _ = Interlocked.Increment(ref _timers);
        return timer;
    }
}

/// <summary>A logger which records each entry's level, event ID, and message.</summary>
internal class RecordingLogger : ILogger
{
    private readonly Lock _lock = new();
    private readonly List<(LogLevel Level, int EventId, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries.Select(static entry => (entry.Level, entry.Message))];
            }
        }
    }

    /// <summary>Gets the event ID of each entry, in order.</summary>
    public IReadOnlyList<int> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries.Select(static entry => entry.EventId)];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        lock (_lock)
        {
            _entries.Add((logLevel, eventId.Id, formatter(state, exception)));
        }
    }
}

/// <summary>A <see cref="RecordingLogger"/> of a category, for the types which take their own logger.</summary>
/// <typeparam name="T">The category.</typeparam>
internal sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>;

/// <summary>A transport for a store which never reaches a replica, so no replica is ever opened.</summary>
internal sealed class NoReplicas : ILedgerReplicaTransport
{
    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
        throw new InvalidOperationException("The test's store reaches no replica.");

    public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The test's store reaches no replica.");
}

/// <summary>
/// An activation's runtime context, as a journal storage's creation sees it: its grain, its deactivation, which
/// <see cref="Deactivate()"/> completes, and the migrations asked of it, each of which deactivates it as a migration from
/// a valid activation does. Every other member is beyond these tests.
/// </summary>
internal sealed class FakeGrainContext(GrainId grain) : IGrainContext
{
    private readonly TaskCompletionSource _deactivated = new();
    private int _migrations;

    /// <summary>Gets how many migrations were asked of the activation.</summary>
    public int Migrations => Volatile.Read(ref _migrations);

    public GrainId GrainId => grain;

    public Task Deactivated => _deactivated.Task;

    public GrainReference GrainReference => throw new NotSupportedException();

    public object? GrainInstance => null;

    public ActivationId ActivationId => throw new NotSupportedException();

    public GrainAddress Address => throw new NotSupportedException();

    public IServiceProvider ActivationServices => throw new NotSupportedException();

    public IGrainLifecycle ObservableLifecycle => throw new NotSupportedException();

    public IWorkItemScheduler Scheduler => throw new NotSupportedException();

    /// <summary>Completes the activation's deactivation.</summary>
    public void Deactivate() => _deactivated.TrySetResult();

    public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _migrations);
        Deactivate();
    }

    public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public void ReceiveMessage(object message) => throw new NotSupportedException();

    public void Rehydrate(IRehydrationContext context) => throw new NotSupportedException();

    public object? GetTarget() => throw new NotSupportedException();

    public object? GetComponent(Type componentType) => throw new NotSupportedException();

    public void SetComponent<TComponent>(TComponent? value)
        where TComponent : class => throw new NotSupportedException();

    public bool Equals(IGrainContext? other) => ReferenceEquals(this, other);
}
