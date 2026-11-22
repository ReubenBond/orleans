using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orleans.Testing.Simulation;

/// <summary>
/// Represents the execution state of a simulated node.
/// </summary>
public enum SimulationNodeState
{
    /// <summary>
    /// The node is running and will execute tasks during simulation stepping.
    /// </summary>
    Running,

    /// <summary>
    /// The node is suspended and will not execute tasks.
    /// Messages sent to the node will be queued but not processed until resumed.
    /// Timers will accumulate and fire when the node is resumed if their due time has passed.
    /// </summary>
    Suspended,

    /// <summary>
    /// The node has explicitly discarded all pending work and cannot accept new work.
    /// </summary>
    Discarded,
}

internal enum SimulationNodeAttachmentState
{
    Attaching,
    Attached,
    Disposing,
    Detached,
}

/// <summary>
/// <para>
/// Encapsulates all per-node simulation state, including the node's scheduler lane,
/// task scheduler, synchronization context, time provider, and random number generator.
/// </para>
/// <para>
/// Each simulated node has its own context, allowing fine-grained control
/// over individual node execution (pause, resume, step) while sharing a unified
/// <see cref="SimulationSchedulerLane"/> for time synchronization.
/// </para>
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed partial class SimulationNodeContext
{
    private readonly SingleThreadedGuard _guard;
    private readonly SimulationSchedulerLane? _externalSchedulerLane;
    private readonly List<ScheduledItem> _sharedLaneItems = [];
    private readonly ILogger _logger;
    private SimulationNodeAttachmentState _attachmentState = SimulationNodeAttachmentState.Attaching;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationNodeContext"/> class.
    /// Creates a new simulation node context using the specified scheduler and random generator.
    /// </summary>
    /// <param name="scheduler">The shared simulation scheduler for time and work coordination.</param>
    /// <param name="guard">The shared single-threaded guard for detecting concurrent access.</param>
    /// <param name="random">The deterministic random number generator for this node.</param>
    /// <param name="externalSchedulerLane">Optional external scheduler lane for operations that must run
    /// even when this node is suspended (e.g., auto-resume from SuspendFor). If not provided,
    /// SuspendFor will throw InvalidOperationException.</param>
    /// <param name="logger">Optional logger for suspend/resume operations.</param>
    /// <param name="node">The node identity attached to work scheduled by this context.</param>
    internal SimulationNodeContext(
        SimulationScheduler scheduler,
        SingleThreadedGuard guard,
        SimulationRandom random,
        SimulationSchedulerLane? externalSchedulerLane,
        ILogger? logger,
        SimulationNodeIdentity? node)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(random);

        Scheduler = scheduler;
        Random = random;
        _guard = guard;
        _externalSchedulerLane = externalSchedulerLane;
        _logger = logger ?? NullLogger.Instance;

        SchedulerLane = new SimulationSchedulerLane(scheduler, guard, node);
        TaskScheduler = new SimulationTaskScheduler(SchedulerLane);
        TimeProvider = new SimulationTimeProvider(SchedulerLane);
    }

    /// <summary>
    /// Gets the shared scheduler which owns this node's work and virtual time.
    /// </summary>
    public SimulationScheduler Scheduler { get; }

    /// <summary>
    /// Gets the deterministic random number generator for this node.
    /// </summary>
    public SimulationRandom Random { get; }

    /// <summary>
    /// Gets the scheduler lane for this node.
    /// Work scheduled on this lane is eligible only while this node is active.
    /// </summary>
    public SimulationSchedulerLane SchedulerLane { get; }

    /// <summary>
    /// Gets the task scheduler for this node.
    /// Used for scheduling TPL tasks on this node's lane.
    /// </summary>
    public SimulationTaskScheduler TaskScheduler { get; }

    /// <summary>
    /// Gets the scheduler lane which owns this node's queued work.
    /// </summary>
    public SimulationSchedulerLane TaskQueue => SchedulerLane;

    /// <summary>
    /// Gets the synchronization context for this node.
    /// Used for async/await continuations on this node's lane.
    /// </summary>
    public SimulationSynchronizationContext SynchronizationContext => SchedulerLane.SynchronizationContext;

    /// <summary>
    /// Gets the time provider for this node.
    /// Timers created through this provider are scheduled on this node's lane.
    /// </summary>
    public SimulationTimeProvider TimeProvider { get; }

    /// <summary>
    /// Gets the current execution state of this node.
    /// </summary>
    public SimulationNodeState State { get; private set; } = SimulationNodeState.Running;

    /// <summary>
    /// Gets a value indicating whether gets whether this node is currently suspended.
    /// </summary>
    public bool IsSuspended => State == SimulationNodeState.Suspended;

    /// <summary>
    /// Gets a value indicating whether gets whether this node has any tasks ready to execute at the current time.
    /// </summary>
    public bool HasReadyTasks
    {
        get
        {
            if (State != SimulationNodeState.Running)
                return false;

            // Check if the lane has any items due at or before the current time
            var items = SchedulerLane.CaptureScheduledItems();
            if (items.Count == 0)
                return false;

            return items[0].DueTime <= Scheduler.UtcNow;
        }
    }

    /// <summary>
    /// Gets the due time of the next waiting (not yet ready) task on this node's lane,
    /// or null if no tasks are waiting.
    /// </summary>
    public DateTimeOffset? NextWaitingDueTime =>
        State == SimulationNodeState.Discarded ? null : SchedulerLane.NextWaitingDueTime;

    /// <summary>
    /// Executes one ready task from this node's lane.
    /// </summary>
    /// <returns>True if a task was executed; false if no tasks are ready or the node is suspended.</returns>
    /// <summary>Executes one ready task from this node's lane.</summary>
    /// <param name="cancellationToken">A token checked before dispatching the task.</param>
    /// <returns>True if a task was executed; false if no tasks are ready or the node is suspended.</returns>
    public bool Step(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfPendingWorkDiscarded();
        if (State == SimulationNodeState.Suspended)
            return false;

        return SchedulerLane.RunOnce(cancellationToken);
    }

    /// <summary>
    /// Executes all ready tasks from this node's lane.
    /// </summary>
    /// <returns>The number of tasks executed. Returns 0 if the node is suspended.</returns>
    /// <summary>Executes ready tasks from this node's lane until idle or cancellation is requested.</summary>
    /// <param name="cancellationToken">A token that can cancel the run between task dispatches.</param>
    /// <returns>The number of tasks executed. Returns 0 if the node is suspended.</returns>
    public int RunUntilIdle(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfPendingWorkDiscarded();
        if (State == SimulationNodeState.Suspended)
            return 0;

        return SchedulerLane.RunUntilIdle(cancellationToken);
    }

    /// <summary>
    /// Suspends this node, preventing it from executing tasks.
    /// Messages sent to the node will be queued but not processed until resumed.
    /// </summary>
    public void Suspend()
    {
        using var _ = _guard.Enter();
        ThrowIfUnavailable();
        State = SimulationNodeState.Suspended;
        if (_attachmentState != SimulationNodeAttachmentState.Disposing)
        {
            SchedulerLane.SetEnabled(enabled: false);
        }

        Log.NodeSuspended(_logger);
    }

    /// <summary>
    /// Resumes this node, allowing it to execute tasks again.
    /// Any tasks that became ready while suspended will be executed on subsequent steps.
    /// </summary>
    public void Resume()
    {
        using var _ = _guard.Enter();
        ThrowIfUnavailable();
        State = SimulationNodeState.Running;
        if (_attachmentState != SimulationNodeAttachmentState.Disposing)
        {
            SchedulerLane.SetEnabled(enabled: true);
        }

        Log.NodeResumed(_logger);
    }

    /// <summary>
    /// Suspends this node for the specified duration, then automatically resumes it.
    /// The resume occurs when simulated time advances past the duration.
    /// Requires an external scheduler lane to be provided at construction time.
    /// </summary>
    /// <param name="duration">How long to suspend the node (in simulated time).</param>
    /// <exception cref="InvalidOperationException">Thrown if no external scheduler lane was provided.</exception>
    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The scheduled item transfers to the scheduler lane and shared-item registry; the failure path disposes it.")]
    public void SuspendFor(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        using var _ = _guard.Enter();
        ThrowIfUnavailable();
        if (_externalSchedulerLane is null)
        {
            throw new InvalidOperationException(
                "SuspendFor requires an external scheduler lane to be provided at construction time.");
        }

        SimulationNodeState priorState = State;
        ScheduledActionItem? resume = null;
        resume = new ScheduledActionItem(() =>
        {
            RemoveSharedLaneItem(resume);
            Resume();
        });
        try
        {
            // Schedule before committing the suspended state so delay overflow, a detached lane, or
            // a scheduler failure leaves this context unchanged.
            _externalSchedulerLane.EnqueueAfter(resume, duration);
            AddSharedLaneItem(resume);
            Suspend();
            Log.NodeSuspendedFor(_logger, duration);
        }
        catch
        {
            RemoveSharedLaneItem(resume);
            resume.Dispose();
            State = priorState;
            if (_attachmentState != SimulationNodeAttachmentState.Disposing)
            {
                SchedulerLane.SetEnabled(priorState == SimulationNodeState.Running);
            }

            throw;
        }
    }

    /// <summary>
    /// Permanently discards every pending unit of work owned by this node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this only after the simulated member has stopped. Ready and delayed scheduler-lane
    /// callbacks, timers and virtual deadlines, controlled operations and resource waits, and
    /// cluster-lane callbacks owned by this context (such as a timed-suspension resume) are canceled
    /// without invoking their callbacks. Work owned by other nodes or by the cluster is unchanged.
    /// </para>
    /// <para>
    /// The transition is permanent and idempotent while the context remains attached. The context
    /// enters <see cref="SimulationNodeState.Discarded"/>, and later attempts to enqueue node-owned
    /// work throw <see cref="ObjectDisposedException"/>. Calling this from work currently executing
    /// for this node throws <see cref="InvalidOperationException"/> without discarding anything.
    /// After the containing cluster has detached the context, this method throws
    /// <see cref="ObjectDisposedException"/>.
    /// </para>
    /// </remarks>
    public void DiscardPendingWork()
    {
        using IDisposable? control = Scheduler.CurrentOperation is null
            ? Scheduler.EnterControlScope()
            : null;
        ScheduledItem[] laneItems;
        ScheduledItem[] sharedLaneItems;
        Exception? schedulerFailure = null;
        using (var _ = _guard.Enter())
        {
            ThrowIfDetached();
            SimulationNodeIdentity node = SchedulerLane.Node
                ?? throw new InvalidOperationException("The scheduler lane is not node-scoped.");
            if (State == SimulationNodeState.Discarded)
            {
                laneItems = SchedulerLane.CaptureItems();
                sharedLaneItems = [];
            }
            else
            {
                Scheduler.EnsureNodeCanDiscardPendingWork(node);
                State = SimulationNodeState.Discarded;
                laneItems = SchedulerLane.MarkPendingWorkDiscarded();
                sharedLaneItems = [.. _sharedLaneItems];
                _sharedLaneItems.Clear();
            }

            try
            {
                Scheduler.DiscardPendingWork(node);
            }
            catch (Exception exception)
            {
                schedulerFailure = exception;
            }
        }

        try
        {
            CancelScheduledItems(laneItems, sharedLaneItems);
        }
        catch (Exception cancellationFailure) when (schedulerFailure is not null)
        {
            throw new AggregateException(
                "Simulation node pending-work discard failed during scheduler and lane cleanup.",
                schedulerFailure,
                cancellationFailure).Flatten();
        }

        if (schedulerFailure is not null)
        {
            ExceptionDispatchInfo.Capture(schedulerFailure).Throw();
        }
    }

    internal bool IsAttachmentInProgress
    {
        get
        {
            using var _ = _guard.Enter();
            return _attachmentState == SimulationNodeAttachmentState.Attaching;
        }
    }

    internal bool HasPendingAttachmentWork
    {
        get
        {
            using var _ = _guard.Enter();
            if (State == SimulationNodeState.Discarded)
            {
                return false;
            }

            return SchedulerLane.HasPendingWork || _sharedLaneItems.Count > 0;
        }
    }

    internal void CompleteAttachment()
    {
        using var _ = _guard.Enter();
        if (_attachmentState != SimulationNodeAttachmentState.Attaching)
        {
            throw new InvalidOperationException(
                $"Cannot attach a node context while it is {_attachmentState}.");
        }

        _attachmentState = SimulationNodeAttachmentState.Attached;
        SchedulerLane.SetEnabled(State == SimulationNodeState.Running);
    }

    internal void BeginAttachmentCleanup()
    {
        using var _ = _guard.Enter();
        if (_attachmentState == SimulationNodeAttachmentState.Disposing
            || _attachmentState == SimulationNodeAttachmentState.Detached)
        {
            return;
        }

        if (_attachmentState != SimulationNodeAttachmentState.Attaching
            && _attachmentState != SimulationNodeAttachmentState.Attached)
        {
            throw new InvalidOperationException(
                $"Cannot begin node attachment cleanup while the context is {_attachmentState}.");
        }

        _attachmentState = SimulationNodeAttachmentState.Disposing;
        if (State != SimulationNodeState.Discarded)
        {
            State = SimulationNodeState.Running;
            SchedulerLane.SetEnabled(enabled: true);
        }
    }

    internal void CompleteAttachmentCleanup()
    {
        ScheduledItem[] laneItems;
        ScheduledItem[] sharedLaneItems;
        using (var _ = _guard.Enter())
        {
            if (_attachmentState == SimulationNodeAttachmentState.Detached)
            {
                return;
            }

            _attachmentState = SimulationNodeAttachmentState.Detached;
            laneItems = SchedulerLane.Detach();
            sharedLaneItems = [.. _sharedLaneItems];
            _sharedLaneItems.Clear();
            if (State != SimulationNodeState.Discarded)
            {
                State = SimulationNodeState.Running;
            }
        }

        CancelScheduledItems(laneItems, sharedLaneItems);
    }

    private void AddSharedLaneItem(ScheduledItem item)
    {
        using var _ = _guard.Enter();
        _sharedLaneItems.Add(item);
    }

    private void RemoveSharedLaneItem(ScheduledItem? item)
    {
        if (item is null)
        {
            return;
        }

        using var _ = _guard.Enter();
        _sharedLaneItems.Remove(item);
    }

    private static void CancelScheduledItems(params IEnumerable<ScheduledItem>[] itemGroups)
    {
        List<Exception>? failures = null;
        foreach (IEnumerable<ScheduledItem> items in itemGroups)
        {
            foreach (ScheduledItem item in items)
            {
                try
                {
                    item.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception exception)
                {
                    failures ??= [];
                    failures.Add(exception);
                }
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "One or more scheduled items failed to cancel during node attachment cleanup.",
                failures);
        }
    }

    private void ThrowIfDetached() =>
        ObjectDisposedException.ThrowIf(
            _attachmentState == SimulationNodeAttachmentState.Detached,
            this);

    private void ThrowIfPendingWorkDiscarded() =>
        ObjectDisposedException.ThrowIf(
            State == SimulationNodeState.Discarded,
            this);

    private void ThrowIfUnavailable()
    {
        ThrowIfDetached();
        ThrowIfPendingWorkDiscarded();
    }

    private string DebuggerDisplay =>
        $"SimulationNodeContext({State}, Tasks={SchedulerLane.CaptureScheduledItems().Count})";

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Node suspended")]
        public static partial void NodeSuspended(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Node resumed")]
        public static partial void NodeResumed(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Node suspended for {Duration}")]
        public static partial void NodeSuspendedFor(ILogger logger, TimeSpan duration);
    }
}
