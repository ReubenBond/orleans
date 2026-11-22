using System.Reflection;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Conformance.Tests;

internal static class SimulationNotActiveExceptionAssert
{
    public static SimulationNotActiveException Throws(MethodInfo method, params object?[] args)
    {
        var invocation = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, args.Length == 0 ? null : args));
        var exception = Assert.IsType<SimulationNotActiveException>(invocation.InnerException);

        Assert.False(string.IsNullOrWhiteSpace(exception.ApiName));
        Assert.Equal(SimulationNotActiveException.DiagnosticMessage, exception.Message);
        return exception;
    }
}
