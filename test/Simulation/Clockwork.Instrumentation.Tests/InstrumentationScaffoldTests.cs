namespace Orleans.Testing.Simulation.Instrumentation.Tests;

public sealed class InstrumentationScaffoldTests
{
    [Fact]
    public void InstrumentationAssemblyIsNamedForItsFuturePackage()
    {
        var assembly = System.Reflection.Assembly.Load("Orleans.Testing.Simulation.Instrumentation");

        Assert.Equal("Orleans.Testing.Simulation.Instrumentation", assembly.GetName().Name);
    }

    [Fact]
    public void InstrumentationDependsOnSimulationKernelButNotOnBuildOrTool()
    {
        // Instrumentation sits above the simulation kernel and below Instrumentation.Build/Tool - verify the
        // dependency edge points the right way.
        var assembly = System.Reflection.Assembly.Load("Orleans.Testing.Simulation.Instrumentation");
        var referencedNames = assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.Contains("Orleans.Testing.Simulation", referencedNames);
        Assert.DoesNotContain("Orleans.Testing.Simulation.Instrumentation.Build", referencedNames);
        Assert.DoesNotContain("Orleans.Testing.Simulation.Tool", referencedNames);
    }
}
