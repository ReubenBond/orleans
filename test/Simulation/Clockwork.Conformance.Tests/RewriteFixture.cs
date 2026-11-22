using System.Reflection;
using System.Text;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Runtime.Shims;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// Compiles ordinary C# source whose direct calls target the real .NET&#160;11 BCL APIs, rewrites it
/// in-process with a versioned built-in rule set, and loads the rewritten assembly. Rewritten code is
/// simulation-only, while separately loaded uninstrumented probes continue to use the real BCL.
/// </summary>
internal sealed class RewriteFixture : IDisposable
{
    private static readonly MetadataReference[] RuntimeReferences = LoadRuntimeReferences();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cwr-conformance", Guid.NewGuid().ToString("n"));

    public RewriteFixture()
    {
        Directory.CreateDirectory(SourceDir);
        Directory.CreateDirectory(StagingDir);
    }

    private string SourceDir => Path.Combine(_root, "src");

    private string StagingDir => Path.Combine(_root, "staged");

    /// <summary>Compiles, rewrites with the built-in rule set, and loads the rewritten probe type.</summary>
    public StagedProbe Stage(
        string assemblyName,
        string typeName,
        string source,
        IEnumerable<BuiltInRuleFamily>? families = null)
        => StageWith(
            assemblyName,
            typeName,
            source,
            BuiltInRuleSets.BuildDeterministicBcl(families ?? BuiltInRuleSets.AllFamilies));

