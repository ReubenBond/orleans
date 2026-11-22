namespace Orleans.Testing.Simulation.Runtime;

/// <summary>
/// Thrown at a rewritten call site that invokes an uncontrolled process-control or abrupt
/// host-termination API which the deterministic scheduler cannot model - the static
/// <see cref="System.Diagnostics.Process"/> <c>Start</c> family, the
/// <see cref="System.Diagnostics.Process"/> instance <c>Kill</c>/<c>WaitForExit</c>/<c>WaitForExitAsync</c>
/// members, and <see cref="System.Environment"/> <c>Exit</c>/<c>FailFast</c>. Launching, killing, blocking
/// on, or terminating a real OS process would let control escape the simulation (or tear the host down out
/// from under it), so the rewritten call site rejects the invocation precisely, naming the exact API,
/// rather than letting it reach uncontrolled OS machinery.
/// </summary>
public sealed class UncontrolledInvocationException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="UncontrolledInvocationException"/> class.</summary>
    /// <param name="apiName">The uncontrolled API that was invoked.</param>
    public UncontrolledInvocationException(string apiName)
        : base(
            $"The API '{apiName}' cannot be controlled by the Clockwork deterministic scheduler and is " +
            "rejected in a rewritten assembly: launching, killing, waiting on, or terminating a real " +
            "operating-system process would let execution escape the simulation. Restructure the code to " +
            "avoid process control and abrupt host termination under simulation.")
    {
        ApiName = apiName;
    }

    /// <summary>Gets the uncontrolled API that was invoked.</summary>
    public string? ApiName { get; }
}

/// <summary>
/// The runtime rejection shim for uncontrolled process/termination APIs. The instrumentation
/// rule set rewrites each targeted call site to invoke <see cref="Reject(string)"/> immediately before the
/// original call (which therefore never executes), passing the fully-qualified name of the intercepted API.
/// The rejection is unconditional - unlike the controlled shims, these APIs cannot be modelled at all, so a
/// rewritten assembly is not permitted to reach them whether or not a simulation is currently active, which
/// mirrors Coyote's uncontrolled-invocation pass (whose rewritten sites always throw).
/// </summary>
public static class UncontrolledInvocationGuard
{
    /// <summary>Rejects an uncontrolled invocation, naming the exact API.</summary>
    /// <param name="apiName">The fully-qualified name of the intercepted API.</param>
    /// <exception cref="UncontrolledInvocationException">Always thrown.</exception>
    public static void Reject(string apiName) =>
        throw new UncontrolledInvocationException(apiName);
}
