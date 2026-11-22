using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;
using Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Scheduling;

/// <summary>
/// <para>
/// The controlled-operation kernel: the single authority that registers
/// <see cref="SimulationOperation"/>s, chooses exactly one runnable operation at a time, grants and
/// revokes the permission baton, drives every legal state transition, and performs terminal
/// cleanup. Controlled <c>Monitor</c>, semaphore, wait-handle, and synchronous <see cref="Task"/>
/// waits build on this scheduling layer. It owns the
/// reusable controlled-resource model, atomic resource waits with virtual-time timeouts, and the
/// modeled clock those timeouts fire against; cancellation-token races and deadlock detection are
/// layered on in later resource/wait scheduler increments.
/// </para>
/// <para>
/// <b>Physical-thread gating.</b> Each operation is attached to one physical worker thread while its
/// stack is live. A paused operation retains that worker and stack until it resumes; after a terminal
/// operation unwinds, its worker may carry a later operation. The scheduler grants a single
/// permission baton to at most one operation at a time and blocks the controlling thread until that
/// operation hands control back (by pausing, yielding, completing, or faulting). The handoff uses
/// counting wait handles - never busy-spinning - so at most one operation ever executes
/// system-under-test code, no matter how many physical threads exist. No operation is ever aborted
/// with an unsafe <c>Thread.Abort</c>; teardown unwinds parked threads cooperatively.
/// </para>
/// <para>
/// <b>Threading contract.</b> The "controlling thread" is whichever thread calls
/// <see cref="RunStep(CancellationToken)"/>/<see cref="Drain(CancellationToken)"/>; only one thread may drive the scheduler at a time.
/// <see cref="Register"/>, <see cref="Admit"/>, <see cref="Resume"/>, and <see cref="Cancel"/> take
/// the scheduler lock and may be called from the controlling thread or from within a running
/// operation's body (which is how nested scheduling and cross-operation resume work); while an
/// operation body runs, the controlling thread is blocked inside <see cref="RunStep(CancellationToken)"/>, so state
/// transitions are always serialized.
/// </para>
/// </summary>
public sealed class SimulationScheduler : IDisposable
{
    /// <summary>
    /// The bounded time the scheduler waits for a torn-down operation's stack to unwind from its
    /// worker, or for a worker to exit during disposal, before giving up. A stack that refuses to
    /// unwind within this window is a
    /// system-under-test bug (e.g. an infinite loop that never parks); the scheduler surfaces that
    /// rather than blocking teardown forever.
    /// </summary>
    public static readonly TimeSpan ThreadJoinTimeout = TimeSpan.FromSeconds(30);

    [ThreadStatic]
    private static SimulationOperation? t_currentOperation;

    private static readonly ContextCallback s_runBodyInCapturedContext = static state =>
    {
        var operation = (SimulationOperation)state!;
        operation.Scheduler.RunBodyScoped(operation);
    };

    // Marks a scheduler's thread-affinity violation as surfaced, so later violations stay unrecorded.
    private static readonly ExceptionDispatchInfo s_surfacedThreadAffinityViolation =
        ExceptionDispatchInfo.Capture(new InvalidOperationException("A thread-affinity violation was surfaced."));

    private readonly object _gate = new();
    private readonly object _workerGate = new();
    private readonly object _transitionPublicationGate = new();
    private readonly List<SimulationOperation> _operations = [];
    private readonly List<SimulationOperation> _activeOperations = [];
    private readonly SortedDictionary<SimulationResourceId, SimulationResource> _resources = new();
    private readonly HashSet<string> _suspendedNodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _discardedNodes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _handback = new(0, 1);
    private readonly SimulationLogicalExecutionIdSource _logicalIds = new();
    private readonly SimulationTimerQueue _clock = new();
    private readonly List<ReadinessWait> _readinessWaits = [];
    private readonly Queue<ExceptionDispatchInfo> _unhandledCallbackFailures = new();
    private readonly RaceSchedulingTrace _raceSchedulingPoints = new(DefaultRaceTraceCapacity);
    private readonly RaceTracker _raceTracker = new();
    private readonly SimulationRuntimeIdentity _runtime;
    private readonly ISimulationOperationListener? _listener;
    private readonly Queue<OperationWorker> _idleWorkers = new();
    private readonly HashSet<OperationWorker> _workers = [];

    private ISimulationSchedulingStrategy _strategy = SimulationSchedulingStrategies.RoundRobin();
    private ISimulationDecisionLog? _decisionLog;
    private SimulationDecisionReplayValidator? _replayValidator;
    private long _nextReplayDecisionId;

    private long _nextOperationId;
    private long _nextWorkerId;
    private long _nextResourceId;
    private long _nextWaiterSequence;
    private long _nextRaceSchedulingPointSequence;
    private long _dispatchSequence;
    private SimulationOperationId _lastSelected = SimulationOperationId.None;
    private SimulationOperation? _current;
    private int _controlThreadId;
    private bool _controlThreadBusy;
    private ExceptionDispatchInfo? _threadAffinityViolation;
    private bool _disposed;
    private bool _workerPoolDisposed;
    private int _createdWorkerThreadCount;
    private SimulationOperation? _pendingTerminalNotification;
    private int _transitionPublicationDepth;
    private List<CancellationTokenRegistration>? _deferredRegistrationDisposals;

    // The controlling thread dispatches one operation at a time, so one idle worker can serve every
    // non-pausing operation. Additional workers exist only while operation stacks are parked.
    private const int MaximumIdleWorkerCount = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationScheduler"/> class.
    /// </summary>
    /// <param name="runtime">The simulation runtime identity every operation runs under.</param>
    /// <param name="listener">An optional lifecycle listener for diagnostics.</param>
    /// <exception cref="UnsupportedRuntimeException">
    /// A loaded assembly rewritten by Clockwork does not support the runtime this process runs.
    /// </exception>
    public SimulationScheduler(
        SimulationRuntimeIdentity runtime,
        ISimulationOperationListener? listener = null)
        : this(
            runtime,
            new SimulationSeedAuthority(runtime?.Seed ?? throw new ArgumentNullException(nameof(runtime))),
            DateTimeOffset.UnixEpoch,
            TimeZoneInfo.Utc,
            listener)
    {
    }

