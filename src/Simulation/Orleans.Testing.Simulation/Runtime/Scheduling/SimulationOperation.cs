using System.Diagnostics;
using System.Globalization;
using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Runtime.Scheduling;

/// <summary>
/// <para>
/// One independently-schedulable strand of controlled work owned by a
/// <see cref="SimulationScheduler"/>. A controlled operation is the unit the kernel grants
/// the single permission baton to: while it is <see cref="SimulationOperationState.Running"/> it is
/// the one operation allowed to execute system-under-test code, regardless of how many physical
/// threads exist.
/// </para>
/// <para>
/// An operation carries a stable identity (<see cref="Id"/>), its creator/parent
/// (<see cref="ParentId"/>), the simulation runtime and node it runs under, a logical execution
/// identity distinct from any physical thread id (<see cref="LogicalExecutionId"/>), stable
/// originating-work metadata (<see cref="WorkDescription"/>), its current
/// <see cref="SimulationOperationState"/>, why it is paused (<see cref="PauseReason"/>), and its
/// terminal outcome (<see cref="TerminalException"/> / cancellation).
/// </para>
/// <para>
/// State is only ever mutated by the owning scheduler, under the scheduler's lock, via the internal
/// <see cref="ApplyTransition"/> method which validates every edge against
/// <see cref="CanTransition"/>. Callers observe state but never set it - that is what makes the
/// "exactly one running" and legal-transition invariants enforceable in one place.
/// </para>
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Usage",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The owning SimulationScheduler exclusively controls this operation's lifetime and disposes its signal via DisposeSignals during terminal cleanup/teardown; a public Dispose would invite callers to break the scheduler's ownership invariant.")]
public sealed class SimulationOperation
{
    private Action? _body;

    // Released (Release count -> 1) by the scheduler to grant the permission baton to this
    // operation's physical thread; waited on by that thread. A binary semaphore, so a spurious
    // double-grant would throw rather than silently over-count.
    private readonly SemaphoreSlim _permission = new(0, 1);
    private object? _workerReleaseGate;
    private int _workerAttached;

    private SimulationOperationState _state = SimulationOperationState.Created;

    internal SimulationOperation(
        SimulationScheduler scheduler,
        SimulationOperationId id,
        SimulationOperationId parentId,
        SimulationRuntimeIdentity runtime,
        SimulationNodeIdentity? node,
        SimulationLogicalExecutionId logicalExecutionId,
        string workDescription,
        Action body,
        ExecutionContext? capturedContext,
        int priority = 0)
    {
        Scheduler = scheduler;
        Id = id;
        ParentId = parentId;
        Runtime = runtime;
        Node = node;
        LogicalExecutionId = logicalExecutionId;
        WorkDescription = workDescription;
        Priority = priority;
        _body = body;
        CapturedContext = capturedContext;
    }

    /// <summary>Gets the scheduler that owns this operation.</summary>
    internal SimulationScheduler Scheduler { get; }

    /// <summary>Gets this operation's stable, scheduler-assigned identity.</summary>
    public SimulationOperationId Id { get; }

    /// <summary>
    /// Gets the identity of the operation that created this one, or <see cref="SimulationOperationId.None"/>
    /// for a root operation registered directly by the simulation host.
    /// </summary>
    public SimulationOperationId ParentId { get; }

    /// <summary>Gets the simulation runtime this operation executes under.</summary>
    public SimulationRuntimeIdentity Runtime { get; }

    /// <summary>
    /// Gets the node this operation is scoped to, or <see langword="null"/> for cluster-level work.
    /// </summary>
    public SimulationNodeIdentity? Node { get; }

    /// <summary>
    /// Gets this operation's logical execution identity - the ambient "logical thread" it runs as,
    /// deliberately distinct from whatever <see cref="Environment.CurrentManagedThreadId"/> its
    /// physical thread reports. The scheduler installs this into
    /// <see cref="SimulationExecutionContext"/> while the operation runs.
    /// </summary>
    public SimulationLogicalExecutionId LogicalExecutionId { get; }

    /// <summary>
    /// Gets a short, stable description of the work this operation was created to perform, used in
    /// deterministic diagnostics. Never embeds non-deterministic data.
    /// </summary>
    public string WorkDescription { get; }

