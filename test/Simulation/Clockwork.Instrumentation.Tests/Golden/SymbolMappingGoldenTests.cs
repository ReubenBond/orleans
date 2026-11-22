using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden tests for symbol handling and source mapping: portable and embedded PDBs are detected and
/// carried into per-transformation source locations, absent symbols are reported (not silently
/// dropped), and the output preserves a separate portable PDB.
/// </summary>
public sealed class SymbolMappingGoldenTests
{
    private const string Fixture = """
        using ClockworkFixtures.Api;

        namespace Fx
        {
            public static class Sym
            {
                public static long Ticks() => RealClock.UtcNowTicks();
            }
        }
        """;

    [Fact]
    public void PortablePdbYieldsSourceMappingAndPreservedOutputSymbols()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.SymPortable", Fixture, FixtureSymbols.PortableFile);
        string outputPath = Path.Combine(context.Directory, "Fx.SymPortable.rewritten.dll");

        var result = context.Rewrite(fixturePath, outputPath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        Assert.True(result.Manifest.Input.HasSymbols);
        Assert.Equal("Portable", result.Manifest.Input.SymbolKind);

        var transformation = Assert.Single(result.Manifest.Transformations);
        Assert.False(string.IsNullOrEmpty(transformation.SourceFile));
        Assert.True(transformation.SourceLine > 0);

        // The rewritten assembly keeps a separate portable PDB.
        Assert.True(File.Exists(Path.ChangeExtension(outputPath, "pdb")));
    }

    [Fact]
    public void EmbeddedPdbIsDetected()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.SymEmbedded", Fixture, FixtureSymbols.Embedded);

        var result = context.Rewrite(fixturePath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        Assert.True(result.Manifest.Input.HasSymbols);
        Assert.Equal("Embedded", result.Manifest.Input.SymbolKind);

        var transformation = Assert.Single(result.Manifest.Transformations);
        Assert.False(string.IsNullOrEmpty(transformation.SourceFile));
    }

    [Fact]
    public void AbsentSymbolsAreReportedNotDropped()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.SymNone", Fixture, FixtureSymbols.None);

        var result = context.Rewrite(fixturePath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        Assert.False(result.Manifest.Input.HasSymbols);
        Assert.Equal("None", result.Manifest.Input.SymbolKind);
        Assert.Contains(result.Diagnostics, d => d.Id == RewriteDiagnosticIds.SymbolsAbsent);

        // The site is still rewritten and recorded - only its source location is unavailable.
        var transformation = Assert.Single(result.Manifest.Transformations);
        Assert.Null(transformation.SourceFile);
        Assert.Equal(-1, transformation.SourceLine);
    }
}
