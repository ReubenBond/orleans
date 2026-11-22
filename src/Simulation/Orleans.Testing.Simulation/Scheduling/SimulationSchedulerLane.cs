using System.Diagnostics;
using System.Globalization;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation;

/// <summary>
/// A node-scoped facade for scheduling callbacks on a <see cref="SimulationScheduler"/>.
/// The scheduler owns execution ordering, virtual time, and timers; a lane contributes only
/// node identity, synchronization-context installation, and pending-item diagnostics.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
[DebuggerTypeProxy(typeof(SimulationSchedulerLaneDebugView))]
public sealed class SimulationSchedulerLane
{
    private readonly SortedSet<ScheduledItem> _items = new(ScheduledItem.Comparer);
    private readonly SimulationScheduler _scheduler;
    private readonly SingleThreadedGuard _guard;
    private readonly SimulationNodeIdentity? _node;
    private readonly SimulationSynchronizationContext _synchronizationContext;
    private long _sequenceNumber;
    private bool _enabled = true;
    private bool _pendingWorkDiscarded;
    private volatile bool _detached;

    /// <summary>Initializes a lane owned by <paramref name="scheduler"/>.</summary>
    public SimulationSchedulerLane(
        SimulationScheduler scheduler,
        SingleThreadedGuard guard,
        SimulationNodeIdentity? node = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(guard);
        _scheduler = scheduler;
        _guard = guard;
        _node = node;
        _synchronizationContext = new SimulationSynchronizationContext(this);
    }

    /// <summary>Gets the scheduler-backed current simulated date/time.</summary>
    public DateTimeOffset UtcNow
    {
        get
        {
            ThrowIfDetachedWithoutGuard();
            return _scheduler.UtcNow;
        }
    }

    /// <summary>Gets the synchronization context which posts callbacks to this lane.</summary>
    public SimulationSynchronizationContext SynchronizationContext
    {
        get
        {
            ThrowIfDetachedWithoutGuard();
            return _synchronizationContext;
        }
    }

    internal SimulationScheduler Scheduler => _scheduler;

    internal SimulationNodeIdentity? Node => _node;

    internal bool IsDetached => _detached;

    internal bool HasPendingWork =>
        _pendingWorkDiscarded
            ? false
            : _node is { } node ? _scheduler.HasPendingWork(node) : HasItems;

    /// <summary>Gets whether this lane has pending items.</summary>
    public bool HasItems
    {
        get
        {
            using var _ = _guard.Enter();
            ThrowIfDetachedUnderGuard();
            if (_pendingWorkDiscarded)
            {
                return false;
            }

            return _items.Count > 0;
        }
    }

    /// <summary>Gets the earliest future due time, if any.</summary>
    public DateTimeOffset? NextWaitingDueTime
    {
        get
        {
            using var _ = _guard.Enter();
            ThrowIfDetachedUnderGuard();
            if (_pendingWorkDiscarded)
            {
                return null;
            }

            var now = _scheduler.UtcNow;
            foreach (var item in _items)
            {
                if (item.DueTime > now)
                {
                    return item.DueTime;
                }
            }

            return null;
        }
    }

