namespace Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

/// <summary>
/// The terminal reason a controlled resource wait ended, returned by
/// <c>SimulationScheduler.WaitOnResource</c>. Exactly one of these describes every
/// completed wait; the value is decided deterministically by the first of signal / virtual-timeout /
/// cancellation to resolve the waiter under the scheduler lock (see the scheduler's race-resolution
/// rules). Higher-level shims (controlled synchronization) translate these into BCL-shaped results - e.g.
/// <see cref="Signaled"/> vs <see cref="TimedOut"/> becomes a <c>Monitor.Wait</c> /
/// <c>SemaphoreSlim.Wait(timeout)</c> boolean, and <see cref="Canceled"/> becomes an
/// <see cref="System.OperationCanceledException"/>.
/// </summary>
public enum SimulationWaitOutcome
{
    /// <summary>
    /// The wait was satisfied by an explicit signal/release from another operation (the resource
    /// became available, or an event/task the waiter was blocked on completed).
    /// </summary>
    Signaled,

    /// <summary>
    /// The wait's virtual-time timeout elapsed before any signal arrived. Also the immediate result
    /// of a zero timeout that could not be satisfied synchronously.
    /// </summary>
    TimedOut,

    /// <summary>
    /// The wait's <see cref="System.Threading.CancellationToken"/> was canceled before a signal or
    /// timeout resolved it (including a token already canceled at the moment the wait began).
    /// </summary>
    Canceled,
}
