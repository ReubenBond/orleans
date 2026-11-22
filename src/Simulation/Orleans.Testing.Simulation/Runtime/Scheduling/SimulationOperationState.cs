namespace Orleans.Testing.Simulation.Runtime.Scheduling;

/// <summary>
/// <para>
/// The lifecycle state of a <see cref="SimulationOperation"/> as tracked by its owning
/// <see cref="SimulationScheduler"/>. The scheduler - not arbitrary callers - is the only
/// component that drives these transitions, and it validates every transition against a fixed
/// legality table (see <see cref="SimulationOperation.CanTransition"/>); an illegal transition
/// fails loudly with an <see cref="InvalidSimulationOperationTransitionException"/> rather than
/// silently corrupting scheduler state.
/// </para>
/// <para>
/// The legal transitions are:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Created"/> -&gt; <see cref="Runnable"/> (admitted for scheduling).</description></item>
///   <item><description><see cref="Created"/> -&gt; <see cref="Canceled"/> (canceled before it ever ran).</description></item>
///   <item><description><see cref="Runnable"/> -&gt; <see cref="Running"/> (granted the permission baton).</description></item>
///   <item><description><see cref="Runnable"/> -&gt; <see cref="Canceled"/> (canceled while waiting to run).</description></item>
///   <item><description><see cref="Running"/> -&gt; <see cref="Paused"/> (voluntarily yielded the baton to wait).</description></item>
///   <item><description><see cref="Running"/> -&gt; <see cref="Runnable"/> (yielded the baton but immediately re-runnable).</description></item>
///   <item><description><see cref="Running"/> -&gt; <see cref="Completed"/> (body returned normally).</description></item>
///   <item><description><see cref="Running"/> -&gt; <see cref="Faulted"/> (an exception, including <see cref="OperationCanceledException"/>, escaped the body).</description></item>
///   <item><description><see cref="Running"/> -&gt; <see cref="Canceled"/> (torn down).</description></item>
///   <item><description><see cref="Paused"/> -&gt; <see cref="Runnable"/> (resumed).</description></item>
///   <item><description><see cref="Paused"/> -&gt; <see cref="Canceled"/> (canceled / torn down while paused).</description></item>
/// </list>
/// <para>
/// <see cref="Completed"/>, <see cref="Faulted"/>, and <see cref="Canceled"/> are terminal: no
/// transition leaves them.
/// </para>
/// </summary>
public enum SimulationOperationState
{
    /// <summary>
    /// The operation has been registered but has not yet been admitted for scheduling. It holds no
    /// permission baton and its physical thread (if any) has not started running its body.
    /// </summary>
    Created,

    /// <summary>
    /// The operation is eligible to run and is waiting for the scheduler to grant it the single
    /// permission baton. It is not currently executing any SUT code.
    /// </summary>
    Runnable,

    /// <summary>
    /// The operation currently holds the permission baton and is the one operation permitted to
    /// execute SUT code. At most one operation per scheduler is ever in this state at a time.
    /// </summary>
    Running,

    /// <summary>
    /// The operation has voluntarily released the permission baton to wait for something (its
    /// reason is recorded in <see cref="SimulationOperation.PauseReason"/>) and is not eligible to
    /// run until something transitions it back to <see cref="Runnable"/> via
    /// <see cref="SimulationScheduler.Resume"/>. Its physical thread is parked on a wait
    /// handle - it is not busy-spinning.
    /// </summary>
    Paused,

    /// <summary>
    /// Terminal: the operation's body returned normally.
    /// </summary>
    Completed,

    /// <summary>
    /// Terminal: an exception escaped the operation's body unhandled and is captured in
    /// <see cref="SimulationOperation.TerminalException"/>. This includes an
    /// <see cref="OperationCanceledException"/>: a body which lets one escape has no task to observe
    /// it, like a .NET <c>Thread</c> body, thread-pool work item, timer callback, or
    /// synchronization-context callback, where it crashes the process. Task-backed operations
    /// (<c>Task.Run</c>, <c>TaskFactory.StartNew</c>, continuations, and async methods) capture their
    /// delegate's exceptions in their task, so their operations complete.
    /// </summary>
    Faulted,

    /// <summary>
    /// Terminal: the operation was canceled through <see cref="SimulationScheduler.Cancel"/>, withdrawn
    /// by the registration that scheduled it, or torn down before completing normally.
    /// </summary>
    Canceled,
}
