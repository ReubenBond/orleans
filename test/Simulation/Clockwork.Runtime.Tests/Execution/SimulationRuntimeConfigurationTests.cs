using System.Reflection;
using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Runtime.Tests.Execution;

public sealed class SimulationRuntimeConfigurationTests
{
    [Fact]
    public void RuntimeEntryIsNotPublic()
    {
        MethodInfo? enterRuntime = typeof(SimulationExecutionContext).GetMethod(
            "EnterRuntime",
            BindingFlags.Public | BindingFlags.Static);

        Assert.Null(enterRuntime);
    }

    [Fact]
    public void IncompleteRuntimeCannotBecomeAmbient()
    {
        var runtime = new SimulationRuntimeIdentity(Guid.NewGuid(), 1);

        var exception = Assert.Throws<InvalidOperationException>(
            () => SimulationExecutionContext.EnterRuntime(runtime));

        Assert.Contains("incomplete", exception.Message, StringComparison.Ordinal);
        Assert.False(SimulationExecutionContext.IsActive);
    }
}