    internal SimulationScheduler(
        SimulationRuntimeIdentity runtime,
        SimulationSeedAuthority seedAuthority,
        DateTimeOffset startDateTime,
        TimeZoneInfo localTimeZone,
        ISimulationOperationListener? listener = null,
        int processorCount = SimulationRuntimeEnvironment.DefaultProcessorCount)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(seedAuthority);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processorCount);
        if (runtime.IsConfigured)
        {
            throw new InvalidOperationException(
                $"Simulation runtime '{runtime.Id}' is already owned by another scheduler.");
        }

        SupportedRuntimesMarker.VerifyLoadedAssemblies();

        _runtime = runtime;
        _listener = listener;
        StartDateTime = startDateTime;
        runtime.ConfigureServices(
            new SimulationRuntimeEnvironment(
                seedAuthority,
                () => UtcNow,
                localTimeZone,
                startDateTime,
                processorCount),
            this);
    }

    /// <summary>Gets the runtime identity every operation in this scheduler runs under.</summary>
    public SimulationRuntimeIdentity Runtime => _runtime;

    internal int CreatedWorkerThreadCount => Volatile.Read(ref _createdWorkerThreadCount);

    internal int IdleWorkerThreadCount
    {
        get
        {
            lock (_workerGate)
            {
                return _idleWorkers.Count;
            }
        }
    }

    public DateTimeOffset StartDateTime { get; }

    public DateTimeOffset UtcNow => StartDateTime + VirtualTime;

    /// <summary>
    /// Gets or sets the policy that chooses which runnable operation runs next. Defaults to
    /// the round-robin strategy from <see cref="SimulationSchedulingStrategies"/>. Assigning a
    /// strategy takes effect on the next selection. Set this before driving the scheduler for a
    /// reproducible schedule.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public ISimulationSchedulingStrategy SchedulingStrategy
    {
        get
        {
            lock (_gate)
            {
                return _strategy;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                _strategy = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the decision log that captures every scheduling choice made among two or more
    /// runnable operations, as a <see cref="SimulationDecisionKind.SchedulingOrder"/> decision in the
    /// <see cref="SimulationSeedDomain.Scheduler"/> domain. When <see langword="null"/> (the default),
    /// no scheduling decisions are recorded. Attach a log to capture a schedule for later exact replay
    /// via <see cref="SimulationSchedulingStrategies.Replay"/>. Single-candidate steps are never recorded, so
    /// recording is stable against interleavings where only one operation is runnable.
    /// </summary>
    public ISimulationDecisionLog? DecisionLog
    {
        get
        {
            lock (_gate)
            {
                return _decisionLog;
            }
        }

        set
        {
            lock (_gate)
            {
                _decisionLog = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets a validator that checks each scheduling choice against a previously recorded run,
    /// throwing <see cref="SimulationDecisionReplayMismatchException"/> at the first divergence. This
    /// is orthogonal to <see cref="DecisionLog"/>: a validator can be attached with or without a live
    /// log, and with or without a replay strategy, to assert that a re-run's
    /// scheduling decisions reproduce the original exactly.
    /// </summary>
    public SimulationDecisionReplayValidator? ReplayValidator
    {
        get
        {
            lock (_gate)
            {
                return _replayValidator;
            }
        }

        set
        {
            lock (_gate)
            {
                _replayValidator = value;
            }
        }
    }

    /// <summary>
    /// The single logical-owner identity that both the controlling thread and every operation thread
    /// of a scheduler report while a controlled drive is in progress. It is deliberately outside the
    /// range of real <see cref="Environment.CurrentManagedThreadId"/> values (which are always
    /// positive) so a single-threaded guard keyed on <see cref="IsSimulationThread"/> treats a
    /// legitimate baton handoff as reentrant while still flagging genuinely escaped async work on an
    /// unrelated thread.
    /// </summary>
    public const int SimulationLogicalThreadOwnerId = int.MinValue;

    /// <summary>
    /// Gets a value indicating whether the calling physical thread is part of this scheduler's single
    /// logical simulation thread right now: either a thread currently executing one of this
    /// scheduler's operation bodies, or the thread inside <see cref="EnterControlScope"/>. Every
    /// controlled rewrite target called with this scheduler's runtime as the ambient context requires
    /// it, and throws <see cref="SimulationThreadAffinityException"/> on any other thread. A
    /// single-threaded guard also uses it as its logical-owner check. This is a logical-affinity
    /// property: code entered directly under <see cref="EnterControlScope"/> can run on the caller
    /// thread, while queued operations and continuations can run on scheduler-owned
    /// <c>SimulationOperationWorker-*</c> threads. Physical managed-thread identity is not part of
    /// the simulation contract.
    /// </summary>
    public bool IsSimulationThread =>
        (t_currentOperation is { } op && ReferenceEquals(op.Scheduler, this))
        || Environment.CurrentManagedThreadId == Volatile.Read(ref _controlThreadId);

    /// <summary>
    /// Makes the calling thread this scheduler's controlling thread until the returned scope is
    /// disposed, so <see cref="IsSimulationThread"/> reports <see langword="true"/> for it. Code that
    /// drives the scheduler (dequeue plus <see cref="RunStep(CancellationToken)"/>) runs inside this
    /// scope, as does host code that runs simulation callbacks inline while no step is in progress -
    /// cancelling a <see cref="CancellationTokenSource"/> whose token the system under test registered
    /// callbacks on, say - so those callbacks may call controlled APIs. The scope makes the
    /// controlling thread and the operation threads one logical owner, for controlled APIs and for a
    /// single-threaded guard alike.
    /// <para>
    /// One thread at a time holds the scope. The holding thread may enter it again; the nested scope
    /// leaves control with the outermost one, whose disposal releases it. The scope belongs to the
    /// thread, so it must not be held across an <see langword="await"/>.
    /// </para>
    /// </summary>
    /// <returns>A disposable that releases control when the outermost scope is disposed.</returns>
    /// <exception cref="SimulationSchedulerException">Another thread holds the scope.</exception>
    public IDisposable EnterControlScope()
    {
        int thread = Environment.CurrentManagedThreadId;
        int owner = Interlocked.CompareExchange(ref _controlThreadId, thread, 0);
        if (owner == 0)
        {
            return new ControlScope(this, thread);
        }

        if (owner == thread)
        {
            return NestedControlScope.Instance;
        }

        throw new SimulationSchedulerException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Managed thread {thread} cannot control the scheduler: managed thread {owner} controls it. One thread at a time drives the scheduler or runs simulation callbacks inline."));
    }

    /// <summary>
    /// Gets the operation the calling thread is currently executing as, or <see langword="null"/> if
    /// the calling thread is not a controlled-operation thread of this scheduler. This is how
    /// pause/yield primitives and nested scheduling discover "who am I".
    /// </summary>
    public SimulationOperation? CurrentOperation =>
        t_currentOperation is { } op && !ReferenceEquals(op.Scheduler, this) ? null : t_currentOperation;

    /// <summary>
    /// Gets the scheduler and operation currently executing on the calling physical thread.
    /// </summary>
    internal static bool TryGetExecutingOperation(
        [NotNullWhen(true)]
        out SimulationScheduler? scheduler,
        [NotNullWhen(true)]
        out SimulationOperation? operation)
    {
        operation = t_currentOperation;
        scheduler = operation?.Scheduler;
        return scheduler is not null;
    }

    /// <summary>The number of the most recent race scheduling points a scheduler retains unless configured otherwise.</summary>
    public const int DefaultRaceTraceCapacity = 4096;

    /// <summary>
    /// Gets or sets the number of the most recent race scheduling points the scheduler retains, for
    /// <see cref="CaptureRaceSchedulingPoints"/>, race reports, and replay artifacts. Older points are dropped, so a long
    /// run retains a bounded trace; replaying the run with a larger capacity recovers them. Defaults to
    /// <see cref="DefaultRaceTraceCapacity"/>, and is at most
    /// <see cref="Replay.ReplayArtifactLimits.MaxRaceSchedulingPoints"/>, the most a replay artifact holds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive, or exceeds the replay artifact limit.</exception>
    public int RaceTraceCapacity
    {
        get
        {
            lock (_gate)
            {
                return _raceSchedulingPoints.Capacity;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, Replay.ReplayArtifactLimits.MaxRaceSchedulingPoints);
            lock (_gate)
            {
                _raceSchedulingPoints.Capacity = value;
            }
        }
    }

    /// <summary>
    /// Captures the most recent injected race-exploration points (see <see cref="RaceTraceCapacity"/>) in deterministic
    /// order. Each point's <see cref="RaceSchedulingPoint.Sequence"/> counts every point the run has reached.
    /// </summary>
    public IReadOnlyList<RaceSchedulingPoint> CaptureRaceSchedulingPoints()
    {
        lock (_gate)
        {
            return [.. _raceSchedulingPoints];
        }
    }

    /// <summary>Gets the deterministic first race detected in this scheduler, if any.</summary>
    public RaceReport? FirstRace
    {
        get
        {
            lock (_gate)
            {
                return _raceTracker.FirstRace;
            }
        }
    }

    /// <summary>Gets the size of the race detector's retained state.</summary>
    internal RaceTrackerStatistics RaceTrackerStatistics
    {
        get
        {
            lock (_gate)
            {
                return _raceTracker.Statistics;
            }
        }
    }

    /// <summary>
    /// Records an injected scheduling point and yields the current operation through the configured
    /// scheduling strategy and decision/replay pipeline.
    /// </summary>
    internal void ReachRaceSchedulingPoint(
        RaceAccessKind kind,
        RaceMemoryLocationKind? locationKind,
        object? target,
        string member,
        long? elementIndex,
        RaceSourceLocation source)
    {
        SimulationOperation operation = RequireCurrentOperation();
        bool suppressInterleaving;
        lock (_gate)
        {
            RaceTracker.LocationState? location = kind is RaceAccessKind.Read or RaceAccessKind.Write
                ? _raceTracker.ResolveLocation(locationKind, target, member, elementIndex)
                : null;
            var point = new RaceSchedulingPoint(
                ++_nextRaceSchedulingPointSequence,
                operation.Id,
                kind,
                location?.Description ?? member,
                source);
            _raceSchedulingPoints.Add(point);
            if (location is not null)
            {
                _raceTracker.RecordAccess(operation, kind, location, source, _raceSchedulingPoints);
            }

            suppressInterleaving = _raceTracker.HasHeldSynchronization(operation);
        }

        if (!suppressInterleaving)
        {
            Yield();
        }
    }

    internal void EnterExclusiveRaceSynchronization(object synchronization)
    {
        SimulationOperation operation = RequireCurrentOperation();
        lock (_gate)
        {
            _raceTracker.EnterExclusive(operation, synchronization);
        }
    }

    internal void ExitExclusiveRaceSynchronization(object synchronization)
    {
        SimulationOperation operation = RequireCurrentOperation();
        lock (_gate)
        {
            _raceTracker.ExitExclusive(operation, synchronization);
        }
    }

    /// <summary>
    /// Ends <paramref name="operation"/>'s race tracking. Every path by which an operation ends calls this exactly once,
    /// at the point the operation can record no further race event: its body has returned or unwound, or never started.
    /// An operation whose thread does not unwind within <see cref="ThreadJoinTimeout"/> may still record events, so its
    /// tracking does not end. Requires the scheduler lock.
    /// </summary>
    private void EndRaceTracking(SimulationOperation operation)
    {
        Debug.Assert(Monitor.IsEntered(_gate), "Race tracking ends under the scheduler lock.");
        Debug.Assert(!ReferenceEquals(t_currentOperation, operation), "An operation's body no longer runs when its race tracking ends.");
        _raceTracker.CompleteOperation(operation);
    }

    internal void SignalRaceSynchronization(object synchronization)
    {
        SimulationOperation operation = RequireCurrentOperation();
        lock (_gate)
        {
            _raceTracker.SignalSynchronization(operation, synchronization);
        }
    }

    internal void WaitRaceSynchronization(object synchronization)
    {
        SimulationOperation operation = RequireCurrentOperation();
        lock (_gate)
        {
            _raceTracker.WaitSynchronization(operation, synchronization);
        }
    }

    /// <summary>
    /// Registers a new operation in the <see cref="SimulationOperationState.Created"/> state without
    /// admitting it for scheduling. The operation's parent is the operation the calling thread is
    /// running as (enabling nested parent/child identity), or <see cref="SimulationOperationId.None"/>
    /// for a root registration.
    /// </summary>
    /// <param name="workDescription">A short, stable description of the work, for diagnostics.</param>
    /// <param name="body">The operation body. Runs exactly once, on the operation's own thread.</param>
    /// <param name="node">The node the operation is scoped to, or <see langword="null"/> for cluster-level work.</param>
    /// <param name="priority">
    /// The operation's scheduling priority (see <see cref="SimulationOperation.Priority"/>). Only a
    /// priority strategy from <see cref="SimulationSchedulingStrategies"/> consults it; it is inert
    /// under the other strategies. Defaults to <c>0</c>.
    /// </param>
    /// <returns>The newly created operation.</returns>
    public SimulationOperation Register(string workDescription, Action body, SimulationNodeIdentity? node = null, int priority = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(workDescription);
        ArgumentNullException.ThrowIfNull(body);
        return RegisterCore(
            workDescription,
            body,
            node,
            _logicalIds.Next(),
            ExecutionContext.Capture(),
            priority);
    }

    /// <summary>
    /// Registers and immediately admits an operation, so it is <see cref="SimulationOperationState.Runnable"/>
    /// on return. Convenience for the common "create a ready-to-run operation" case.
    /// </summary>
    /// <param name="workDescription">A short, stable description of the work, for diagnostics.</param>
    /// <param name="body">The operation body.</param>
    /// <param name="node">The node the operation is scoped to, or <see langword="null"/>.</param>
    /// <param name="priority">
    /// The operation's scheduling priority (see <see cref="SimulationOperation.Priority"/>); only a
    /// priority strategy from <see cref="SimulationSchedulingStrategies"/> consults it. Defaults to
    /// <c>0</c>.
    /// </param>
    /// <returns>The newly created, admitted operation.</returns>
    public SimulationOperation Schedule(string workDescription, Action body, SimulationNodeIdentity? node = null, int priority = 0)
    {
        var operation = Register(workDescription, body, node, priority);
        Admit(operation);
        return operation;
    }

    internal SimulationOperation Schedule(
        string workDescription,
        SimulationExecutionSnapshot snapshot,
        ExecutionContext? executionContext,
        Action body,
        bool preserveLogicalExecution)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Runtime != _runtime)
        {
            throw new SimulationSchedulerException("Cannot schedule work captured from a different simulation runtime.");
        }

        var logicalExecutionId = preserveLogicalExecution && !snapshot.LogicalExecutionId.IsNone
            ? snapshot.LogicalExecutionId
            : _logicalIds.Next();
        var operation = RegisterCore(
            workDescription,
            body,
            snapshot.Node,
            logicalExecutionId,
            executionContext,
            priority: 0);
        Admit(operation);
        return operation;
    }

    private SimulationOperation RegisterCore(
        string workDescription,
        Action body,
        SimulationNodeIdentity? node,
        SimulationLogicalExecutionId logicalExecutionId,
        ExecutionContext? capturedContext,
        int priority)
    {
        SimulationOperation operation;
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ThrowIfNodeDiscardedUnderLock(node);
                SimulationOperation? parent =
                    t_currentOperation is { } currentParent && ReferenceEquals(currentParent.Scheduler, this)
                        ? currentParent
                        : null;
                var parentId = parent?.Id ?? SimulationOperationId.None;
                var id = new SimulationOperationId(++_nextOperationId);
                operation = new SimulationOperation(
                    this,
                    id,
                    parentId,
                    _runtime,
                    node,
                    logicalExecutionId,
                    workDescription,
                    body,
                    capturedContext,
                    priority);
                _operations.Add(operation);
                _activeOperations.Add(operation);
                _raceTracker.RegisterOperation(operation, parent);
            }

            Notify(operation, SimulationOperationState.Created);
        }

        return operation;
    }

    internal void Schedule(SimulationNodeIdentity? node, Action continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        Schedule(
            "Simulation continuation",
            CaptureSchedulingSnapshot(node),
            ExecutionContext.Capture(),
            continuation,
            preserveLogicalExecution: true);
    }

    internal void Schedule(Action continuation) => Schedule(node: null, continuation);

    internal ISimulationWorkRegistration ScheduleWhenReady(
        SimulationNodeIdentity? node,
        Func<bool> isReady,
        Action continuation) =>
        ScheduleWhenReady(
            "Simulation continuation",
            CaptureSchedulingSnapshot(node),
            ExecutionContext.Capture(),
            isReady,
            continuation);

    internal ISimulationWorkRegistration ScheduleWhenReady(Func<bool> isReady, Action continuation) =>
        ScheduleWhenReady(node: null, isReady, continuation);

    internal bool RunOne(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CurrentOperation is not null)
        {
            long before;
            lock (_gate)
            {
                before = _dispatchSequence;
            }

            Yield();
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                // Resuming this operation accounts for one dispatch. A larger delta means another
                // operation ran while this one yielded; otherwise callers should park so time or
                // deadlock detection can make progress instead of repeatedly selecting themselves.
                return _dispatchSequence - before > 1;
            }
        }

        return RunStep(cancellationToken);
    }

    internal void DrainUntil(Func<bool> completed, CancellationToken cancellationToken) =>
        DrainUntil(completed, "Orleans.Testing.Simulation.Runtime.Tasks synchronous wait", cancellationToken);

    internal ISimulationTimer RegisterTimer(
        SimulationNodeIdentity? node,
        TimeSpan delay,
        Action? onElapsed) =>
        RegisterTimer(
            CaptureSchedulingSnapshot(node),
            delay,
            onElapsed,
            "Simulation timeout");

    internal ISimulationTimer RegisterTimer(TimeSpan delay, Action? onElapsed = null) =>
        RegisterTimer(node: null, delay, onElapsed);

    internal ISimulationWorkRegistration ScheduleWhenReady(
        string workDescription,
        SimulationExecutionSnapshot snapshot,
        ExecutionContext? executionContext,
        Func<bool> isReady,
        Action continuation)
    {
        ArgumentException.ThrowIfNullOrEmpty(workDescription);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(isReady);
        ArgumentNullException.ThrowIfNull(continuation);
        if (snapshot.Runtime != _runtime)
        {
            throw new SimulationSchedulerException("Cannot schedule readiness work captured from a different simulation runtime.");
        }

        var logicalExecutionId = snapshot.LogicalExecutionId.IsNone
            ? _logicalIds.Next()
            : snapshot.LogicalExecutionId;
        var operation = RegisterCore(
            workDescription,
            continuation,
            snapshot.Node,
            logicalExecutionId,
            executionContext,
            priority: 0);
        var wait = ReadinessWait.ForContinuation(this, isReady, operation);
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfNodeDiscardedUnderLock(snapshot.Node);
            if (operation.IsTerminal)
            {
                throw new SimulationSchedulerException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Cannot schedule readiness work for terminal operation {operation.Id}."));
            }

            _readinessWaits.Add(wait);
        }

        return wait;
    }

    internal ISimulationTimer RegisterTimer(
        SimulationExecutionSnapshot snapshot,
        TimeSpan delay,
        Action? onElapsed,
        string workDescription)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
        ArgumentException.ThrowIfNullOrEmpty(workDescription);
        if (snapshot.Runtime != _runtime)
        {
            throw new SimulationSchedulerException("Cannot register a timer captured from a different simulation runtime.");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfNodeDiscardedUnderLock(snapshot.Node);
            Action? scheduleCallback = onElapsed is null
                ? null
                : () => Schedule(
                    workDescription,
                    snapshot,
                    executionContext: null,
                    onElapsed,
                    preserveLogicalExecution: true);
            return _clock.Schedule(delay, scheduleCallback, "CallbackTimer", snapshot.Node);
        }
    }

    internal IDisposable ScheduleAfter(
        string workDescription,
        Action body,
        TimeSpan delay,
        SimulationNodeIdentity? node = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        if (delay == TimeSpan.Zero)
        {
            return new ScheduledOperationRegistration(
                this,
                Schedule(workDescription, body, node),
                timer: null);
        }

        var snapshot = CaptureSchedulingSnapshot(node);
        var executionContext = ExecutionContext.Capture();
        var registration = new ScheduledOperationRegistration(this);
        ISimulationTimer timer;
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfNodeDiscardedUnderLock(snapshot.Node);
            timer = _clock.Schedule(
                delay,
                () => registration.Schedule(
                    () => Schedule(
                        workDescription,
                        snapshot,
                        executionContext,
                        body,
                        preserveLogicalExecution: true)),
                node: snapshot.Node);
        }

        registration.SetTimer(timer);
        return registration;
    }

    internal void SetNodeEnabled(SimulationNodeIdentity node, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (enabled)
            {
                _suspendedNodes.Remove(node.Address);
            }
            else
            {
                _suspendedNodes.Add(node.Address);
            }
        }
    }

    internal void DrainUntil(
        Func<bool> completed,
        string apiName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentException.ThrowIfNullOrEmpty(apiName);
        cancellationToken.ThrowIfCancellationRequested();
        if (CurrentOperation is { } operation)
        {
            while (!completed())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wait = ReadinessWait.ForSynchronousWait(
                    this,
                    () => completed() || cancellationToken.IsCancellationRequested,
                    operation,
                    apiName);
                lock (_gate)
                {
                    ThrowIfDisposed();
                    _readinessWaits.Add(wait);
                }

                Pause(SimulationPauseReason.ResourceWait(apiName));
                wait.ThrowIfFailed();
            }

            return;
        }

        while (!completed())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RunStep(cancellationToken))
            {
                continue;
            }

            if (TryAdvanceVirtualTime())
            {
                continue;
            }

            throw new SimulationSynchronousWaitDeadlockException(apiName);
        }
    }

    internal void ParkCurrent(string apiName)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiName);
        var operation = RequireCurrentOperation();
        var wait = ReadinessWait.ForIndefiniteWait(this, operation);
        lock (_gate)
        {
            ThrowIfDisposed();
            _readinessWaits.Add(wait);
        }

        Pause(SimulationPauseReason.ResourceWait(apiName));
    }

    private SimulationExecutionSnapshot CaptureSchedulingSnapshot(SimulationNodeIdentity? node)
    {
        var current = SimulationExecutionContext.Current;
        if (current is not null && current.Runtime == _runtime)
        {
            return current with { Node = node ?? current.Node };
        }

        return new SimulationExecutionSnapshot(
            _runtime,
            node,
            CurrentOperation?.LogicalExecutionId ?? SimulationLogicalExecutionId.None);
    }

    /// <summary>
    /// Creates a new <see cref="SimulationResource"/> owned by this scheduler, with a stable,
    /// monotonically-assigned <see cref="SimulationResourceId"/>. The resource starts unowned, with no
    /// waiters; acquire/release policy and waits are layered on top by callers via the scheduler's
    /// wait/signal primitives and the resource's own bookkeeping fields. Controlled synchronization
    /// primitives use this entry point to obtain the resource backing one synchronization object.
    /// </summary>
    /// <param name="kind">The diagnostic classification of the primitive the resource models.</param>
    /// <param name="name">A short, stable, human-readable name for diagnostics.</param>
    /// <returns>The newly created resource.</returns>
    public SimulationResource CreateResource(SimulationResourceKind kind, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            ThrowIfDisposed();
            var id = new SimulationResourceId(++_nextResourceId);
            var resource = new SimulationResource(this, id, kind, name);
            _resources.Add(id, resource);
            return resource;
        }
    }

    /// <summary>
    /// Captures a deterministic snapshot of every resource currently registered with this scheduler,
    /// in stable <see cref="SimulationResourceId"/> order, for diagnostics and deadlock reporting.
    /// </summary>
    /// <returns>An ordered, immutable list of resources.</returns>
    public IReadOnlyList<SimulationResource> CaptureResources()
    {
        lock (_gate)
        {
            return [.. _resources.Values];
        }
    }


    /// <summary>
    /// Records (or clears) the operation that owns <paramref name="resource"/>. Ownership is the raw
    /// metadata the wait-for graph reads to draw a "waiter -&gt; owner" edge and the hook a
    /// controlled <c>Monitor</c> or synchronous <c>Task</c> wait sets when it acquires a resource; this
    /// method deliberately performs no acquire/release policy of its own (it does not block or count),
    /// so each primitive composes its own semantics on top. Must be given operations/resources that
    /// belong to this scheduler.
    /// </summary>
    /// <param name="resource">The resource whose owner is being set. Must belong to this scheduler.</param>
    /// <param name="owner">The owning operation, or <see langword="null"/> to mark the resource unowned.</param>
    public void MarkResourceOwner(SimulationResource resource, SimulationOperation? owner)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ValidateResourceOwnership(resource);
        if (owner is not null)
        {
            ValidateOwnership(owner);
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            resource.Owner = owner;
        }
    }

    /// <summary>
    /// Admits a <see cref="SimulationOperationState.Created"/> operation for scheduling, transitioning
    /// it to <see cref="SimulationOperationState.Runnable"/>.
    /// </summary>
    /// <param name="operation">The operation to admit.</param>
    public void Admit(SimulationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateOwnership(operation);
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ThrowIfNodeDiscardedUnderLock(operation.Node);
                operation.ApplyTransition(SimulationOperationState.Runnable);
            }

            Notify(operation, SimulationOperationState.Runnable);
        }
    }

    /// <summary>
    /// Selects exactly one <see cref="SimulationOperationState.Runnable"/> operation deterministically,
    /// grants it the permission baton, and blocks the controlling thread until that operation hands
    /// control back by pausing, yielding, completing, or faulting. Terminal operations are cleaned up
    /// before returning.
    /// <para>
    /// Selection is a stable round-robin by <see cref="SimulationOperation.Id"/>: the runnable
    /// operation with the smallest id greater than the last-selected one, wrapping to the smallest
    /// runnable id when none is greater. When no operation ever yields (the compatibility case) this
    /// is identical to strict registration order - each operation runs to completion before the next
    /// starts - so legacy deterministic ordering is preserved; when operations yield it gives every
    /// operation a turn instead of starving all but the lowest id. Alternative scheduling policies are
    /// selected through <see cref="SchedulingStrategy"/>.
    /// </para>
    /// <para>
    /// Whether or not an operation ran, the step then rethrows a failure not yet surfaced: the first
    /// controlled API called with this simulation's ambient context off its logical thread
    /// (<see cref="SimulationThreadAffinityException"/>) ahead of the oldest exception escaping a lane
    /// callback.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">A token checked before dispatching the operation.</param>
    /// <returns><see langword="true"/> if an operation ran; <see langword="false"/> if none was runnable.</returns>
    public bool RunStep(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool result = RunStepCore(cancellationToken, out ExceptionDispatchInfo? failure);
        failure?.Throw();
        return result;
    }

    internal bool RunStepCapturingFailure(
        CancellationToken cancellationToken,
        out Exception? failure)
    {
        bool result = RunStepCore(cancellationToken, out ExceptionDispatchInfo? captured);
        failure = captured?.SourceException;
        return result;
    }

    private bool RunStepCore(
        CancellationToken cancellationToken,
        out ExceptionDispatchInfo? failure)
    {
        failure = null;
        if (Monitor.IsEntered(_transitionPublicationGate))
        {
            throw new SimulationSchedulerException(
                "RunStep/Drain cannot be driven reentrantly from an operation listener callback. " +
                "Listeners may inspect state or request non-driving transitions, but the controlling thread must resume scheduler driving after the callback returns.");
        }

        PromoteReadyWaits();
        SimulationOperation operation;
        SimulationOperation? resumedDeadlock = null;
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_controlThreadBusy)
                {
                    throw new SimulationSchedulerException(
                        "The scheduler is already being driven by another thread. RunStep/Drain must be driven by a single controlling thread at a time.");
                }

                var useRoundRobinFastPath = CanSelectRoundRobinWithoutSnapshot();
                SimulationOperation? next = useRoundRobinFastPath
                    ? FindRoundRobinRunnableUnderLock()
                    : null;
                var hasRunnable = useRoundRobinFastPath
                    ? next is not null
                    : HasRunnableUnderLock();
                if (!hasRunnable && !_clock.HasPending && HasResumableDeadlockedSynchronousWaitUnderLock())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    resumedDeadlock = ResumeDeadlockedSynchronousWaitUnderLock();
                    next = useRoundRobinFastPath
                        ? FindRoundRobinRunnableUnderLock()
                        : null;
                    hasRunnable = useRoundRobinFastPath
                        ? next is not null
                        : HasRunnableUnderLock();
                }

                if (!hasRunnable)
                {
                    failure = TakeFailureUnderLock();
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (useRoundRobinFastPath)
                {
                    operation = next ?? throw new UnreachableException();
                    _lastSelected = operation.Id;
                }
                else
                {
                    operation = SelectRunnable() ?? throw new UnreachableException();
                }

                operation.ApplyTransition(SimulationOperationState.Running);
                _dispatchSequence++;
                _current = operation;
                _controlThreadBusy = true;
            }

            if (resumedDeadlock is not null)
            {
                Notify(resumedDeadlock, SimulationOperationState.Runnable);
            }

            Notify(operation, SimulationOperationState.Running);
        }

        bool assignedWorker = EnsureThreadStarted(operation);

        // Grant the baton and block until the operation hands control back. Only the granted
        // operation executes SUT code between here and the handback.
        if (!assignedWorker)
        {
            operation.GrantPermission();
        }

        _handback.Wait();

        SimulationOperationState resulting;
        lock (_gate)
        {
            resulting = operation.State;
            _current = null;
            _controlThreadBusy = false;
        }

        if (SimulationOperation.IsTerminalState(resulting))
        {
            FinalizeTerminal(operation);
        }

        // Rewritten code can load after the simulation starts (behind a delegate, say), so every step that ran checks
        // any assembly loaded since the last check. Checking only after a step keeps a caller that retries failed steps
        // making progress, and checking before a recorded failure is taken loses none.
        SupportedRuntimesMarker.VerifyLoadedAssemblies();
        lock (_gate)
        {
            failure = TakeFailureUnderLock();
        }

        return true;
    }

    internal void ReportUnhandledCallbackException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _unhandledCallbackFailures.Enqueue(ExceptionDispatchInfo.Capture(exception));
        }
    }

    /// <summary>
    /// Records a controlled API called with this simulation's ambient context off its logical thread, so the thread
    /// driving the scheduler surfaces it from its next <see cref="RunStep(CancellationToken)"/>, whether or not an
    /// operation runs. Only the first violation is recorded: escaped work typically calls many controlled APIs, and the
    /// first identifies where it escaped. The calling thread is the escaped one, so recording takes no lock.
    /// </summary>
    /// <param name="apiName">The controlled API which was called.</param>
    internal void ReportThreadAffinityViolation(string apiName)
    {
        // Once a violation is recorded or surfaced, later ones skip building an exception.
        if (Volatile.Read(ref _threadAffinityViolation) is not null)
        {
            return;
        }

        // The driving thread rethrows its own instance, carrying the escaped thread's stack: one exception object must
        // not be thrown on two threads at once.
        var violation = new SimulationThreadAffinityException(apiName, _runtime);
        ExceptionDispatchInfo.SetCurrentStackTrace(violation);
        Interlocked.CompareExchange(ref _threadAffinityViolation, ExceptionDispatchInfo.Capture(violation), null);
    }

    /// <summary>Takes the recorded thread-affinity violation, if it has not been surfaced yet.</summary>
    internal ExceptionDispatchInfo? TakeThreadAffinityViolation()
    {
        ExceptionDispatchInfo? violation = Volatile.Read(ref _threadAffinityViolation);
        if (violation is null || ReferenceEquals(violation, s_surfacedThreadAffinityViolation))
        {
            return null;
        }

        // The slot holds at most one violation, so one taker wins it; the sentinel keeps later violations unrecorded.
        ExceptionDispatchInfo? taken = Interlocked.CompareExchange(
            ref _threadAffinityViolation,
            s_surfacedThreadAffinityViolation,
            violation);
        return ReferenceEquals(taken, violation) ? violation : null;
    }

    private ExceptionDispatchInfo? TakeFailureUnderLock() =>
        TakeThreadAffinityViolation()
        ?? (_unhandledCallbackFailures.TryDequeue(out ExceptionDispatchInfo? failure) ? failure : null);

    private void PromoteReadyWaits()
    {
        ReadinessWait[]? waits = null;
        int count;
        lock (_gate)
        {
            count = _readinessWaits.Count;
            if (count == 0)
            {
                return;
            }

            waits = ArrayPool<ReadinessWait>.Shared.Rent(count);
            _readinessWaits.CopyTo(waits, 0);
        }

        try
        {
            for (var index = 0; index < count; index++)
            {
                var wait = waits[index];
                if (wait.IsCanceled || !wait.IsReady())
                {
                    continue;
                }

                SimulationOperation? operation = null;
                using (EnterTransitionPublicationScope())
                {
                    lock (_gate)
                    {
                        if (!_readinessWaits.Remove(wait) || wait.IsCanceled)
                        {
                            continue;
                        }

                        operation = wait.Operation;
                        operation.ApplyTransition(SimulationOperationState.Runnable);
                    }

                    Notify(operation, SimulationOperationState.Runnable);
                }
            }
        }
        finally
        {
            Array.Clear(waits, 0, count);
            ArrayPool<ReadinessWait>.Shared.Return(waits);
        }
    }

    private SimulationOperation? ResumeDeadlockedSynchronousWaitUnderLock()
    {
        foreach (var wait in _readinessWaits)
        {
            if (!wait.IsSynchronous || wait.IsIndefinite || wait.IsCanceled)
            {
                continue;
            }

            _readinessWaits.Remove(wait);
            wait.Fail(new SimulationSynchronousWaitDeadlockException(wait.ApiName!));
            wait.Operation.ApplyTransition(SimulationOperationState.Runnable);
            return wait.Operation;
        }

        return null;
    }

    private bool HasResumableDeadlockedSynchronousWaitUnderLock()
    {
        foreach (var wait in _readinessWaits)
        {
            if (wait.IsSynchronous && !wait.IsIndefinite && !wait.IsCanceled)
            {
                return true;
            }
        }

        return false;
    }

    private void CancelReadinessWait(ReadinessWait wait)
    {
        SimulationOperation? operation = null;
        lock (_gate)
        {
            if (!_readinessWaits.Remove(wait))
            {
                return;
            }

            if (!wait.IsSynchronous && wait.Operation.State == SimulationOperationState.Created)
            {
                operation = wait.Operation;
            }
        }

        if (operation is not null)
        {
            Cancel(operation);
        }
    }

    /// <summary>
    /// Repeatedly runs steps until no operation is runnable, advancing modeled virtual time to fire
    /// the next due timeout whenever the runnable set is exhausted, and repeating until neither any
    /// operation is runnable nor any timeout is pending. Returns the number of steps executed (time
    /// advances are not steps). A step count that keeps growing without the set of operations
    /// shrinking indicates operations that only ever yield; callers that need a bound should cap
    /// iterations themselves.
    /// </summary>
    /// <returns>The number of steps executed.</returns>
    /// <summary>Drains scheduler work until quiescence or cancellation is requested.</summary>
    /// <param name="cancellationToken">A token that can cancel the drain between scheduler dispatches.</param>
    /// <returns>The number of steps executed.</returns>
    public int Drain(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var steps = 0;
        while (true)
        {
            while (RunStepForPump(cancellationToken))
            {
                steps++;
            }

            if (NextTimerDue is null)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryAdvanceVirtualTime())
            {
                break;
            }
        }

        return steps;
    }

    /// <summary>
    /// Runs operations which are currently runnable until the scheduler becomes idle, without
    /// advancing virtual time.
    /// </summary>
    /// <returns>The number of operations dispatched.</returns>
    /// <summary>Runs currently runnable operations until idle or cancellation is requested.</summary>
    /// <param name="cancellationToken">A token that can cancel the run between scheduler dispatches.</param>
    /// <returns>The number of operations dispatched.</returns>
    internal int RunUntilIdle(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var steps = 0;
        while (RunStepForPump(cancellationToken))
        {
            steps++;
        }

        return steps;
    }

    internal bool RunStepForPump(CancellationToken cancellationToken)
    {
        bool result = RunStepCore(cancellationToken, out ExceptionDispatchInfo? failure);
        failure?.Throw();
        return result;
    }

    /// <summary>
    /// Validates that decision and scheduling replay streams were consumed completely at an explicit
    /// successful end-of-run boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="Drain(CancellationToken)"/> does not call this automatically because a scheduler is reusable: a quiescent
    /// batch can be followed by more scheduled work whose replay records remain unread. Call this once
    /// the overall run is known to be complete. Partial or aborted runs must omit this call.
    /// </remarks>
    public void ValidateReplayComplete()
    {
        lock (_gate)
        {
            if (_activeOperations.Count > 0)
            {
                throw new SimulationSchedulerException(
                    "Replay completion cannot be validated while non-terminal operations remain. " +
                    "Finish the run first, or omit completion validation for a partial or aborted run.");
            }
        }

        _replayValidator?.ValidateComplete();
        (_strategy as ISimulationSchedulingStrategyRuntimeHooks)?.ValidateComplete();
    }

    /// <summary>
    /// Validates complete consumption of decision streams at an explicit terminal boundary which may
    /// retain non-terminal operations, such as a diagnosed deadlock.
    /// </summary>
    public void ValidateReplayDecisionStreamsComplete()
    {
        _replayValidator?.ValidateComplete();
        (_strategy as ISimulationSchedulingStrategyRuntimeHooks)?.ValidateComplete();
    }

    /// <summary>
    /// Advances modeled virtual time to the earliest pending timeout and fires every timeout due at
    /// that instant, resolving each affected waiter as <see cref="SimulationWaitOutcome.TimedOut"/>
    /// and making its operation runnable. Advancing is only legal when no operation is currently
    /// runnable (the controlling thread is idle and nothing can make progress in the present instant);
    /// this is what guarantees a pending signal always precedes a same-instant timeout. It is a no-op
    /// - returning <see langword="false"/> - when an operation is still runnable or no timeout is
    /// pending.
    /// </summary>
    /// <returns><see langword="true"/> if time advanced and at least one waiter timed out; otherwise <see langword="false"/>.</returns>
    public bool TryAdvanceVirtualTime()
    {
        IReadOnlyList<ISimulationTimerEntry> due;
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_controlThreadBusy)
                {
                    throw new SimulationSchedulerException(
                        "Cannot advance virtual time while an operation is running; drive RunStep to quiescence first.");
                }

                if (HasRunnableUnderLock() || !_clock.HasPending)
                {
                    return false;
                }

                due = _clock.AdvanceToNextDue();
            }

            return ElapseTimers(due) > 0;
        }
    }

    internal void AdvanceVirtualTimeTo(TimeSpan target)
    {
        IReadOnlyList<ISimulationTimerEntry> due;
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_controlThreadBusy || HasRunnableUnderLock())
                {
                    throw new SimulationSchedulerException(
                        "Cannot advance virtual time while simulation work is runnable.");
                }

                due = _clock.AdvanceTo(target);
            }

            _ = ElapseTimers(due);
        }
    }

    internal TimeSpan? NextTimerDue
    {
        get
        {
            lock (_gate)
            {
                return _clock.NextDueTime();
            }
        }
    }

    internal int PendingTimerRegistrationCount
    {
        get
        {
            lock (_gate)
            {
                return _clock.RegistrationCount;
            }
        }
    }

    internal IReadOnlyList<SimulationPendingTimerInfo> CapturePendingTimers()
    {
        lock (_gate)
        {
            return _clock.SnapshotAllPending();
        }
    }

    internal bool HasPendingWork(SimulationNodeIdentity node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate)
        {
            foreach (var operation in _activeOperations)
            {
                if (operation.Node == node)
                {
                    return true;
                }
            }

            return _clock.HasPendingTimer(node);
        }
    }

    internal void EnsureNodeCanDiscardPendingWork(SimulationNodeIdentity node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_discardedNodes.Contains(node.Address))
            {
                return;
            }

            if (HasRunningOperationUnderLock(node))
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Cannot discard pending work for simulation node '{node.Address}' while one of its callbacks is running."));
            }
        }
    }

    internal void DiscardPendingWork(SimulationNodeIdentity node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (HasRunningOperationUnderLock(node))
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Cannot discard pending work for simulation node '{node.Address}' while one of its callbacks is running."));
            }

            _discardedNodes.Add(node.Address);
            _suspendedNodes.Remove(node.Address);
        }

        RemovePendingWork(node);
    }

    internal void RemovePendingWork(SimulationNodeIdentity node)
    {
        ArgumentNullException.ThrowIfNull(node);
        List<ReadinessWait> readinessWaits = [];
        SimulationOperation[] operations;
        lock (_gate)
        {
            _clock.CancelPendingTimers(node);
            for (var index = 0; index < _readinessWaits.Count;)
            {
                var wait = _readinessWaits[index];
                if (wait.Operation.Node != node)
                {
                    index++;
                    continue;
                }

                readinessWaits.Add(wait);
                _readinessWaits.RemoveAt(index);
            }

            operations = _activeOperations
                .Where(operation => operation.Node == node)
                .ToArray();
        }

        try
        {
            foreach (ReadinessWait wait in readinessWaits)
            {
                wait.Cancel();
            }

            foreach (SimulationOperation operation in operations)
            {
                Cancel(operation);
            }
        }
        finally
        {
            lock (_gate)
            {
                _clock.CompactCanceled();
            }
        }
    }

    private int ElapseTimers(IReadOnlyList<ISimulationTimerEntry> due)
    {
        var elapsed = 0;
        foreach (var timeoutRegistration in due)
        {
            if (timeoutRegistration is SimulationTimerRegistration timer)
            {
                lock (_gate)
                {
                    if (timer.Node is { } node && _discardedNodes.Contains(node.Address))
                    {
                        timer.Cancel();
                    }
                }

                if (timer.TryClaimElapsed(out Action? callback))
                {
                    callback?.Invoke();
                    elapsed++;
                }

                continue;
            }

            var resourceTimeout = (SimulationResourceTimeoutRegistration)timeoutRegistration;
            SimulationOperation? operation = null;
            CancellationTokenRegistration cancellationRegistration = default;
            lock (_gate)
            {
                var waiter = resourceTimeout.Waiter;
                if (waiter.TryResolve(SimulationWaitOutcome.TimedOut))
                {
                    RecordWaitResolution(waiter, SimulationWaitOutcome.TimedOut);
                    cancellationRegistration = DetachWaiterRegistrationsUnderLock(waiter);
                    waiter.Resource.RemoveWaiter(waiter);
                    waiter.Operation.ApplyTransition(SimulationOperationState.Runnable);
                    operation = waiter.Operation;
                }
            }

            if (operation is not null)
            {
                Notify(operation, SimulationOperationState.Runnable);
                DisposeRegistration(cancellationRegistration);
                elapsed++;
            }
        }

        return elapsed;
    }

    private bool HasRunnableUnderLock()
    {
        foreach (var operation in _activeOperations)
        {
            if (operation.State == SimulationOperationState.Runnable && IsEligibleUnderLock(operation))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resumes a <see cref="SimulationOperationState.Paused"/> operation, transitioning it back to
    /// <see cref="SimulationOperationState.Runnable"/> so a subsequent <see cref="RunStep(CancellationToken)"/> can grant
    /// it the baton again. Its physical thread stays parked (not busy-spinning) until then.
    /// </summary>
    /// <param name="operation">The paused operation to resume.</param>
    public void Resume(SimulationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateOwnership(operation);
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (operation.Waiter is not null)
                {
                    throw new SimulationSchedulerException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Cannot resume operation {operation.Id} directly while it has an active resource waiter. Resolve it through SignalOne/SignalAll, timeout, or cancellation so the wait receives exactly one outcome."));
                }

                operation.ApplyTransition(SimulationOperationState.Runnable);
            }

            Notify(operation, SimulationOperationState.Runnable);
        }
    }

    /// <summary>
    /// Cancels a non-running operation (<see cref="SimulationOperationState.Created"/>,
    /// <see cref="SimulationOperationState.Runnable"/>, or <see cref="SimulationOperationState.Paused"/>),
    /// transitioning it to <see cref="SimulationOperationState.Canceled"/>. If the operation has a
    /// parked physical thread, it is unwound cooperatively and joined. Cancellation is idempotent for
    /// already-terminal operations. The currently-running operation cannot be canceled from outside
    /// its own body (it holds the baton); it should observe cooperative cancellation instead.
    /// </summary>
    /// <param name="operation">The operation to cancel.</param>
    public void Cancel(SimulationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateOwnership(operation);

        bool needsUnwind;
        CancellationTokenRegistration registration;
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                if (operation.IsTerminal)
                {
                    return;
                }

                if (ReferenceEquals(operation, _current))
                {
                    throw new SimulationSchedulerException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Cannot externally cancel in-flight operation {operation.Id} before it hands control back to the scheduler; retry after RunStep returns or use cooperative cancellation from within its body."));
                }

                operation.RequestTermination();
                registration = DetachWaiterUnderLock(operation);
                operation.ApplyTransition(SimulationOperationState.Canceled);
                _activeOperations.Remove(operation);
                needsUnwind = operation.Thread is not null;
            }

            Notify(operation, SimulationOperationState.Canceled);
        }

        DisposeRegistration(registration);
        if (needsUnwind)
        {
            UnwindParkedOperation(operation);
        }

        // The unwound body's finally blocks still record synchronization (a lock statement's exit releases its lock
        // to the next acquirer), so its race tracking ends only once its thread has exited.
        lock (_gate)
        {
            EndRaceTracking(operation);
        }

        operation.ReleaseExecutionState();
        operation.DisposeSignals();
    }

    /// <summary>
    /// Pauses the calling operation, releasing the permission baton with the given reason and parking
    /// the calling thread until the operation is resumed and re-granted the baton. Must be called from
    /// within a running operation body of this scheduler. When the operation is later torn down while
    /// paused, this method's park unwinds cooperatively.
    /// </summary>
    /// <param name="reason">Why the operation is pausing. Recorded for diagnostics and resource waits.</param>
    public void Pause(SimulationPauseReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var operation = RequireCurrentOperation();
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                operation.ApplyTransition(SimulationOperationState.Paused, pauseReason: reason);
            }

            Notify(operation, SimulationOperationState.Paused);
        }

        HandBackAndPark(operation);
    }

    /// <summary>
    /// Yields the permission baton from the calling operation, giving the scheduler a chance to run
    /// another operation, while remaining immediately <see cref="SimulationOperationState.Runnable"/>.
    /// Must be called from within a running operation body of this scheduler.
    /// </summary>
    public void Yield()
    {
        var operation = RequireCurrentOperation();
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                operation.ApplyTransition(SimulationOperationState.Runnable);
            }

            Notify(operation, SimulationOperationState.Runnable);
        }

        HandBackAndPark(operation);
    }

    /// <summary>
    /// <para>
    /// Blocks the calling operation on <paramref name="resource"/> until another operation signals it
    /// (see <see cref="SignalOne"/>/<see cref="SignalAll"/>). This is the reusable wait primitive the
    /// later <c>Monitor</c>/<c>SemaphoreSlim</c>/wait-handle/synchronous-<c>Task</c>-wait shims are
    /// built on; this overload waits indefinitely (an infinite timeout, no cancellation).
    /// </para>
    /// <para>
    /// The transition is atomic under the scheduler lock: the operation is enqueued onto the
    /// resource's deterministic FIFO wait queue and moved <c>Running -&gt; Paused</c> in one critical
    /// section before the baton is yielded, so a signal delivered by the very next operation cannot be
    /// lost. An operation can be parked on at most one resource at a time (it is paused), which is why
    /// duplicate queue entries are impossible; a resolved waiter is skipped by every subsequent signal,
    /// which is why stale wakeups cannot occur.
    /// </para>
    /// </summary>
    /// <param name="resource">The resource to wait on. Must belong to this scheduler.</param>
    /// <param name="reason">The stable pause reason recorded for diagnostics and the wait-for graph.</param>
    /// <returns>
    /// The outcome of the wait. For this overload that is always <see cref="SimulationWaitOutcome.Signaled"/>
    /// - the wait only ends when the operation is signaled.
    /// </returns>
    public SimulationWaitOutcome WaitOnResource(SimulationResource resource, SimulationPauseReason reason) =>
        WaitOnResourceCore(resource, Timeout.InfiniteTimeSpan, reason, CancellationToken.None);

    /// <summary>
    /// Blocks the calling operation on <paramref name="resource"/> until it is signaled or the
    /// modeled (virtual) timeout elapses, whichever happens first, resolving the race
    /// deterministically. Timeouts are driven purely by Clockwork's virtual time, never by a
    /// real-time timer, so the wait is fully replayable.
    /// <para>
    /// <b>Timeout semantics.</b> A <see cref="TimeSpan.Zero"/> timeout never parks: it resolves
    /// <see cref="SimulationWaitOutcome.TimedOut"/> immediately (a signal cannot arrive synchronously
    /// to the still-running operation). <see cref="Timeout.InfiniteTimeSpan"/> registers no timeout
    /// and behaves exactly like the signal-only overload. A finite, strictly positive timeout
    /// registers a virtual-time deadline; the deadline can only fire while no operation is runnable
    /// (see <see cref="TryAdvanceVirtualTime"/>), so a pending signal at the same instant always wins.
    /// </para>
    /// </summary>
    /// <param name="resource">The resource to wait on. Must belong to this scheduler.</param>
    /// <param name="timeout">
    /// The modeled wait budget: <see cref="TimeSpan.Zero"/> to poll, <see cref="Timeout.InfiniteTimeSpan"/>
    /// to wait forever, or any strictly positive span for a virtual-time deadline.
    /// </param>
    /// <param name="reason">The stable pause reason recorded for diagnostics and the wait-for graph.</param>
    /// <returns>
    /// <see cref="SimulationWaitOutcome.Signaled"/> if signaled first, otherwise
    /// <see cref="SimulationWaitOutcome.TimedOut"/>.
    /// </returns>
    public SimulationWaitOutcome WaitOnResource(SimulationResource resource, TimeSpan timeout, SimulationPauseReason reason) =>
        WaitOnResourceCore(resource, timeout, reason, CancellationToken.None);

    /// <summary>
    /// Blocks the calling operation on <paramref name="resource"/> until it is signaled, the modeled
    /// (virtual) timeout elapses, or <paramref name="cancellationToken"/> is canceled - whichever
    /// happens first - resolving the three-way race deterministically. Cancellation is observed
    /// <b>synchronously</b>: the token's registration runs on the thread that cancels the token, under
    /// the scheduler lock, so there is no thread-pool hop and no <c>CancelAsync</c>. The registration
    /// is always disposed when the wait ends, so no callback leaks past it.
    /// <para>
    /// <b>Race resolution.</b> The waiter carries a single-assignment resolution; the first of
    /// signal/timeout/cancel to claim it under the lock wins and the terminal reason is exactly that
    /// outcome. An already-canceled token resolves <see cref="SimulationWaitOutcome.Canceled"/>
    /// immediately without parking (cancellation takes precedence over a zero timeout). Timeout still
    /// fires only during virtual-time advance, so a signal or cancellation delivered while the
    /// operation set is still making progress always precedes a same-instant timeout.
    /// </para>
    /// </summary>
    /// <param name="resource">The resource to wait on. Must belong to this scheduler.</param>
    /// <param name="timeout">
    /// The modeled wait budget: <see cref="TimeSpan.Zero"/> to poll, <see cref="Timeout.InfiniteTimeSpan"/>
    /// to wait until signaled/canceled, or any strictly positive span for a virtual-time deadline.
    /// </param>
    /// <param name="reason">The stable pause reason recorded for diagnostics and the wait-for graph.</param>
    /// <param name="cancellationToken">A token whose synchronous cancellation ends the wait.</param>
    /// <returns>The deterministic terminal outcome of the wait.</returns>
    public SimulationWaitOutcome WaitOnResource(
        SimulationResource resource,
        TimeSpan timeout,
        SimulationPauseReason reason,
        CancellationToken cancellationToken) =>
        WaitOnResourceCore(resource, timeout, reason, cancellationToken);

    /// <summary>
    /// The shared implementation behind every <c>WaitOnResource</c> overload. Validates ownership and
    /// the timeout, resolves the non-parking fast paths (already-canceled token, then zero timeout),
    /// then atomically enqueues the waiter (with an optional virtual-time timeout and synchronous
    /// cancellation registration) and parks the operation under the scheduler lock before yielding the
    /// baton, so no signal, timeout, or cancellation can be lost.
    /// </summary>
    private SimulationWaitOutcome WaitOnResourceCore(
        SimulationResource resource,
        TimeSpan timeout,
        SimulationPauseReason reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(reason);
        ValidateResourceOwnership(resource);
        var operation = RequireCurrentOperation();

        var infinite = timeout == Timeout.InfiniteTimeSpan;
        if (!infinite && timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "A resource wait timeout must be non-negative, TimeSpan.Zero, or Timeout.InfiniteTimeSpan.");
        }

        // Already-canceled token: resolve without parking. Cancellation takes precedence over a zero
        // timeout so the terminal reason is exactly Canceled.
        if (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
            }

            return SimulationWaitOutcome.Canceled;
        }

        // Zero timeout: never park. A signal cannot arrive synchronously to the running operation, so
        // the deterministic outcome is an immediate timeout.
        if (!infinite && timeout == TimeSpan.Zero)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
            }

            return SimulationWaitOutcome.TimedOut;
        }

        SimulationResourceWaiter waiter;
        CancellationTokenRegistration detachedRegistration = default;
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                waiter = new SimulationResourceWaiter(operation, resource, ++_nextWaiterSequence, reason);
                resource.EnqueueWaiter(waiter);
                operation.Waiter = waiter;
                if (!infinite)
                {
                    waiter.Timeout = _clock.Schedule(timeout, waiter);
                }

                operation.ApplyTransition(SimulationOperationState.Paused, pauseReason: reason);
            }

            Notify(operation, SimulationOperationState.Paused);

            // The publication gate keeps external signal/cancel transitions behind the Paused event.
            // Register can still invoke synchronously on this thread if cancellation arrived after the
            // fast-path check; that callback re-enters the publication gate and publishes Runnable only
            // after Paused. Assign the returned registration under the scheduler gate, or dispose it
            // afterward if a listener reentrantly detached the waiter during Paused publication.
            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(
                    static state =>
                    {
                        var (scheduler, canceledWaiter) = ((SimulationScheduler, SimulationResourceWaiter))state!;
                        scheduler.OnWaiterCanceled(canceledWaiter);
                    },
                    (this, waiter));
                lock (_gate)
                {
                    if (ReferenceEquals(operation.Waiter, waiter))
                    {
                        waiter.CancellationRegistration = registration;
                    }
                    else
                    {
                        detachedRegistration = registration;
                    }
                }
            }
        }

        DisposeRegistration(detachedRegistration);
        HandBackAndPark(operation);

        SimulationWaitOutcome outcome = FinishWait(operation, waiter);
        if (outcome == SimulationWaitOutcome.Signaled)
        {
            lock (_gate)
            {
                if (waiter.RaceReleaseClock is { } releaseClock)
                {
                    _raceTracker.ConsumeRelease(operation, releaseClock);
                }
            }
        }

        return outcome;
    }

    /// <summary>
    /// The synchronous cancellation callback: resolves <paramref name="waiter"/> as
    /// <see cref="SimulationWaitOutcome.Canceled"/> (if it has not already been signaled or timed out),
    /// cancels its virtual-time timeout, removes it from the resource queue, and makes its operation
    /// runnable so it observes the cancellation. Runs on the thread that cancels the token, under the
    /// scheduler lock; never touches the thread pool. The cancellation registration itself is disposed
    /// later in <see cref="FinishWait"/> (never from inside its own callback).
    /// </summary>
    private void OnWaiterCanceled(SimulationResourceWaiter waiter)
    {
        SimulationOperation? runnable = null;
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (!waiter.TryResolve(SimulationWaitOutcome.Canceled))
                {
                    return;
                }

                RecordWaitResolution(waiter, SimulationWaitOutcome.Canceled);
                if (waiter.Timeout is { } timeout)
                {
                    timeout.IsCanceled = true;
                }

                waiter.Resource.RemoveWaiter(waiter);
                if (waiter.Operation.State == SimulationOperationState.Paused)
                {
                    waiter.Operation.ApplyTransition(SimulationOperationState.Runnable);
                    runnable = waiter.Operation;
                }
            }

            if (runnable is not null)
            {
                Notify(runnable, SimulationOperationState.Runnable);
            }
        }
    }

    /// <summary>
    /// Wakes the earliest-enqueued unresolved waiter on <paramref name="resource"/> (deterministic
    /// FIFO order), resolving it as <see cref="SimulationWaitOutcome.Signaled"/> and transitioning its
    /// operation back to <see cref="SimulationOperationState.Runnable"/>. This is the building block a
    /// monitor pulse / semaphore release / auto-reset-event set is composed from. Safe to call from a
    /// running operation body or the controlling thread.
    /// </summary>
    /// <param name="resource">The resource to signal. Must belong to this scheduler.</param>
    /// <returns>The operation that was woken, or <see langword="null"/> if no waiter was pending.</returns>
    public SimulationOperation? SignalOne(SimulationResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ValidateResourceOwnership(resource);

        SimulationOperation? woken = null;
        CancellationTokenRegistration registration = default;
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var next = SelectResourceWaiter(resource);
                if (next is not null && next.TryResolve(SimulationWaitOutcome.Signaled))
                {
                    if (t_currentOperation is { } signaler && ReferenceEquals(signaler.Scheduler, this))
                    {
                        next.RaceReleaseClock = _raceTracker.CaptureRelease(signaler);
                    }

                    RecordWaitResolution(next, SimulationWaitOutcome.Signaled);
                    registration = DetachWaiterRegistrationsUnderLock(next);
                    resource.RemoveWaiter(next);
                    next.Operation.ApplyTransition(SimulationOperationState.Runnable);
                    woken = next.Operation;
                }
            }

            if (woken is not null)
            {
                Notify(woken, SimulationOperationState.Runnable);
            }
        }

        DisposeRegistration(registration);
        return woken;
    }

    /// <summary>
    /// Wakes every unresolved waiter on <paramref name="resource"/>, in deterministic FIFO order,
    /// resolving each as <see cref="SimulationWaitOutcome.Signaled"/> and making its operation
    /// runnable. This is the building block a monitor <c>PulseAll</c> / manual-reset-event set is
    /// composed from.
    /// </summary>
    /// <param name="resource">The resource to signal. Must belong to this scheduler.</param>
    /// <returns>The woken operations, in the deterministic order they were signaled.</returns>
    public IReadOnlyList<SimulationOperation> SignalAll(SimulationResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ValidateResourceOwnership(resource);

        List<SimulationOperation> woken = [];
        SimulationResourceWaiter[] pending;
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                pending = resource.SnapshotPendingWaiters();
            }

            foreach (var waiter in pending)
            {
                SimulationOperation? operation = null;
                CancellationTokenRegistration registration = default;
                lock (_gate)
                {
                    if (waiter.TryResolve(SimulationWaitOutcome.Signaled))
                    {
                        if (t_currentOperation is { } signaler && ReferenceEquals(signaler.Scheduler, this))
                        {
                            waiter.RaceReleaseClock = _raceTracker.CaptureRelease(signaler);
                        }

                        RecordWaitResolution(waiter, SimulationWaitOutcome.Signaled);
                        registration = DetachWaiterRegistrationsUnderLock(waiter);
                        resource.RemoveWaiter(waiter);
                        waiter.Operation.ApplyTransition(SimulationOperationState.Runnable);
                        operation = waiter.Operation;
                    }
                }

                if (operation is not null)
                {
                    woken.Add(operation);
                    Notify(operation, SimulationOperationState.Runnable);
                    DisposeRegistration(registration);
                }
            }
        }

        return woken;
    }

    /// <summary>
    /// Completes a wait once the operation has been granted the baton again: reads the deterministic
    /// resolution assigned while it was parked, disposes any timeout/cancellation registrations (so a
    /// cancellation callback that resolved the waiter without disposing its own registration cannot
    /// leak), detaches the waiter from the resource and the operation, and returns the outcome. The
    /// resolution is always present here because an operation is only ever made runnable again after
    /// its waiter has been resolved.
    /// </summary>
    private SimulationWaitOutcome FinishWait(SimulationOperation operation, SimulationResourceWaiter waiter)
    {
        SimulationWaitOutcome resolution;
        CancellationTokenRegistration registration;
        lock (_gate)
        {
            resolution = waiter.Resolution ?? throw new SimulationSchedulerException(
                string.Create(CultureInfo.InvariantCulture, $"Controlled operation {operation.Id} resumed from a resource wait with no resolution recorded."));
            registration = DetachWaiterRegistrationsUnderLock(waiter);
            waiter.Resource.RemoveWaiter(waiter);
            if (ReferenceEquals(operation.Waiter, waiter))
            {
                operation.Waiter = null;
            }
        }

        DisposeRegistration(registration);
        return resolution;
    }

    /// <summary>
    /// Detaches a paused operation's waiter (if any) during cancellation/teardown: resolves it as
    /// <see cref="SimulationWaitOutcome.Canceled"/> so a later signal never tries to wake a terminal
    /// operation, cancels its timeout, captures its cancellation registration for disposal after the
    /// scheduler lock is released, removes it from the resource queue, and clears the operation's waiter
    /// slot. Caller must hold the lock.
    /// </summary>
    private static CancellationTokenRegistration DetachWaiterUnderLock(SimulationOperation operation)
    {
        var waiter = operation.Waiter;
        if (waiter is null)
        {
            return default;
        }

        waiter.TryResolve(SimulationWaitOutcome.Canceled);
        var registration = DetachWaiterRegistrationsUnderLock(waiter);
        waiter.Resource.RemoveWaiter(waiter);
        operation.Waiter = null;
        return registration;
    }

    /// <summary>
    /// Cancels and detaches the timeout and cancellation registrations attached to a waiter. The caller
    /// disposes the returned cancellation registration only after releasing the scheduler lock, since
    /// disposal can wait for an in-flight callback that is itself waiting to acquire that lock.
    /// </summary>
    private static CancellationTokenRegistration DetachWaiterRegistrationsUnderLock(SimulationResourceWaiter waiter)
    {
        if (waiter.Timeout is { } timeout)
        {
            timeout.IsCanceled = true;
            waiter.Timeout = null;
        }

        var registration = waiter.CancellationRegistration;
        waiter.CancellationRegistration = default;
        return registration;
    }

    private void DisposeRegistrations(List<CancellationTokenRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            DisposeRegistration(registration);
        }
    }

    private void DisposeRegistration(CancellationTokenRegistration registration)
    {
        if (registration.Equals(default(CancellationTokenRegistration)))
        {
            return;
        }

        if (Monitor.IsEntered(_transitionPublicationGate))
        {
            (_deferredRegistrationDisposals ??= []).Add(registration);
            return;
        }

        registration.Dispose();
    }

    private void ValidateResourceOwnership(SimulationResource resource)
    {
        if (!ReferenceEquals(resource.Scheduler, this))
        {
            throw new SimulationSchedulerException("The resource belongs to a different scheduler.");
        }
    }

    /// <summary>
    /// Captures a deterministic snapshot of every registered operation's identity and status, in
    /// stable <see cref="SimulationOperation.Id"/> order. Intended for diagnostics and for folding
    /// controlled-operation state into higher-level pending-work summaries.
    /// </summary>
    /// <returns>An ordered, immutable list of operation statuses.</returns>
    public IReadOnlyList<SimulationOperationStatus> CaptureStatus()
    {
        lock (_gate)
        {
            var result = new List<SimulationOperationStatus>(_operations.Count);
            foreach (var operation in _operations)
            {
                result.Add(new SimulationOperationStatus(
                    operation.Id,
                    operation.ParentId,
                    operation.State,
                    operation.PauseReason,
                    operation.LogicalExecutionId.Value,
                    operation.Node?.Address,
                    operation.WorkDescription));
            }

            return result;
        }
    }

    /// <summary>
    /// Gets the number of operations that are not in a terminal state (Created/Runnable/Running/Paused).
    /// </summary>
    public int PendingOperationCount
    {
        get
        {
            lock (_gate)
            {
                return _activeOperations.Count;
            }
        }
    }

    internal int ActiveOperationCount
    {
        get
        {
            lock (_gate)
            {
                return _activeOperations.Count;
            }
        }
    }

    internal int RunnableOperationCount => CountActiveOperations(
        static operation => operation.State == SimulationOperationState.Runnable);

    internal int WaitingOperationCount => CountActiveOperations(
        static operation => operation.State is SimulationOperationState.Created or SimulationOperationState.Paused);

    internal bool IsIdle => PendingOperationCount == 0;

    /// <summary>
    /// Gets the current modeled (virtual) time, as a monotonic offset from simulation start. Advanced
    /// only by <see cref="TryAdvanceVirtualTime"/>; never tied to wall-clock time.
    /// </summary>
    public TimeSpan VirtualTime
    {
        get
        {
            lock (_gate)
            {
                return _clock.Now;
            }
        }
    }

    /// <summary>
    /// Gets a deterministic snapshot of the timed waits currently pending in virtual time, ordered by
    /// due time then registration sequence, for diagnostics and deadlock reports.
    /// </summary>
    /// <returns>An ordered, immutable list of pending timeout descriptors.</returns>
    public IReadOnlyList<SimulationPendingResourceTimeoutInfo> CapturePendingTimeouts()
    {
        lock (_gate)
        {
            return _clock.SnapshotPending();
        }
    }

    /// <summary>
    /// Analyzes the current wait-for graph and classifies whether outstanding work can still make
    /// progress, distinguishing a genuine resource-ownership deadlock from a paused-until-time state,
    /// an externally-completable wait, an idle-with-pending-work state, or quiescence. Every detected
    /// cycle is reported deterministically (operations and resources by stable id/name, owners, waiter
    /// order, and originating pause reason), so a stuck simulation explains itself instead of hanging.
    /// <para>
    /// Only <b>indefinite</b> waits contribute deadlock edges: a wait with a pending virtual-time
    /// timeout can always be broken by advancing modeled time, so it never counts as a true cycle.
    /// </para>
    /// </summary>
    /// <returns>A deterministic deadlock/liveness report.</returns>
    public SimulationDeadlockReport DetectDeadlock()
    {
        lock (_gate)
        {
            var edges = new Dictionary<SimulationOperationId, DeadlockEdge>();
            var runnable = 0;
            var blocked = 0;
            var nonTerminal = 0;
            foreach (var operation in _activeOperations)
            {
                nonTerminal++;

                if (operation.State == SimulationOperationState.Runnable)
                {
                    runnable++;
                }

                if (operation.State != SimulationOperationState.Paused || operation.Waiter is not { } waiter)
                {
                    continue;
                }

                blocked++;

                // Only an indefinite wait (no pending timeout) can form a true deadlock: a timed wait
                // is always broken by advancing virtual time.
                if (waiter.Timeout is not null)
                {
                    continue;
                }

                if (waiter.Resource.Owner is { } owner && !owner.IsTerminal)
                {
                    edges[operation.Id] = new DeadlockEdge(owner.Id, waiter);
                }
            }

            var cycles = FindWaitForCyclesUnderLock(edges);
            var pendingTimeouts = _clock.PendingCount;
            var liveness = ClassifyLiveness(nonTerminal, runnable, cycles.Count, pendingTimeouts);
            return new SimulationDeadlockReport(liveness, cycles, runnable, blocked, pendingTimeouts);
        }
    }

    /// <summary>
    /// Produces a deterministic, multi-line human-readable diagnostic that folds the operation-status
    /// snapshot (<see cref="CaptureStatus"/>) together with the <see cref="DetectDeadlock"/> liveness
    /// classification and any wait-for cycles. This is the summary intended to be surfaced by
    /// higher-level execution diagnostics when a simulation appears stuck.
    /// </summary>
    /// <returns>A stable, replayable multi-line description of scheduler liveness.</returns>
    public string DescribeLiveness()
    {
        SimulationDeadlockReport report;
        List<(SimulationOperationId Id, SimulationOperationState State, string Work, SimulationPauseReason? Reason)> operations;
        TimeSpan now;
        lock (_gate)
        {
            report = DetectDeadlock();
            now = _clock.Now;
            operations = new List<(SimulationOperationId, SimulationOperationState, string, SimulationPauseReason?)>(_operations.Count);
            foreach (var operation in _operations)
            {
                operations.Add((operation.Id, operation.State, operation.WorkDescription, operation.PauseReason));
            }
        }

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Liveness: {report.Liveness} (virtual-time {now}, runnable {report.RunnableCount}, blocked {report.BlockedCount}, pending-timeouts {report.PendingTimeoutCount})");
        foreach (var (id, state, work, reason) in operations)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}  {id} [{state}] {work}");
            if (reason is not null && state == SimulationOperationState.Paused)
            {
                builder.Append(CultureInfo.InvariantCulture, $" waiting: {reason}");
            }
        }

        for (var i = 0; i < report.Cycles.Count; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}  Deadlock cycle {i + 1}:");
            foreach (var entry in report.Cycles[i].Entries)
            {
                builder.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}    {entry.OperationId} '{entry.WorkDescription}' waits on {entry.ResourceId} '{entry.ResourceName}' (seq {entry.EnqueueSequence}, {entry.Reason}) owned by {entry.OwnerId}");
            }
        }

        return builder.ToString();
    }

    private List<SimulationWaitCycle> FindWaitForCyclesUnderLock(Dictionary<SimulationOperationId, DeadlockEdge> edges)
    {
        var cycles = new List<SimulationWaitCycle>();
        var globallyVisited = new HashSet<SimulationOperationId>();

        // The wait-for graph is functional here (each paused operation waits on exactly one resource,
        // hence has at most one successor), so following the single successor chain from each start
        // node detects every cycle deterministically. _activeOperations is in id order, so starts are ordered.
        foreach (var operation in _activeOperations)
        {
            var start = operation.Id;
            if (!edges.ContainsKey(start) || globallyVisited.Contains(start))
            {
                continue;
            }

            var path = new List<SimulationOperationId>();
            var indexInPath = new Dictionary<SimulationOperationId, int>();
            var current = start;
            while (true)
            {
                if (globallyVisited.Contains(current))
                {
                    break;
                }

                if (indexInPath.TryGetValue(current, out var index))
                {
                    cycles.Add(BuildCycle(path.GetRange(index, path.Count - index), edges));
                    break;
                }

                if (!edges.TryGetValue(current, out var edge))
                {
                    break;
                }

                indexInPath[current] = path.Count;
                path.Add(current);
                current = edge.OwnerId;
            }

            foreach (var id in path)
            {
                globallyVisited.Add(id);
            }
        }

        // Order cycles deterministically by their (already smallest-first) leading operation id.
        cycles.Sort(static (a, b) => a.Entries[0].OperationId.CompareTo(b.Entries[0].OperationId));
        return cycles;
    }

    private SimulationWaitCycle BuildCycle(List<SimulationOperationId> cycleIds, Dictionary<SimulationOperationId, DeadlockEdge> edges)
    {
        // Rotate so the cycle starts at its smallest operation id for a stable representation.
        var minIndex = 0;
        for (var i = 1; i < cycleIds.Count; i++)
        {
            if (cycleIds[i] < cycleIds[minIndex])
            {
                minIndex = i;
            }
        }

        var entries = new List<SimulationWaitCycleEntry>(cycleIds.Count);
        for (var i = 0; i < cycleIds.Count; i++)
        {
            var id = cycleIds[(minIndex + i) % cycleIds.Count];
            var edge = edges[id];
            var waiter = edge.Waiter;
            entries.Add(new SimulationWaitCycleEntry(
                id,
                GetOperation(id).WorkDescription,
                waiter.Resource.Id,
                waiter.Resource.Name,
                edge.OwnerId,
                waiter.EnqueueSequence,
                waiter.Reason.ToString()));
        }

        return new SimulationWaitCycle(entries);
    }

    private static SimulationLivenessState ClassifyLiveness(int nonTerminal, int runnable, int cycleCount, int pendingTimeouts)
    {
        if (nonTerminal == 0)
        {
            return SimulationLivenessState.Quiescent;
        }

        if (runnable > 0)
        {
            return SimulationLivenessState.Progressing;
        }

        // A live deadline means the scheduler can still advance modeled time. Preserve any cycle in
        // the report for diagnostics, but do not classify it as terminal until all finite escape paths
        // have been exhausted.
        if (pendingTimeouts > 0)
        {
            return SimulationLivenessState.PausedUntilTime;
        }

        if (cycleCount > 0)
        {
            return SimulationLivenessState.Deadlocked;
        }

        return SimulationLivenessState.ExternallyCompletable;
    }

    /// <summary>A single wait-for edge used during deadlock analysis: the owner to follow and the waiter that drew it.</summary>
    private readonly record struct DeadlockEdge(SimulationOperationId OwnerId, SimulationResourceWaiter Waiter);

    /// <summary>
    /// Tears the scheduler down: cancels every non-terminal operation, cooperatively unwinds parked
    /// operation stacks, joins every worker thread (bounded by <see cref="ThreadJoinTimeout"/>), and
    /// releases all wait handles. No operation is aborted unsafely; no thread is left stranded.
    /// Idempotent.
    /// </summary>
    public void Dispose()
    {
        List<SimulationOperation> victims;
        List<CancellationTokenRegistration> registrations;
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                victims = new List<SimulationOperation>();
                registrations = new List<CancellationTokenRegistration>();
                foreach (var operation in _activeOperations)
                {
                    operation.RequestTermination();

                    // Detach any resource wait first so a resolved-Canceled waiter is removed from its
                    // resource queue and never wakes a terminal operation.
                    registrations.Add(DetachWaiterUnderLock(operation));

                    // Force every non-terminal operation to Canceled. Running is not expected here
                    // because Dispose must not race an in-flight step; the Created/Runnable/Paused ->
                    // Canceled and Running -> Canceled edges are all legal, so a single transition call
                    // handles every non-terminal source state.
                    operation.ApplyTransition(SimulationOperationState.Canceled);
                    victims.Add(operation);
                }

                _activeOperations.Clear();

                // Drop any pending virtual-time timeouts; their waiters were just detached and canceled.
                _clock.Clear();
                _readinessWaits.Clear();
            }

            foreach (var victim in victims)
            {
                Notify(victim, SimulationOperationState.Canceled);
            }
        }

        List<OperationWorker> workers = BeginWorkerPoolShutdown();
        DisposeRegistrations(registrations);
        foreach (var victim in victims)
        {
            if (victim.Thread is not null)
            {
                UnwindParkedOperation(victim);
            }

            // As for Cancel, a victim's race tracking ends once its stack has detached from the worker.
            lock (_gate)
            {
                EndRaceTracking(victim);
            }
        }

        foreach (var operation in SnapshotOperations())
        {
            operation.DisposeSignals();
        }

        JoinWorkers(workers);
        _handback.Dispose();
    }

    private SimulationOperation? SelectRunnable()
    {
        // Custom and instrumented strategies receive a stable snapshot which they may retain.
        List<SimulationOperation>? runnable = null;
        for (var index = 0; index < _activeOperations.Count; index++)
        {
            SimulationOperation operation = _activeOperations[index];
            if (operation.State != SimulationOperationState.Runnable || !IsEligibleUnderLock(operation))
            {
                continue;
            }

            (runnable ??= new List<SimulationOperation>()).Add(operation);
        }

        if (runnable is null)
        {
            return null;
        }

        SimulationOperation chosen;
        if (runnable.Count == 1)
        {
            // No real choice - do not consult the strategy's hidden state or record a decision, so
            // single-candidate steps never perturb a seeded/replayed schedule.
            chosen = runnable[0];
        }
        else
        {
            chosen = _strategy.ChooseNext(new SimulationSchedulingContext(runnable, _lastSelected));
            RecordSelection(chosen, runnable);
        }

        _lastSelected = chosen.Id;
        return chosen;
    }

    private bool CanSelectRoundRobinWithoutSnapshot() =>
        _strategy is RoundRobinSchedulingStrategy && _decisionLog is null && _replayValidator is null;

    private SimulationOperation? FindRoundRobinRunnableUnderLock()
    {
        SimulationOperation? wrapTarget = null;
        for (var index = 0; index < _activeOperations.Count; index++)
        {
            SimulationOperation operation = _activeOperations[index];
            if (operation.State != SimulationOperationState.Runnable || !IsEligibleUnderLock(operation))
            {
                continue;
            }

            wrapTarget ??= operation;
            if (operation.Id > _lastSelected)
            {
                return operation;
            }
        }

        return wrapTarget;
    }

    private bool IsEligibleUnderLock(SimulationOperation operation) =>
        operation.Node is not { } node || !_suspendedNodes.Contains(node.Address);

    private sealed class ScheduledOperationRegistration : IDisposable
    {
        private readonly object _gate = new();
        private readonly SimulationScheduler _scheduler;
        private SimulationOperation? _operation;
        private ISimulationTimer? _timer;
        private int _disposed;

        public ScheduledOperationRegistration(
            SimulationScheduler scheduler,
            SimulationOperation operation,
            ISimulationTimer? timer)
        {
            _scheduler = scheduler;
            _operation = operation;
            _timer = timer;
        }

        public ScheduledOperationRegistration(SimulationScheduler scheduler) => _scheduler = scheduler;

        public void SetTimer(ISimulationTimer timer)
        {
            lock (_gate)
            {
                _timer = timer;
                if (_disposed != 0)
                {
                    timer.Cancel();
                }
            }
        }

        public void Schedule(Func<SimulationOperation> schedule)
        {
            lock (_gate)
            {
                if (_disposed != 0)
                {
                    return;
                }

                _operation = schedule();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (_gate)
            {
                _timer?.Cancel();
                if (_operation is { IsTerminal: false, State: not SimulationOperationState.Running } operation)
                {
                    _scheduler.Cancel(operation);
                }
            }
        }
    }

    /// <summary>
    /// Records the scheduling choice among two-or-more candidates as a
    /// <see cref="SimulationDecisionKind.SchedulingOrder"/> decision and/or validates it against a
    /// recorded run. Called under <see cref="_gate"/> on the controlling thread, so the runtime
    /// identity is taken directly from <see cref="_runtime"/> rather than the ambient context (the
    /// controlling thread has no operation scope installed).
    /// </summary>
    private void RecordSelection(SimulationOperation chosen, List<SimulationOperation> runnable)
    {
        if (_decisionLog is null && _replayValidator is null)
        {
            return;
        }

        var request = new SimulationDecisionRequest(
            SimulationSeedDomain.Scheduler,
            SimulationDecisionKind.SchedulingOrder,
            GetDecisionSourceId(),
            FormatCandidateIds(runnable),
            FormatOperationId(chosen.Id),
            _runtime.Id,
            NodeId: null,
            SimulationLogicalExecutionId.None);

        var record = _decisionLog is not null
            ? _decisionLog.Record(request)
            : new SimulationDecisionRecord(
                new SimulationDecisionId(_nextReplayDecisionId++),
                request.Domain,
                request.Kind,
                request.SourceId,
                request.InputMetadata,
                request.SelectedResult,
                request.RuntimeId,
                request.NodeId,
                request.LogicalExecutionId);

        _replayValidator?.Validate(record);
    }

    private SimulationResourceWaiter? SelectResourceWaiter(SimulationResource resource)
    {
        SimulationResourceWaiter[] pending = resource.SnapshotPendingWaiters();
        if (pending.Length <= 1)
        {
            return pending.Length == 0 ? null : pending[0];
        }

        var waiterInfos = new SimulationResourceWaiterInfo[pending.Length];
        for (var index = 0; index < pending.Length; index++)
        {
            waiterInfos[index] = pending[index].ToInfo();
        }

        var selectedIndex = _strategy is ISimulationSchedulingStrategyRuntimeHooks hooks
            ? hooks.ChooseResourceWaiter(waiterInfos)
            : 0;

        SimulationResourceWaiter selected = pending[selectedIndex];
        RecordResourceWinner(selected, pending);
        return selected;
    }

    private void RecordResourceWinner(
        SimulationResourceWaiter selected,
        SimulationResourceWaiter[] pending)
    {
        if (_decisionLog is null && _replayValidator is null)
        {
            return;
        }

        var candidateIds = new string[pending.Length];
        for (var index = 0; index < pending.Length; index++)
        {
            candidateIds[index] = FormatOperationId(pending[index].Operation.Id);
        }

        var request = new SimulationDecisionRequest(
            SimulationSeedDomain.Scheduler,
            SimulationDecisionKind.ResourceWinner,
            GetDecisionSourceId(),
            string.Create(
                CultureInfo.InvariantCulture,
                $"resource={selected.Resource.Id.Value};waiters={string.Join(",", candidateIds)}"),
            FormatOperationId(selected.Operation.Id),
            _runtime.Id,
            selected.Operation.Node?.Address,
            selected.Operation.LogicalExecutionId);

        var record = _decisionLog is not null
            ? _decisionLog.Record(request)
            : new SimulationDecisionRecord(
                new SimulationDecisionId(_nextReplayDecisionId++),
                request.Domain,
                request.Kind,
                request.SourceId,
                request.InputMetadata,
                request.SelectedResult,
                request.RuntimeId,
                request.NodeId,
                request.LogicalExecutionId);

        _replayValidator?.Validate(record);
    }

    private string GetDecisionSourceId() =>
        (_strategy as ISimulationSchedulingStrategyRuntimeHooks)?.DecisionSourceId ?? _strategy.Name;

    private void RecordWaitResolution(SimulationResourceWaiter waiter, SimulationWaitOutcome outcome)
    {
        if (_decisionLog is null && _replayValidator is null)
        {
            return;
        }

        var request = new SimulationDecisionRequest(
            SimulationSeedDomain.Scheduler,
            SimulationDecisionKind.Choice,
            "resource-wait-resolution",
            string.Create(
                CultureInfo.InvariantCulture,
                $"operation={waiter.Operation.Id.Value};resource={waiter.Resource.Id.Value};outcomes=Signaled,TimedOut,Canceled"),
            outcome.ToString(),
            _runtime.Id,
            waiter.Operation.Node?.Address,
            waiter.Operation.LogicalExecutionId);

        var record = _decisionLog is not null
            ? _decisionLog.Record(request)
            : new SimulationDecisionRecord(
                new SimulationDecisionId(_nextReplayDecisionId++),
                request.Domain,
                request.Kind,
                request.SourceId,
                request.InputMetadata,
                request.SelectedResult,
                request.RuntimeId,
                request.NodeId,
                request.LogicalExecutionId);

        _replayValidator?.Validate(record);
    }

    private static string FormatCandidateIds(List<SimulationOperation> runnable)
    {
        var length = runnable.Count - 1;
        Span<char> buffer = stackalloc char[20];
        for (var index = 0; index < runnable.Count; index++)
        {
            bool formatted = runnable[index].Id.Value.TryFormat(
                buffer,
                out var written,
                provider: CultureInfo.InvariantCulture);
            Debug.Assert(formatted);
            length += written;
        }

        return string.Create(
            length,
            runnable,
            static (destination, operations) =>
            {
                var offset = 0;
                for (var index = 0; index < operations.Count; index++)
                {
                    if (index > 0)
                    {
                        destination[offset++] = ',';
                    }

                    bool formatted = operations[index].Id.Value.TryFormat(
                        destination[offset..],
                        out var written,
                        provider: CultureInfo.InvariantCulture);
                    Debug.Assert(formatted);
                    offset += written;
                }
            });
    }

    private static string FormatOperationId(SimulationOperationId id) =>
        id.Value.ToString(CultureInfo.InvariantCulture);

    private bool EnsureThreadStarted(SimulationOperation operation)
    {
        if (operation.Thread is not null)
        {
            return false;
        }

        OperationWorker worker;
        lock (_workerGate)
        {
            ObjectDisposedException.ThrowIf(_workerPoolDisposed, this);

            if (!_idleWorkers.TryDequeue(out worker!))
            {
                worker = new OperationWorker(this, ++_nextWorkerId);
                _workers.Add(worker);
                _createdWorkerThreadCount++;
                worker.Start();
            }

            worker.Assign(operation);
        }

        return true;
    }

    private void RunWorkerOperation(OperationWorker worker, SimulationOperation operation)
    {
        var handBackController = false;
        try
        {
            // Assignment is the first permission grant. Later grants resume this same stack through
            // the operation's permission signal.
            RunBody(operation);
            handBackController = true;
        }
        catch (SimulationOperationAbortSignal)
        {
            // Cancellation or teardown owns the terminal transition.
        }
        finally
        {
            ReleaseWorker(worker, operation);
            if (handBackController)
            {
                _handback.Release();
            }
        }
    }

    private void RunBody(SimulationOperation operation)
    {
        try
        {
            var context = operation.CapturedContext;
            if (context is not null)
            {
                ExecutionContext.Run(context, s_runBodyInCapturedContext, operation);
            }
            else
            {
                RunBodyScoped(operation);
            }

            HandOffTerminal(operation, SimulationOperationState.Completed, terminalException: null);
        }
        catch (SimulationOperationAbortSignal)
        {
            // Let the worker loop distinguish cancellation/teardown unwinding from a terminal body
            // return, since no controlling thread is waiting for a terminal handback in this path.
            throw;
        }
        catch (Exception ex)
        {
            // Any exception escaping the body is unhandled, including OperationCanceledException:
            // a body reaching here has no task to capture it, as with a .NET Thread body, thread-pool
            // item, timer callback, or synchronization-context callback, where it crashes the process.
            // Task-backed shims (Task.Run, TaskFactory.StartNew, continuations, async methods)
            // capture their delegate's exceptions into the task, so their operations complete.
            HandOffTerminal(operation, SimulationOperationState.Faulted, ex);
        }
    }

    private void RunBodyScoped(SimulationOperation operation)
    {
        var previous = t_currentOperation;
        t_currentOperation = operation;
        try
        {
            using (SimulationExecutionContext.EnterRuntime(_runtime))
            using (operation.Node is { } node ? SimulationExecutionContext.EnterNode(node) : null)
            using (SimulationExecutionContext.EnterLogicalExecution(operation.LogicalExecutionId))
            {
                Threading.SimulationSynchronizationFlow.RunAsStrand(
                    -operation.Id.Value,
                    operation.InvokeBody);
            }
        }
        finally
        {
            t_currentOperation = previous;
        }
    }

    /// <summary>
    /// Hands control back to the waiting controlling thread and parks the operation's physical thread
    /// until it is granted the baton again (or torn down). Called from a running operation's own
    /// thread after it has transitioned itself out of <see cref="SimulationOperationState.Running"/>.
    /// </summary>
    private void HandBackAndPark(SimulationOperation operation)
    {
        _handback.Release();
        operation.WaitForPermission();
        // Resumed: the scheduler transitioned us back to Running before granting the baton.
    }

    private void HandOffTerminal(SimulationOperation operation, SimulationOperationState terminal, Exception? terminalException)
    {
        using (EnterTransitionPublicationScope())
        {
            lock (_gate)
            {
                // Only apply if a controlling thread is still waiting for this operation (it holds the
                // baton). If teardown already forced a terminal state, skip.
                if (operation.IsTerminal)
                {
                    return;
                }

                EndRaceTracking(operation);
                operation.ApplyTransition(terminal, terminalException: terminalException);
                _activeOperations.Remove(operation);
                _pendingTerminalNotification = operation;
            }
        }
    }

    private void FinalizeTerminal(SimulationOperation operation)
    {
        // The worker hands control back only after its stack has unwound and it has detached this
        // operation, so execution state can be released without another per-operation handshake.
        operation.ReleaseExecutionState();
        operation.DisposeSignals();
        using (EnterTransitionPublicationScope(waitForPendingTerminal: false))
        {
            if (!ReferenceEquals(_pendingTerminalNotification, operation))
            {
                throw new SimulationSchedulerException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Controlled operation {operation.Id} reached {operation.State} without its terminal listener notification being pending."));
            }

            _pendingTerminalNotification = null;
            try
            {
                Notify(operation, operation.State);
            }
            finally
            {
                Monitor.PulseAll(_transitionPublicationGate);
            }
        }
    }

    private TransitionPublicationScope EnterTransitionPublicationScope(bool waitForPendingTerminal = true)
    {
        Monitor.Enter(_transitionPublicationGate);
        try
        {
            if (waitForPendingTerminal)
            {
                WaitForPendingTerminalNotificationUnderLock();
            }

            _transitionPublicationDepth++;
            return new TransitionPublicationScope(this);
        }
        catch
        {
            Monitor.Exit(_transitionPublicationGate);
            throw;
        }
    }

    private void ExitTransitionPublicationScope()
    {
        List<CancellationTokenRegistration>? deferred = null;
        _transitionPublicationDepth--;
        if (_transitionPublicationDepth == 0 && _deferredRegistrationDisposals is { Count: > 0 })
        {
            deferred = _deferredRegistrationDisposals;
            _deferredRegistrationDisposals = null;
        }

        Monitor.Exit(_transitionPublicationGate);
        if (deferred is not null)
        {
            foreach (var registration in deferred)
            {
                registration.Dispose();
            }
        }
    }

    private void WaitForPendingTerminalNotificationUnderLock()
    {
        while (_pendingTerminalNotification is not null)
        {
            Monitor.Wait(_transitionPublicationGate);
        }
    }

    private readonly ref struct TransitionPublicationScope(SimulationScheduler scheduler)
    {
        public void Dispose() => scheduler.ExitTransitionPublicationScope();
    }

    private static void UnwindParkedOperation(SimulationOperation operation)
    {
        if (operation.Thread is null)
        {
            return;
        }

        // Unpark the operation; because termination was requested, its next WaitForPermission throws
        // the internal unwind signal and the body stack unwinds cooperatively. The worker itself may
        // then be reused, so wait for detachment rather than thread exit.
        operation.GrantPermission();
        WaitForWorkerRelease(operation);
    }

    private static void WaitForWorkerRelease(SimulationOperation operation)
    {
        if (!operation.WaitForWorkerRelease(ThreadJoinTimeout))
        {
            throw new SimulationSchedulerException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Controlled operation {operation.Id} did not unwind from its worker within {ThreadJoinTimeout.TotalSeconds:0}s. Its body is likely stuck in a loop that never parks - this is a system-under-test bug, not a kernel one."));
        }
    }

    private void ReleaseWorker(OperationWorker worker, SimulationOperation operation)
    {
        lock (_workerGate)
        {
            if (_workerPoolDisposed || _idleWorkers.Count >= MaximumIdleWorkerCount)
            {
                worker.RequestStop();
            }
            else
            {
                _idleWorkers.Enqueue(worker);
            }

            operation.ReleaseWorkerThread(worker.Thread);
        }
    }

    private List<OperationWorker> BeginWorkerPoolShutdown()
    {
        lock (_workerGate)
        {
            _workerPoolDisposed = true;
            List<OperationWorker> workers = [.. _workers];
            _idleWorkers.Clear();
            foreach (OperationWorker worker in workers)
            {
                worker.RequestStop();
            }

            return workers;
        }
    }

    private static void JoinWorkers(List<OperationWorker> workers)
    {
        foreach (OperationWorker worker in workers)
        {
            if (!worker.Join(ThreadJoinTimeout))
            {
                throw new SimulationSchedulerException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Simulation worker {worker.Id} did not exit within {ThreadJoinTimeout.TotalSeconds:0}s of scheduler disposal."));
            }
        }
    }

    private void OnWorkerExited(OperationWorker worker)
    {
        lock (_workerGate)
        {
            _workers.Remove(worker);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1001:Types that own disposable fields should be disposable",
        Justification = "The worker thread owns and disposes its assignment signal when its run loop exits; external disposal could race that loop.")]
    private sealed class OperationWorker
    {
        private readonly SimulationScheduler _scheduler;
        private readonly SemaphoreSlim _assignmentAvailable = new(0, 1);
        private SimulationOperation? _operation;
        private int _stopRequested;

        public OperationWorker(SimulationScheduler scheduler, long id)
        {
            _scheduler = scheduler;
            Id = id;
            Thread = new Thread(Run)
            {
                IsBackground = true,
                Name = string.Create(CultureInfo.InvariantCulture, $"SimulationOperationWorker-{id}"),
            };
        }

        public long Id { get; }

        public Thread Thread { get; }

        public void Start() => Thread.Start();

        public void Assign(SimulationOperation operation)
        {
            if (Interlocked.CompareExchange(ref _operation, operation, null) is not null)
            {
                throw new SimulationSchedulerException($"Simulation worker {Id} is already assigned.");
            }

            operation.AssignWorkerThread(Thread);
            _assignmentAvailable.Release();
        }

        public void RequestStop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
            {
                _assignmentAvailable.Release();
            }
        }

        public bool Join(TimeSpan timeout) => Thread.Join(timeout);

        private void Run()
        {
            try
            {
                while (true)
                {
                    _assignmentAvailable.Wait();
                    SimulationOperation? operation = Interlocked.Exchange(ref _operation, null);
                    if (operation is not null)
                    {
                        _scheduler.RunWorkerOperation(this, operation);
                    }

                    if (Volatile.Read(ref _stopRequested) != 0)
                    {
                        return;
                    }
                }
            }
            finally
            {
                _assignmentAvailable.Dispose();
                _scheduler.OnWorkerExited(this);
            }
        }
    }

    private SimulationOperation RequireCurrentOperation()
    {
        var operation = t_currentOperation;
        if (operation is null || !ReferenceEquals(operation.Scheduler, this))
        {
            throw new SimulationSchedulerException(
                "Pause/Yield may only be called from within a running controlled operation of this scheduler.");
        }

        return operation;
    }

    private void ValidateOwnership(SimulationOperation operation)
    {
        if (!ReferenceEquals(operation.Scheduler, this))
        {
            throw new SimulationSchedulerException("The operation belongs to a different scheduler.");
        }
    }

    private SimulationOperation GetOperation(SimulationOperationId id)
    {
        var index = checked((int)(id.Value - 1));
        if ((uint)index >= (uint)_operations.Count || _operations[index].Id != id)
        {
            throw new SimulationSchedulerException($"Unknown controlled operation id '{id}'.");
        }

        return _operations[index];
    }

    private SimulationOperation[] SnapshotOperations()
    {
        lock (_gate)
        {
            var array = new SimulationOperation[_operations.Count];
            _operations.CopyTo(array, 0);
            return array;
        }
    }

    private int CountActiveOperations(Func<SimulationOperation, bool> predicate)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var operation in _activeOperations)
            {
                if (predicate(operation))
                {
                    count++;
                }
            }

            return count;
        }
    }

    private void Notify(SimulationOperation operation, SimulationOperationState state) =>
        _listener?.OnStateChanged(operation, state);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfNodeDiscardedUnderLock(SimulationNodeIdentity? node)
    {
        if (node is { } identity && _discardedNodes.Contains(identity.Address))
        {
            throw new ObjectDisposedException(
                identity.Address,
                "The simulation node discarded its pending work and cannot accept new work.");
        }
    }

    private bool HasRunningOperationUnderLock(SimulationNodeIdentity node) =>
        _activeOperations.Any(
            operation => operation.Node == node
                && operation.State == SimulationOperationState.Running);

    private sealed class ReadinessWait : ISimulationWorkRegistration
    {
        private readonly SimulationScheduler _scheduler;
        private readonly Func<bool> _isReady;
        private ExceptionDispatchInfo? _failure;
        private int _canceled;

        private ReadinessWait(
            SimulationScheduler scheduler,
            Func<bool> isReady,
            SimulationOperation operation,
            bool isSynchronous,
            bool isIndefinite,
            string? apiName)
        {
            _scheduler = scheduler;
            _isReady = isReady;
            Operation = operation;
            IsSynchronous = isSynchronous;
            IsIndefinite = isIndefinite;
            ApiName = apiName;
        }

        public SimulationOperation Operation { get; }

        public bool IsSynchronous { get; }

        public bool IsIndefinite { get; }

        public string? ApiName { get; }

        public bool IsCanceled => Volatile.Read(ref _canceled) != 0;

        public static ReadinessWait ForContinuation(
            SimulationScheduler scheduler,
            Func<bool> isReady,
            SimulationOperation operation) =>
            new(scheduler, isReady, operation, isSynchronous: false, isIndefinite: false, apiName: null);

        public static ReadinessWait ForSynchronousWait(
            SimulationScheduler scheduler,
            Func<bool> isReady,
            SimulationOperation operation,
            string apiName) =>
            new(scheduler, isReady, operation, isSynchronous: true, isIndefinite: false, apiName);

        public static ReadinessWait ForIndefiniteWait(
            SimulationScheduler scheduler,
            SimulationOperation operation) =>
            new(scheduler, static () => false, operation, isSynchronous: true, isIndefinite: true, apiName: null);

        public bool IsReady() => !IsCanceled && _isReady();

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _canceled, 1) == 0)
            {
                _scheduler.CancelReadinessWait(this);
            }
        }

        public void Fail(Exception exception) => _failure = ExceptionDispatchInfo.Capture(exception);

        public void ThrowIfFailed() => _failure?.Throw();
    }

    private sealed class CompletedSimulationTimer : ISimulationTimer
    {
        public static CompletedSimulationTimer Instance { get; } = new();

        public bool IsElapsed => true;

        public void Cancel()
        {
        }
    }

    private sealed class ControlScope(SimulationScheduler scheduler, int thread) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.CompareExchange(ref scheduler._controlThreadId, 0, thread);
            }
        }
    }

    private sealed class NestedControlScope : IDisposable
    {
        public static NestedControlScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