    /// <summary>
    /// Compiles preview-language source with the SDK compiler, rewrites it with the controlled-task
    /// rule set, and loads the rewritten probe. This is opt-in so ordinary conformance fixtures keep
    /// their existing language-version contract.
    /// </summary>
    public StagedProbe StagePreviewControlledTasks(
        string assemblyName,
        string typeName,
        string source,
        bool optimize)
        => StageWith(
            assemblyName,
            typeName,
            source,
            BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies),
            optimize,
            LanguageVersion.Preview);

    /// <summary>Compiles and loads an ordinary probe without applying instrumentation.</summary>
    public UninstrumentedProbe CompileUninstrumented(
        string assemblyName,
        string typeName,
        string source,
        bool optimize = true)
    {
        string sourceDll = Compile(assemblyName, source, optimize);
        Assembly assembly = Assembly.LoadFrom(sourceDll);
        Type type = assembly.GetType(typeName, throwOnError: true)!;
        return new UninstrumentedProbe(type);
    }

    /// <summary>
    /// Compiles, rewrites with the controlled-task rule set, and loads the rewritten probe type. The
    /// <paramref name="optimize"/> flag selects Debug vs Release codegen, which the C# compiler lowers to
    /// materially different async state machines - both must be retargeted onto the controlled machinery.
    /// </summary>
    public StagedProbe StageControlledTasks(
        string assemblyName,
        string typeName,
        string source,
        bool optimize,
        IEnumerable<BuiltInRuleFamily>? families = null)
        => StageWith(
            assemblyName,
            typeName,
            source,
            BuiltInRuleSets.BuildControlledTasks(families ?? BuiltInRuleSets.AllFamilies),
            optimize);

    /// <summary>
    /// Compiles, rewrites with the controlled-task rule set less the rules <paramref name="exclude"/> selects, and loads
    /// the rewritten probe type: the rewrite a call site received before those rules existed.
    /// </summary>
    public StagedProbe StageControlledTasksWithout(
        string assemblyName,
        string typeName,
        string source,
        Func<RewriteRule, bool> exclude)
    {
        RewriteRuleSet full = BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies);
        return StageWith(
            assemblyName,
            typeName,
            source,
            new RewriteRuleSet(full.Id, full.Version, full.Rules.Where(rule => !exclude(rule))));
    }

    /// <summary>
    /// Compiles with runtime async and rewrites with the controlled-task rule set, returning the compiled assembly's
    /// path and the rewrite's outcome without loading either.
    /// </summary>
    public (string SourceDll, RewriteResult Result) RewriteRuntimeAsync(
        string assemblyName,
        string source,
        bool optimize,
        IEnumerable<BuiltInRuleFamily> families)
    {
        string sourceDll = Compile(assemblyName, source, optimize, runtimeAsync: true);
        RewriteResult result = RewriteEngine.Rewrite(new RewriteRequest(
            sourceDll,
            Path.Combine(StagingDir, assemblyName + ".dll"),
            BuiltInRuleSets.BuildControlledTasks(families),
            CreateOptions()));
        return (sourceDll, result);
    }

    private StagedProbe StageWith(
        string assemblyName,
        string typeName,
        string source,
        RewriteRuleSet ruleSet,
        bool optimize = true,
        LanguageVersion languageVersion = LanguageVersion.Latest)
    {
        string sourceDll = Compile(assemblyName, source, optimize, languageVersion: languageVersion);
        string stagedDll = Path.Combine(StagingDir, assemblyName + ".dll");

        RewriteOptions options = CreateOptions();
        RewriteResult result = RewriteEngine.Rewrite(new RewriteRequest(sourceDll, stagedDll, ruleSet, options));
        if (!result.Succeeded)
        {
            string diagnostics = string.Join(
                Environment.NewLine,
                result.Manifest.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}"));
            throw new InvalidOperationException($"Rewrite of '{assemblyName}' failed:{Environment.NewLine}{diagnostics}");
        }

        Assembly staged = Assembly.LoadFrom(stagedDll);
        Type type = staged.GetType(typeName, throwOnError: true)!;
        return new StagedProbe(type, result, sourceDll, stagedDll, ruleSet, options);
    }

    private static RewriteOptions CreateOptions() => new()
    {
        HardenExceptionHandlers = true,
        DetectUncontrolledTasks = true,
        ReplacementAssemblyPaths = [typeof(ControlledDateTime).Assembly.Location],
        ReferenceSearchDirectories = [AppContext.BaseDirectory],
        TargetRuntime = BuiltInRuleSets.SupportedRuntime,
    };

    private string Compile(
        string assemblyName,
        string source,
        bool optimize = true,
        bool runtimeAsync = false,
        LanguageVersion languageVersion = LanguageVersion.Latest)
    {
        var parseOptions = new CSharpParseOptions(languageVersion);
        if (runtimeAsync)
        {
            parseOptions = parseOptions.WithFeatures([new("runtime-async", "on")]);
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, parseOptions, path: assemblyName + ".cs", encoding: Encoding.UTF8);
        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: optimize ? OptimizationLevel.Release : OptimizationLevel.Debug,
            deterministic: true);
        CSharpCompilation compilation = CSharpCompilation.Create(assemblyName, [tree], RuntimeReferences, options);

        string path = Path.Combine(SourceDir, assemblyName + ".dll");
        EmitResult emit = compilation.Emit(path);
        if (!emit.Success)
        {
            string errors = string.Join(Environment.NewLine, emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException($"Compilation of '{assemblyName}' failed:{Environment.NewLine}{errors}");
        }

        return path;
    }

    private static MetadataReference[] LoadRuntimeReferences()
    {
        string trusted = (string)(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty);
        return trusted
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort: the loaded staged assembly keeps its file mapped on Windows.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>An ordinary compiled probe which has not been instrumented.</summary>
internal sealed record UninstrumentedProbe(Type Type)
{
    public MethodInfo Method(string name) =>
        Type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException($"Probe method '{name}' not found on '{Type.FullName}'.");
}

/// <summary>A rewritten, loaded probe type plus the rewrite outcome and inputs that produced it.</summary>
internal sealed record StagedProbe(
    Type Type,
    RewriteResult Result,
    string SourceDll,
    string StagedDll,
    RewriteRuleSet RuleSet,
    RewriteOptions Options)
{
    public MethodInfo Method(string name) =>
        Type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException($"Probe method '{name}' not found on '{Type.FullName}'.");
}
