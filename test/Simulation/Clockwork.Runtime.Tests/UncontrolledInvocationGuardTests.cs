namespace Orleans.Testing.Simulation.Runtime.Tests;

/// <summary>
/// Tests for the uncontrolled-invocation rejection shim. The instrumentation rule set
/// rewrites process-control and abrupt-termination call sites to invoke
/// <see cref="UncontrolledInvocationGuard.Reject(string)"/> before the original call; the guard always
/// throws an <see cref="UncontrolledInvocationException"/> that names the exact API, so a rewritten
/// assembly can never reach the real API.
/// </summary>
public sealed class UncontrolledInvocationGuardTests
{
    [Fact]
    public void RejectThrowsNamingTheApi()
    {
        var ex = Assert.Throws<UncontrolledInvocationException>(
            () => UncontrolledInvocationGuard.Reject("System.Diagnostics.Process.Start"));

        Assert.Equal("System.Diagnostics.Process.Start", ex.ApiName);
        Assert.Contains("System.Diagnostics.Process.Start", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectIsUnconditionalRegardlessOfSimulationState()
    {
        // Unlike the controlled shims, uncontrolled APIs cannot be modelled at all, so the guard rejects
        // whether or not a simulation is active.
        Assert.Throws<UncontrolledInvocationException>(
            () => UncontrolledInvocationGuard.Reject("System.Environment.Exit"));
    }
}
