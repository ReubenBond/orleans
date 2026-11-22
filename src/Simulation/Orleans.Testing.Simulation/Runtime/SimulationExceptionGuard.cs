using System.Runtime.ExceptionServices;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Runtime;

/// <summary>
/// The runtime half of Clockwork's exception-handler hardening. The instrumentation's
/// exception-hardening pass injects a call to <see cref="ThrowIfControlSignal(object)"/> at the start of
/// every broad user <c>catch (Exception)</c> / <c>catch</c> block and every exception <c>filter</c> in a
/// rewritten assembly. When the caught object is the scheduler's internal control-flow unwinding signal the
/// guard re-throws it (preserving its original stack trace) so a broad user handler cannot accidentally
/// swallow it and strand the deterministic scheduler; every other exception passes straight through
/// untouched, so normal application exception handling is unchanged.
/// </summary>
/// <remarks>
/// This is defence-in-depth layered on top of the controlled task runtime's explicit gate/state model: the scheduler already
/// re-raises the signal on the next park and joins physical threads with a bounded timeout, so a swallowed
/// signal cannot permanently strand the kernel. The guard makes the intent explicit at the exact call site
/// and mirrors Microsoft Coyote's <c>ExceptionProvider.ThrowIfExecutionCanceledException</c> mechanism.
/// </remarks>
public static class SimulationExceptionGuard
{
    /// <summary>
    /// Re-throws <paramref name="exception"/> if it is the scheduler's internal control-flow signal;
    /// otherwise does nothing so the user handler runs normally. It requires an active simulation, on
    /// any thread: it reads no simulation state, so escaped work's own handlers run as written while
    /// the controlled API which escaped reports the violation.
    /// </summary>
    /// <param name="exception">The exception the user handler is about to observe.</param>
    public static void ThrowIfControlSignal(object? exception)
    {
        if (!SimulationExecutionContext.IsActive)
        {
            throw new SimulationNotActiveException("Orleans.Testing.Simulation.Runtime.SimulationExceptionGuard.ThrowIfControlSignal");
        }

        if (exception is SimulationOperationAbortSignal signal)
        {
            // Re-throw preserving the original stack trace so the signal keeps unwinding past the user
            // handler exactly as if it had never been caught.
            ExceptionDispatchInfo.Throw(signal);
        }
    }
}
