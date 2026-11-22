using System.Reflection;
using System.Runtime.Loader;
using Orleans.Simulation.SourceBuild.Subject;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Simulation.SourceBuild.Tests;

/// <summary>
/// An instrumented test project which opts in through eng/OrleansSimulation.targets runs its dependency closure
/// rewritten, while the test assembly and the dependency's own build output keep ordinary IL.
/// </summary>
public sealed class SourceBuildInstrumentationTests
{
    private const string RewriteSignature = "Clockwork.RewriteSignature";

    [Fact]
    public void DependencyClosureIsRewritten()
    {
        Assert.True(HasRewriteSignature(typeof(WallClock).Assembly.GetCustomAttributesData()));

        // The deterministic rules route the wall clock to the simulation, and no simulation is active here.
        Assert.Throws<SimulationNotActiveException>(() => WallClock.UtcNow());
    }

    [Fact]
    public void TestAssemblyKeepsOrdinaryIl()
    {
        Assert.False(HasRewriteSignature(typeof(SourceBuildInstrumentationTests).Assembly.GetCustomAttributesData()));
        Assert.NotEqual(default, DateTime.UtcNow);
    }

    [Fact]
    public void DependencyBuildOutputKeepsOrdinaryIl()
    {
        string path = typeof(SourceBuildInstrumentationTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(static attribute => attribute.Key == "SourceBuildSubjectOutput")
            .Value!;
        Assert.NotEqual(Path.GetFullPath(typeof(WallClock).Assembly.Location), Path.GetFullPath(path));

        var context = new AssemblyLoadContext("subject-build-output", isCollectible: true);
        try
        {
            Assembly ordinary = context.LoadFromAssemblyPath(path);
            Assert.False(HasRewriteSignature(ordinary.GetCustomAttributesData()));
        }
        finally
        {
            context.Unload();
        }
    }

    private static bool HasRewriteSignature(IEnumerable<CustomAttributeData> attributes) =>
        attributes.Any(static attribute =>
            attribute.AttributeType.FullName == typeof(AssemblyMetadataAttribute).FullName
            && attribute.ConstructorArguments is [{ Value: RewriteSignature }, ..]);
}
