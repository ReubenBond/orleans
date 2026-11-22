using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>
/// A disposable per-test sandbox: a unique temporary directory holding the compiled API and shim
/// assemblies, plus helpers to compile fixtures, run the rewrite engine with sensible defaults, and
/// read the rewritten output back with Mono.Cecil. The directory (and everything in it) is deleted on
/// <see cref="Dispose"/>.
/// </summary>
internal sealed class RewriteTestContext : IDisposable
{
    private RewriteTestContext(string directory, string apiPath, string shimPath)
    {
        Directory = directory;
        ApiPath = apiPath;
        ShimPath = shimPath;
    }

    /// <summary>Gets the target runtime the tests rewrite for: the one the built-in rules support.</summary>
    public static Version TargetRuntime => BuiltInRuleSets.SupportedRuntime;

    /// <summary>Gets the sandbox directory.</summary>
    public string Directory { get; }

    /// <summary>Gets the path of the compiled controlled-API assembly.</summary>
    public string ApiPath { get; }

    /// <summary>Gets the path of the compiled shim assembly.</summary>
    public string ShimPath { get; }

    /// <summary>Creates a new sandbox and compiles the shared API and shim assemblies into it.</summary>
    public static RewriteTestContext Create()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cwr-tests", Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(directory);

        string apiPath = FixtureCompiler.Compile(
            FixtureSources.ApiAssemblyName, FixtureSources.Api, directory, FixtureSymbols.PortableFile, optimize: false);
        string shimPath = FixtureCompiler.Compile(
            FixtureSources.ShimAssemblyName, FixtureSources.Shims, directory, FixtureSymbols.PortableFile, optimize: false,
            additionalReferencePaths: [apiPath]);

        return new RewriteTestContext(directory, apiPath, shimPath);
    }

    /// <summary>Compiles a fixture assembly that references the API (and shim) assemblies.</summary>
    public string CompileFixture(
        string assemblyName,
        string source,
        FixtureSymbols symbols = FixtureSymbols.PortableFile,
        bool optimize = false)
        => FixtureCompiler.Compile(assemblyName, source, Directory, symbols, optimize, additionalReferencePaths: [ApiPath, ShimPath]);

    /// <summary>Runs the engine against <paramref name="inputPath"/> writing a "*.rewritten.dll" beside it.</summary>
    public RewriteResult Rewrite(string inputPath, RewriteRuleSet ruleSet, RewriteOptions? options = null)
    {
        string outputPath = Path.Combine(
            Directory, Path.GetFileNameWithoutExtension(inputPath) + ".rewritten.dll");
        return Rewrite(inputPath, outputPath, ruleSet, options);
    }

    /// <summary>Runs the engine against <paramref name="inputPath"/> writing to <paramref name="outputPath"/>.</summary>
    public RewriteResult Rewrite(string inputPath, string outputPath, RewriteRuleSet ruleSet, RewriteOptions? options = null)
    {
        options ??= new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [ShimPath],
            ReferenceSearchDirectories = [Directory],
        };

        return RewriteEngine.Rewrite(new RewriteRequest(inputPath, outputPath, ruleSet, options));
    }

    /// <summary>Loads an assembly with Mono.Cecil for structural assertions (symbols read when present).</summary>
    public ModuleDefinition LoadModule(string assemblyPath)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Directory);
        try
        {
            return ModuleDefinition.ReadModule(assemblyPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadSymbols = true,
                InMemory = true,
            });
        }
        catch (Exception ex) when (ex is Mono.Cecil.Cil.SymbolsNotFoundException or Mono.Cecil.Cil.SymbolsNotMatchingException or BadImageFormatException)
        {
            return ModuleDefinition.ReadModule(assemblyPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadSymbols = false,
                InMemory = true,
            });
        }
    }

    /// <summary>Builds the standard rule set used by most golden tests.</summary>
    public static RewriteRuleSet StandardRuleSet(string version = "1.0")
    {
        const string shim = FixtureSources.ShimAssemblyName;

        return new RewriteRuleSet(
            "clockwork.fixtures",
            version,
            [
                RewriteRule.RedirectCall(
                    "redirect-utcnowticks",
                    MemberSignature.Method("ClockworkFixtures.Api.RealClock", "UtcNowTicks"),
                    RewriteReplacement.Method(shim, "ClockworkFixtures.Shims.ClockShim", "UtcNowTicks")),
                RewriteRule.RedirectCall(
                    "redirect-getvalue",
                    MemberSignature.Method("ClockworkFixtures.Api.Service", "GetValue"),
                    RewriteReplacement.Method(shim, "ClockworkFixtures.Shims.ClockShim", "GetValue")),
                RewriteRule.RedirectNewObj(
                    "redirect-widget-ctor",
                    MemberSignature.Constructor("ClockworkFixtures.Api.Widget", "System.Int32"),
                    RewriteReplacement.Method(shim, "ClockworkFixtures.Shims.ClockShim", "CreateWidget")),
                RewriteRule.RedirectCall(
                    "redirect-echo",
                    new MemberSignature("ClockworkFixtures.Api.GenericOps", "Echo"),
                    RewriteReplacement.Method(shim, "ClockworkFixtures.Shims.ClockShim", "Echo")),
                RewriteRule.WrapAfterCall(
                    "wrap-measure",
                    MemberSignature.Method("ClockworkFixtures.Api.Meterable", "Measure"),
                    RewriteReplacement.Method(shim, "ClockworkFixtures.Shims.ClockShim", "WrapMeasure")),
                RewriteRule.InjectRejection(
                    "reject-dangerouswrite",
                    MemberSignature.Method("ClockworkFixtures.Api.Forbidden", "DangerousWrite", "System.String"),
                    RewriteReplacement.Method(shim, "ClockworkFixtures.Shims.ClockShim", "Reject")),
            ]);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file must not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }
}