    /// <summary>
    /// Gets this operation's scheduling priority. Higher values are preferred by the
    /// the priority strategy created by
    /// <see cref="Orleans.Testing.Simulation.Runtime.Scheduling.Strategies.SimulationSchedulingStrategies"/>; it has no
    /// effect under the other strategies. Defaults to <c>0</c>. This is a crisp, caller-supplied
    /// integer (not a BCL <see cref="System.Threading.ThreadPriority"/>): the scheduler never infers
    /// or mutates it, so priority-ordered schedules stay reproducible.
    /// </summary>
    public int Priority { get; }

    /// <summary>Gets the operation's current lifecycle state.</summary>
    public SimulationOperationState State => _state;

    /// <summary>
    /// Gets why the operation is paused, or <see langword="null"/> if it is not paused. Set together
    /// with a transition into <see cref="SimulationOperationState.Paused"/> and cleared on resume.
    /// </summary>
    public SimulationPauseReason? PauseReason { get; private set; }

    /// <summary>
    /// Gets the exception that escaped the operation's body (<see cref="SimulationOperationState.Faulted"/>),
    /// including an <see cref="OperationCanceledException"/>, or <see langword="null"/> otherwise. The
    /// kernel's internal teardown-unwind signal is never surfaced here.
    /// </summary>
    public Exception? TerminalException { get; private set; }

    /// <summary>
    /// Gets a value indicating whether this operation reached a terminal state
    /// (<see cref="SimulationOperationState.Completed"/>, <see cref="SimulationOperationState.Faulted"/>,
    /// or <see cref="SimulationOperationState.Canceled"/>).
    /// </summary>
    public bool IsTerminal => IsTerminalState(_state);

    // --- Internal mechanics owned by the scheduler ---

    /// <summary>The physical worker thread currently carrying this operation, assigned lazily on first dispatch.</summary>
    internal Thread? Thread { get; private set; }

    /// <summary>
    /// The resource waiter this operation is currently parked on, or <see langword="null"/> when the
    /// operation is not waiting on a resource. Set by the scheduler when the operation pauses onto a
    /// resource and cleared when the wait resolves; it is how the scheduler resolves a wakeup back to
    /// the right waiter and how the wait-for graph discovers what an operation is blocked on.
    /// </summary>
    internal Scheduling.Resources.SimulationResourceWaiter? Waiter { get; set; }

    /// <summary>The caller's captured execution context, restored on the operation thread so ambient flows in.</summary>
    internal ExecutionContext? CapturedContext { get; private set; }

    /// <summary>
    /// Set by the scheduler during teardown so the next park (or the current one, once unparked)
    /// throws the internal unwind signal instead of continuing. Never reset.
    /// </summary>
    internal bool TerminationRequested { get; private set; }

    /// <summary>Invokes the operation body. Only called on the operation's own physical thread.</summary>
    internal void InvokeBody() =>
        (_body ?? throw new SimulationSchedulerException($"Operation {Id} no longer has executable state."))();

    internal void ReleaseExecutionState()
    {
        _body = null;
        CapturedContext = null;
    }

    internal void AssignWorkerThread(Thread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (Thread is not null)
        {
            throw new SimulationSchedulerException($"Operation {Id} already has an assigned worker thread.");
        }

        Thread = thread;
        Volatile.Write(ref _workerAttached, 1);
    }

    internal void ReleaseWorkerThread(Thread thread)
    {
        if (!ReferenceEquals(Thread, thread))
        {
            throw new SimulationSchedulerException($"Operation {Id} was released by a worker it does not own.");
        }

        object? releaseGate = Volatile.Read(ref _workerReleaseGate);
        if (releaseGate is null)
        {
            Thread = null;
            Volatile.Write(ref _workerAttached, 0);
            return;
        }

        lock (releaseGate)
        {
            Thread = null;
            Volatile.Write(ref _workerAttached, 0);
            Monitor.PulseAll(releaseGate);
        }
    }

    internal bool WaitForWorkerRelease(TimeSpan timeout)
    {
        if (Volatile.Read(ref _workerAttached) == 0)
        {
            return true;
        }

        object created = new();
        object releaseGate = Interlocked.CompareExchange(ref _workerReleaseGate, created, null) ?? created;
        lock (releaseGate)
        {
            return Volatile.Read(ref _workerAttached) == 0 || Monitor.Wait(releaseGate, timeout);
        }
    }

    /// <summary>Grants the permission baton to this operation's physical thread.</summary>
    internal void GrantPermission() => _permission.Release();

