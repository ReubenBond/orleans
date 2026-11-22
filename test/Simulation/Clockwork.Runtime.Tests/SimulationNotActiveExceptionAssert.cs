using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Runtime.Tests;

internal static class SimulationNotActiveExceptionAssert
{
    public static void Equal(Exception? exception, string expectedApiName)
    {
        var simulationNotActive = Assert.IsType<SimulationNotActiveException>(exception);
        Assert.Equal(expectedApiName, simulationNotActive.ApiName);
        Assert.Equal(SimulationNotActiveException.DiagnosticMessage, simulationNotActive.Message);
    }
}
