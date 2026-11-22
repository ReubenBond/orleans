using Orleans.Testing.Simulation.Instrumentation.Closure;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Closure;

/// <summary>
/// Verifies deterministic application-closure discovery: managed application and dependency
/// assemblies are selected for rewriting; framework, satellite, native, symbol, and config assets
/// are copied verbatim with a recorded reason; include/exclude patterns honor the mandatory framework
/// boundary; the entry assembly is detected from a runtimeconfig; and the asset order is stable.
/// </summary>
public sealed class ClosureDiscoveryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "cwr-closure-tests", Guid.NewGuid().ToString("n"));

    public ClosureDiscoveryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ClassifiesAppDependencyFrameworkAndAssets()
    {
        BuildStandardClosure();
        ClosurePlan plan = ClosureDiscovery.Discover(_directory, new InstrumentationConfiguration());

        Assert.Equal(
            ["app.dll", "thirdparty.dll"],
            plan.AssembliesToRewrite.Select(a => a.RelativePath).OrderBy(x => x, StringComparer.Ordinal));

        ClosureAsset framework = Single(plan, "System.Fake.dll");
        Assert.False(framework.Rewrite);
        Assert.Equal("framework assembly", framework.SkipReason);

        ClosureAsset satellite = Single(plan, "app.resources.dll");
        Assert.Equal(AssetKind.SatelliteAssembly, satellite.Kind);
        Assert.False(satellite.Rewrite);

        Assert.Equal(AssetKind.NativeLibrary, Single(plan, "native.dll").Kind);
        Assert.Equal(AssetKind.DepsJson, Single(plan, "app.deps.json").Kind);
        Assert.Equal(AssetKind.RuntimeConfig, Single(plan, "app.runtimeconfig.json").Kind);
        Assert.Equal(AssetKind.DebugSymbols, Single(plan, "app.pdb").Kind);
        Assert.Equal(AssetKind.Other, Single(plan, "appsettings.json").Kind);
    }

    [Fact]
    public void ExcludePatternIsHonored()
    {
        BuildStandardClosure();
        ClosurePlan plan = ClosureDiscovery.Discover(
            _directory, new InstrumentationConfiguration { ExcludePatterns = ["thirdparty.dll"] });

        Assert.Equal(["app.dll"], plan.AssembliesToRewrite.Select(a => a.RelativePath));
        Assert.Equal("excluded by pattern", Single(plan, "thirdparty.dll").SkipReason);
    }

    [Fact]
    public void IncludePatternLimitsRewriteSet()
    {
        BuildStandardClosure();
        ClosurePlan plan = ClosureDiscovery.Discover(
            _directory, new InstrumentationConfiguration { IncludePatterns = ["app.dll"] });

        Assert.Equal(["app.dll"], plan.AssembliesToRewrite.Select(a => a.RelativePath));
        Assert.Equal("not matched by include pattern", Single(plan, "thirdparty.dll").SkipReason);
    }

    [Fact]
    public void ExplicitIncludeCannotOverrideFrameworkBoundary()
    {
        BuildStandardClosure();
        ClosurePlan plan = ClosureDiscovery.Discover(
            _directory, new InstrumentationConfiguration { IncludePatterns = ["System.Fake.dll"] });

        Assert.Empty(plan.AssembliesToRewrite);
        Assert.Equal("framework assembly", Single(plan, "System.Fake.dll").SkipReason);
    }

    [Fact]
    public void MissingRootThrows()
    {
        Assert.Throws<ClosureException>(() => ClosureDiscovery.Discover(
            Path.Combine(_directory, "nope"), new InstrumentationConfiguration()));
    }

    [Fact]
    public void RejectsInvalidEntryWithoutUsableManagedIL()
    {
        File.WriteAllBytes(Path.Combine(_directory, "app.dll"), [0x00, 0x01, 0x02, 0x03]);
        File.WriteAllText(Path.Combine(_directory, "app.runtimeconfig.json"), "{}");

        ClosureException exception = Assert.Throws<ClosureException>(
            () => ClosureDiscovery.Discover(_directory, new InstrumentationConfiguration()));

        Assert.Contains("usable IL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetsAreOrderedDeterministically()
    {
        BuildStandardClosure();
        ClosurePlan plan = ClosureDiscovery.Discover(_directory, new InstrumentationConfiguration());

        string[] relativePaths = [.. plan.Assets.Select(a => a.RelativePath)];
        string[] sorted = [.. relativePaths.OrderBy(x => x, StringComparer.Ordinal)];
        Assert.Equal(sorted, relativePaths);
    }

    [Fact]
    public void EntryDetectedFromRuntimeConfig()
    {
        BuildStandardClosure();
        ClosurePlan plan = ClosureDiscovery.Discover(_directory, new InstrumentationConfiguration());
        Assert.Equal("app.dll", plan.EntryAssemblyRelativePath);
    }

    private static ClosureAsset Single(ClosurePlan plan, string relativePath) =>
        plan.Assets.Single(a => string.Equals(a.RelativePath, relativePath, StringComparison.Ordinal));

    private void BuildStandardClosure()
    {
        string third = Compile("thirdparty", "namespace Third { public static class T { public static int V() => 1; } }");
        Compile("app", "namespace App { public static class A { public static int Go() => Third.T.V(); } }",
            references: [third]);
        Compile("System.Fake", "namespace SysFake { public static class C { public static int V() => 1; } }");
        Compile("app.resources", "namespace Res { public static class C { public static int V() => 1; } }");

        File.WriteAllBytes(Path.Combine(_directory, "native.dll"), [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);
        File.WriteAllText(Path.Combine(_directory, "app.deps.json"), "{}");
        File.WriteAllText(Path.Combine(_directory, "app.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), "{}");
    }

    private string Compile(string name, string source, IEnumerable<string>? references = null) =>
        FixtureCompiler.Compile(
            name, source, _directory, name == "app" ? FixtureSymbols.PortableFile : FixtureSymbols.None,
            optimize: false, additionalReferencePaths: references);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
