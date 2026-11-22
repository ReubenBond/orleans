namespace Orleans.Testing.Simulation.Testing.Tests;

public sealed class TestingScaffoldTests
{
    [Fact]
    public void TestingAssemblyIsNamedForItsFuturePackage()
    {
        var assembly = System.Reflection.Assembly.Load("Orleans.Testing.Simulation.Testing");

        Assert.Equal("Orleans.Testing.Simulation.Testing", assembly.GetName().Name);
    }

    [Fact]
    public void TestingDependsOnSimulationKernelButNotOnInstrumentationOrTool()
    {
        // Testing helpers should only need the simulation kernel, not the instrumentation or
        // tooling layers built on top of it.
        var assembly = System.Reflection.Assembly.Load("Orleans.Testing.Simulation.Testing");
        var referencedNames = assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.Contains("Orleans.Testing.Simulation", referencedNames);
        Assert.DoesNotContain("Microsoft.Testing.Platform", referencedNames);
        Assert.DoesNotContain("Orleans.Testing.Simulation.Testing.MTP", referencedNames);
        Assert.DoesNotContain("Orleans.Testing.Simulation.Instrumentation", referencedNames);
        Assert.DoesNotContain("Orleans.Testing.Simulation.Tool", referencedNames);
    }

    [Fact]
    public void TestingMtpAssemblyOwnsThePlatformHook()
    {
        var assembly = System.Reflection.Assembly.Load("Orleans.Testing.Simulation.Testing.MTP");
        var referencedNames = assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.Contains("Microsoft.Testing.Platform", referencedNames);
        Assert.Contains("Orleans.Testing.Simulation", referencedNames);
        Assert.Equal("Orleans.Testing.Simulation.Testing.MTP", assembly.GetName().Name);
        Assert.Same(assembly, typeof(TestingPlatformBuilderHook).Assembly);
    }
}
