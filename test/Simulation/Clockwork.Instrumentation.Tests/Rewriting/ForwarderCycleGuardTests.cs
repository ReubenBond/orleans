using Orleans.Testing.Simulation.Instrumentation.Closure;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Rewriting;

/// <summary>
/// Assemblies that forward a type back to each other - as facades built for different target frameworks can - would
/// have Cecil follow the forwarders until the stack overflows, terminating the process. Every resolver the
/// instrumentation reads assemblies with stops at the cycle and fails the resolution, and the rewrite reports it.
/// </summary>
public sealed class ForwarderCycleGuardTests
{
    private const string Cycle = "The type forwarders for 'Fx.Cyc.Base' form a cycle: Fx.CycA -> Fx.CycB -> Fx.CycA";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForwarderCycleFailsTheResolutionInsteadOfOverflowingTheStack(bool staged)
    {
        string directory = TestArtifacts.CreateUnique("cwr-forwarder-cycle");
        try
        {
            TypeForwarders.Write(directory, "Fx.CycA", "Fx.Cyc.Base", "Fx.CycB");
            TypeForwarders.Write(directory, "Fx.CycB", "Fx.Cyc.Base", "Fx.CycA");
            (IAssemblyResolver resolver, ForwarderCycleGuard guard) = CreateResolver(directory, staged);
            using (resolver)
            {
                using ModuleDefinition user = ModuleDefinition.CreateModule(
                    "Fx.User",
                    new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = resolver, MetadataResolver = guard });
                var reference = new TypeReference("Fx.Cyc", "Base", user, new AssemblyNameReference("Fx.CycA", new Version(0, 0, 0, 0)));

                AssemblyResolutionException exception = Assert.Throws<AssemblyResolutionException>(() => reference.Resolve());

                Assert.Equal(Cycle, Assert.IsType<TypeLoadException>(exception.InnerException).Message);
                Assert.Equal([Cycle], guard.Cycles);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ForwarderCycleFailsTheRewriteWithADiagnostic()
    {
        // Checking the override against its base type resolves the base type, which the forwarders send round in a cycle.
        using var context = RewriteTestContext.Create();
        string basePath = FixtureCompiler.Compile(
            "Fx.CycA",
            """
            namespace Fx.Cyc
            {
                public abstract class Base
                {
                    public abstract string Handle(ClockworkFixtures.Api.LegacyMarker marker);
                }
            }
            """,
            context.Directory,
            FixtureSymbols.None,
            optimize: false,
            additionalReferencePaths: [context.ApiPath]);
        string fixturePath = FixtureCompiler.Compile(
            "Fx.Cyclic",
            """
            namespace Fx
            {
                public sealed class Handler : Fx.Cyc.Base
                {
                    public override string Handle(ClockworkFixtures.Api.LegacyMarker marker) => marker.ToString();
                }
            }
            """,
            context.Directory,
            FixtureSymbols.PortableFile,
            optimize: false,
            additionalReferencePaths: [context.ApiPath, basePath]);
        TypeForwarders.Write(context.Directory, "Fx.CycA", "Fx.Cyc.Base", "Fx.CycB");
        TypeForwarders.Write(context.Directory, "Fx.CycB", "Fx.Cyc.Base", "Fx.CycA");

        RewriteResult result = context.Rewrite(
            fixturePath,
            MarkerRuleSet(),
            new RewriteOptions { TargetRuntime = RewriteTestContext.TargetRuntime, ReplacementAssemblyPaths = [context.ShimPath], ReferenceSearchDirectories = [context.Directory] });

        Assert.False(result.Succeeded);
        RewriteDiagnostic error = Assert.Single(result.Errors);
        Assert.Equal(RewriteDiagnosticIds.UnresolvedReference, error.Id);
        Assert.Equal($"{Cycle}, so 'Fx.Cyclic' cannot be checked against the type.", error.Message);
    }

    private static (IAssemblyResolver Resolver, ForwarderCycleGuard Guard) CreateResolver(string directory, bool staged)
    {
        if (staged)
        {
            var stagedResolver = new StagedClosureAssemblyResolver(directory, FrameworkAssemblyClassifier.Default);
            return (stagedResolver, stagedResolver.Guard);
        }

        var engineResolver = new GuardedAssemblyResolver();
        engineResolver.AddSearchDirectory(directory);
        return (engineResolver, engineResolver.Guard);
    }

    private static RewriteRuleSet MarkerRuleSet() =>
        new(
            "clockwork.fixtures.forwarder-cycle",
            "1.0",
            [
                RewriteRule.SubstituteType(
                    "substitute-marker",
                    "ClockworkFixtures.Api.LegacyMarker",
                    RewriteReplacement.Type(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims.ModernMarker")),
            ]);
}