    /// <summary>Schedules an action immediately.</summary>
    public void Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Schedule(new ScheduledActionItem(action), TimeSpan.Zero);
    }

    /// <summary>Schedules an action after a modeled delay.</summary>
    public void EnqueueAfter(Action action, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = EnqueueAfter(new ScheduledActionItem(action), delay);
    }

    internal void Enqueue(ScheduledItem item) => Schedule(item, TimeSpan.Zero);

    internal TItem EnqueueAfter<TItem>(TItem item, TimeSpan delay)
        where TItem : ScheduledItem
    {
        Schedule(item, delay);
        return item;
    }

    private void Schedule(ScheduledItem item, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        using var _ = _guard.Enter();
        ThrowIfDetachedUnderGuard();
        ThrowIfPendingWorkDiscardedUnderGuard();
        var dueTime = _scheduler.UtcNow + delay;
        item.OnScheduled(this, dueTime, _sequenceNumber++);
        _items.Add(item);

        IDisposable registration;
        try
        {
            registration = _scheduler.ScheduleAfter(
                "Simulation lane callback",
                () => Execute(item),
                delay,
                _node);
        }
        catch
        {
            _items.Remove(item);
            item.OnRemoved();
            throw;
        }

        item.SetCancellation(registration);
    }

    /// <summary>
    /// Captures an immutable diagnostic snapshot of pending work, ordered by due time and
    /// registration sequence.
    /// </summary>
    public IReadOnlyList<SimulationScheduledItemDiagnostic> CaptureScheduledItems()
    {
        using var _ = _guard.Enter();
        ThrowIfDetachedUnderGuard();
        if (_pendingWorkDiscarded)
        {
            return [];
        }

        var now = _scheduler.UtcNow;
        var queueIdentity = _node?.Address ?? "cluster";
        var result = new SimulationScheduledItemDiagnostic[_items.Count];
        var index = 0;
        foreach (var item in _items)
        {
            var isReady = item.DueTime <= now;
            result[index++] = new SimulationScheduledItemDiagnostic(
                queueIdentity,
                item.Kind,
                item.Description,
                item.DueTime,
                item.SequenceNumber,
                isReady,
                isReady && !_enabled);
        }

        return Array.AsReadOnly(result);
    }

    internal ScheduledItem[] CaptureItems()
    {
        using var _ = _guard.Enter();
        ThrowIfDetachedUnderGuard();
        return [.. _items];
    }

    internal void RemoveItem(ScheduledItem item)
    {
        using var _ = _guard.Enter();
        if (_items.Remove(item))
        {
            item.CancelRegistration();
        }
    }

    private void Execute(ScheduledItem item)
    {
        using var _ = _guard.Enter();
        if (!_items.Remove(item))
        {
            return;
        }

        item.OnInvoking();
        using var syncScope = _synchronizationContext.Install();
        try
        {
            item.Invoke();
        }
        catch (Exception exception)
        {
            _scheduler.ReportUnhandledCallbackException(exception);
            throw;
        }
    }

    /// <summary>Runs one scheduler operation without advancing virtual time.</summary>
    /// <param name="cancellationToken">A token checked before dispatching the operation.</param>
    public bool RunOnce(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        using var control = _scheduler.EnterControlScope();
        return _scheduler.RunStep(cancellationToken);
    }

    /// <summary>Runs scheduler operations until none is currently eligible.</summary>
    /// <param name="cancellationToken">A token that can cancel the run between scheduler dispatches.</param>
    public int RunUntilIdle(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        using var control = _scheduler.EnterControlScope();
        return _scheduler.RunUntilIdle(cancellationToken);
    }

    internal void SetEnabled(bool enabled)
    {
        using var _ = _guard.Enter();
        ThrowIfDetachedUnderGuard();
        ThrowIfPendingWorkDiscardedUnderGuard();
        _enabled = enabled;
        if (_node is { } node)
        {
            _scheduler.SetNodeEnabled(node, enabled);
        }
    }

    internal ScheduledItem[] Detach()
    {
        using var _ = _guard.Enter();
        if (_detached)
        {
            return [];
        }

        _detached = true;
        if (!_pendingWorkDiscarded && _node is { } node)
        {
            _scheduler.RemovePendingWork(node);
        }

        return [.. _items];
    }

    internal ScheduledItem[] MarkPendingWorkDiscarded()
    {
        using var _ = _guard.Enter();
        ThrowIfDetachedUnderGuard();
        if (_pendingWorkDiscarded)
        {
            return [];
        }

        _pendingWorkDiscarded = true;
        _enabled = false;
        return [.. _items];
    }

    private void ThrowIfDetached()
    {
        using var _ = _guard.Enter();
        ThrowIfDetachedUnderGuard();
    }

    private void ThrowIfUnavailable()
    {
        using var _ = _guard.Enter();
        ThrowIfDetachedUnderGuard();
        ThrowIfPendingWorkDiscardedUnderGuard();
    }

    private void ThrowIfDetachedWithoutGuard() =>
        ObjectDisposedException.ThrowIf(_detached, this);

    private void ThrowIfDetachedUnderGuard() =>
        ObjectDisposedException.ThrowIf(_detached, this);

    private void ThrowIfPendingWorkDiscardedUnderGuard()
    {
        if (_pendingWorkDiscarded)
        {
            throw new ObjectDisposedException(
                nameof(SimulationSchedulerLane),
                "The owning simulation node discarded its pending work and cannot accept new work.");
        }
    }

    private string DebuggerDisplay
    {
        get
        {
            IReadOnlyList<SimulationScheduledItemDiagnostic> items = CaptureScheduledItems();
            return string.Create(CultureInfo.InvariantCulture, $"Count={items.Count} UtcNow={_scheduler.UtcNow:HH:mm:ss.fff}");
        }
    }
}

internal sealed class SimulationSchedulerLaneDebugView(SimulationSchedulerLane lane)
{
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public SimulationScheduledItemDiagnostic[] Items => [.. lane.CaptureScheduledItems()];
}
