namespace Orleans.Testing.Simulation.Runtime.Tasks;

/// <summary>
/// <para>
/// A handle to a deterministic virtual-time deadline registered on the controlled work loop. A finite
/// blocking wait (a <c>Monitor.TryEnter(timeout)</c>, <c>Monitor.Wait(timeout)</c>, or
/// <c>SemaphoreSlim.Wait</c>/<c>WaitAsync(timeout)</c>) registers one of these, then pumps the loop until
/// either the thing it is waiting for happens or the deadline elapses. There is no real timer and no wall
/// clock: the deadline only fires when the loop has no other runnable work and advances modelled time to
/// the next due deadline, so a timeout is a pure, replayable function of the scheduling order.
/// </para>
/// <para>
/// Because time only advances when nothing else can run, any release/pulse/cancellation that is possible
/// at the current modelled instant always happens <em>before</em> a timeout - which is exactly the
/// deterministic first-winner policy the finite-wait shims rely on.
/// </para>
/// </summary>
public interface ISimulationTimer
{
    /// <summary>Gets a value indicating whether the deadline has elapsed (modelled time reached it).</summary>
    bool IsElapsed { get; }

    /// <summary>
    /// Cancels the deadline so it can no longer elapse. Called by a wait that completed for another reason
    /// (acquired the lock, was pulsed, was cancelled) so a later time advance does not fire a stale timeout.
    /// Idempotent, and a no-op once the deadline has already elapsed.
    /// </summary>
    void Cancel();
}
