using System.IO;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Tests;

/// <summary>
/// Tests for the exception-handler hardening guard. The instrumentation's
/// exception-hardening pass injects a call to
/// <see cref="SimulationExceptionGuard.ThrowIfControlSignal(object)"/> at the start of every broad user
/// <c>catch</c> block and exception filter. The guard must re-throw the scheduler's internal control-flow
/// signal (so it keeps unwinding past a broad user handler) while letting every ordinary exception pass
/// straight through untouched.
/// </summary>
public sealed class ControlledExceptionGuardTests
{
    [Fact]
    public void RethrowsTheInternalControlSignal()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var signal = new SimulationOperationAbortSignal(new SimulationOperationId(42));

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            // The guard must re-surface the exact same signal instance, preserving its identity/stack.
            var rethrown = Assert.Throws<SimulationOperationAbortSignal>(
                () => SimulationExceptionGuard.ThrowIfControlSignal(signal));

            Assert.Same(signal, rethrown);
            Assert.Equal(new SimulationOperationId(42), rethrown.OperationId);
        });
    }

    [Fact]
    public void LetsOrdinaryExceptionsPassThrough()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            // A normal application exception a broad user catch is meant to handle must NOT be re-thrown by
            // the guard - it returns so the user handler runs exactly as written.
            SimulationExceptionGuard.ThrowIfControlSignal(new InvalidOperationException("boom"));
            SimulationExceptionGuard.ThrowIfControlSignal(new IOException("io"));
            SimulationExceptionGuard.ThrowIfControlSignal(new TimeoutException("base"));
        });
    }

    [Fact]
    public void IgnoresNonExceptionAndNullOperands()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            // The injected IL dups whatever is on the handler's evaluation stack; the guard must tolerate a
            // null or non-Exception operand without throwing.
            SimulationExceptionGuard.ThrowIfControlSignal(null);
            SimulationExceptionGuard.ThrowIfControlSignal("not an exception");
            SimulationExceptionGuard.ThrowIfControlSignal(42);
        });
    }
}