    /// <summary>
    /// Parks the calling (operation) thread until the scheduler grants it the permission baton.
    /// Throws the internal unwind signal if teardown has been requested, so a torn-down operation's
    /// thread unwinds instead of resuming its body.
    /// </summary>
    internal void WaitForPermission()
    {
        _permission.Wait();
        if (TerminationRequested)
        {
            throw new SimulationOperationAbortSignal(Id);
        }
    }

    /// <summary>Requests teardown-driven unwinding of this operation's thread.</summary>
    internal void RequestTermination() => TerminationRequested = true;

    /// <summary>
    /// Validates and applies a state transition. Only the owning scheduler calls this, and only
    /// while holding its lock; every rejected edge throws
    /// <see cref="InvalidSimulationOperationTransitionException"/> rather than silently no-op'ing.
    /// </summary>
    /// <param name="to">The target state.</param>
    /// <param name="pauseReason">The pause reason when transitioning to <see cref="SimulationOperationState.Paused"/>; otherwise ignored.</param>
    /// <param name="terminalException">The fault when transitioning to <see cref="SimulationOperationState.Faulted"/>; otherwise ignored.</param>
    internal void ApplyTransition(
        SimulationOperationState to,
        SimulationPauseReason? pauseReason = null,
        Exception? terminalException = null)
    {
        var from = _state;
        if (!CanTransition(from, to))
        {
            throw new InvalidSimulationOperationTransitionException(Id, from, to);
        }

        if (to == SimulationOperationState.Paused)
        {
            PauseReason = pauseReason ?? throw new ArgumentNullException(nameof(pauseReason), "A pause reason is required when pausing.");
        }
        else
        {
            PauseReason = null;
        }

        if (to == SimulationOperationState.Faulted)
        {
            TerminalException = terminalException ?? throw new ArgumentNullException(nameof(terminalException), "A terminal exception is required when faulting.");
        }

        _state = to;
    }

    /// <summary>
    /// The fixed legality table for the operation state machine. See
    /// <see cref="SimulationOperationState"/> for the full rationale of each edge.
    /// </summary>
    /// <param name="from">The current state.</param>
    /// <param name="to">The proposed next state.</param>
    /// <returns><see langword="true"/> if the transition is permitted.</returns>
    public static bool CanTransition(SimulationOperationState from, SimulationOperationState to) => (from, to) switch
    {
        (SimulationOperationState.Created, SimulationOperationState.Runnable) => true,
        (SimulationOperationState.Created, SimulationOperationState.Canceled) => true,
        (SimulationOperationState.Runnable, SimulationOperationState.Running) => true,
        (SimulationOperationState.Runnable, SimulationOperationState.Canceled) => true,
        (SimulationOperationState.Running, SimulationOperationState.Paused) => true,
        (SimulationOperationState.Running, SimulationOperationState.Runnable) => true,
        (SimulationOperationState.Running, SimulationOperationState.Completed) => true,
        (SimulationOperationState.Running, SimulationOperationState.Faulted) => true,
        (SimulationOperationState.Running, SimulationOperationState.Canceled) => true,
        (SimulationOperationState.Paused, SimulationOperationState.Runnable) => true,
        (SimulationOperationState.Paused, SimulationOperationState.Canceled) => true,
        _ => false,
    };

    /// <summary>
    /// Gets a value indicating whether the given state is terminal (no legal transition leaves it).
    /// </summary>
    /// <param name="state">The state to classify.</param>
    /// <returns><see langword="true"/> if the state is terminal.</returns>
    public static bool IsTerminalState(SimulationOperationState state) =>
        state is SimulationOperationState.Completed or SimulationOperationState.Faulted or SimulationOperationState.Canceled;

    private bool _signalsDisposed;

    internal void DisposeSignals()
    {
        if (_signalsDisposed)
        {
            return;
        }

        _signalsDisposed = true;
        _permission.Dispose();
    }

    private string DebuggerDisplay => ToString();

    /// <inheritdoc />
    public override string ToString()
    {
        var reason = PauseReason is { } r ? $" reason={r}" : string.Empty;
        var node = Node is { } n ? $" node={n.Address}" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Id} [{_state}]{reason}{node} work='{WorkDescription}' logical={LogicalExecutionId.Value} parent={ParentId.Value}");
    }
}
