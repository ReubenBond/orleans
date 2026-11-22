namespace Orleans.Testing.Simulation.Instrumentation.Tests.Packaging;

public sealed class PackageBoundaryInspectorTests
{
    [Fact]
    public void ForbiddenProductionPackageSetIsExactAndEveryEntryIsDetected()
    {
        string[] expected =
        [
            "Microsoft.Orleans.Testing.Simulation",
            "Microsoft.Orleans.Testing.Simulation.Instrumentation",
            "Microsoft.Orleans.Testing.Simulation.Instrumentation.Build",
            "Microsoft.Orleans.Testing.Simulation.Analyzers",
            "Microsoft.Orleans.Testing.Simulation.Testing",
            "Microsoft.Orleans.Testing.Simulation.Testing.MTP",
            "Microsoft.Orleans.Testing.Simulation.Tool",
        ];

        Assert.Equal(expected, PackageBoundaryInspector.ForbiddenProductionPackageIds);
        Assert.Equal(
            expected.Order(StringComparer.OrdinalIgnoreCase),
            PackageBoundaryInspector.FindForbiddenPackages(
                [.. expected, "Microsoft.Testing.Platform", "Orleans.Durable.Storage.Diagnostics"]));
        Assert.Empty(
            PackageBoundaryInspector.FindForbiddenPackages(
                ["Microsoft.Orleans.Testing.Simulation.Experimental"]));
    }

    [Theory]
    [InlineData("lib/net11.0/Orleans.Testing.Simulation.dll", true)]
    [InlineData("runtimes/win-x64/lib/net11.0/Orleans.Testing.Simulation.Testing.dll", true)]
    [InlineData("lib/net11.0/Orleans.Testing.Simulation.Testing.MTP.dll", true)]
    [InlineData("lib/net11.0/Orleans.Testing.Simulation.Analyzers.dll", true)]
    [InlineData("analyzers/dotnet/cs/Orleans.Testing.Simulation.Analyzers.dll", false)]
    [InlineData("build/Orleans.Testing.Simulation.Instrumentation.Build.dll", false)]
    [InlineData("lib/net11.0/Orleans.Durable.Storage.Diagnostics.dll", false)]
    public void SimulationAssembliesAreForbiddenOnlyWhenTheyAreRuntimeAssets(
        string assetPath,
        bool expectedForbidden)
    {
        string[] forbidden = PackageBoundaryInspector.FindForbiddenRuntimeAssets([assetPath]);

        if (expectedForbidden)
        {
            Assert.Equal([assetPath], forbidden);
        }
        else
        {
            Assert.Empty(forbidden);
        }
    }
}
