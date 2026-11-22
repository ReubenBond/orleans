using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Keeps every validator's result consumed (conventions.md §15): CA1806 reports a call whose result is ignored only for
/// the methods <c>dotnet_code_quality.CA1806.additional_use_results_methods</c> lists in <c>eng/durable-storage/DurableStorage.globalconfig</c>, so every
/// synchronous method of <c>src</c> which returns <c>InvalidDataException?</c>, a rejection or <see langword="null"/>
/// when its input is valid, is listed there by its documentation ID, and the SDK's CA1806 reports a canary which
/// discards one under the repository's settings.
/// </summary>
public sealed class ValidatorTests
{
    private const string OptionKey = "dotnet_code_quality.CA1806.additional_use_results_methods";

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(static () =>
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static path => MetadataReference.CreateFromFile(path)),
    ]);

    [Fact]
    public void EveryMethodReturningARejectionOrNullIsListedForCA1806()
    {
        var listed = ListedMethods();
        var validators = Validators();
        Assert.NotEmpty(validators);
        var missing = validators.Except(listed).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            $"Add these validators to {OptionKey} in eng/durable-storage/DurableStorage.globalconfig, separated by '|': {string.Join("|", missing)}");
        var stale = listed.Except(validators).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, $"{OptionKey} lists methods which no longer return InvalidDataException?: {string.Join(", ", stale)}");
    }

    /// <summary>
    /// A canary which discards a listed validator's result draws CA1806 from the SDK's analyzer under the repository's
    /// <c>eng/durable-storage/DurableStorage.globalconfig</c>, so the list takes effect, at warning severity, which fails the build; one which consumes it
    /// draws none.
    /// </summary>
    [Fact]
    public async Task CA1806ReportsADiscardedValidatorUnderTheRepositorysSettings()
    {
        Assert.Contains(ListedMethods(), static id => id.StartsWith("M:Orleans.Durable.Storage.Engine.Wal.WalFormat.ValidatePage(", StringComparison.Ordinal));
        var discarded = await CA1806Async("WalFormat.ValidatePage(page);");
        var report = Assert.Single(discarded);
        Assert.Equal(DiagnosticSeverity.Warning, report.Severity);
        Assert.Empty(await CA1806Async("_ = WalFormat.ValidatePage(page) ?? throw new InvalidDataException();"));
    }

    /// <summary>
    /// Returns the documentation ID of every method declared in a production assembly, compiler-generated ones aside,
    /// whose return type is <see cref="InvalidDataException"/>, unless annotated not null.
    /// </summary>
    private static HashSet<string> Validators()
    {
        var references = Repository.ProductionAssemblies().ToDictionary(static assembly => assembly.Path, static assembly => MetadataReference.CreateFromFile(assembly.Path));
        var compilation = CSharpCompilation.Create(
            "Validators",
            references: [.. References.Value.Where(reference => !references.ContainsKey(((PortableExecutableReference)reference).FilePath!)), .. references.Values],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithMetadataImportOptions(MetadataImportOptions.All));
        var rejection = compilation.GetTypeByMetadataName(typeof(InvalidDataException).FullName!)!;
        return
        [
            .. references.Values
                .Select(reference => (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(reference)!)
                .SelectMany(static assembly => Types(assembly.GlobalNamespace))
                .Where(static type => type.CanBeReferencedByName)
                .SelectMany(static type => type.GetMembers().OfType<IMethodSymbol>())
                .Where(method => method.MethodKind == MethodKind.Ordinary
                    && method.CanBeReferencedByName
                    && SymbolEqualityComparer.Default.Equals(method.ReturnType, rejection)
                    && method.ReturnNullableAnnotation != NullableAnnotation.NotAnnotated)
                .Select(static method => DocumentationCommentId.CreateDeclarationId(method)!),
        ];
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol space) =>
        space.GetNamespaceMembers().SelectMany(Types).Concat(space.GetTypeMembers().SelectMany(Nested));

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) => [type, .. type.GetTypeMembers().SelectMany(Nested)];

    /// <summary>Storage's global analyzer configuration, which every Storage project applies.</summary>
    private static string AnalyzerConfigPath => Path.Combine(Repository.FindRoot(), "eng", "durable-storage", "DurableStorage.globalconfig");

    private static HashSet<string> ListedMethods()
    {
        var line = File.ReadLines(AnalyzerConfigPath)
            .Select(static text => text.Trim())
            .SingleOrDefault(static text => text.StartsWith(OptionKey, StringComparison.Ordinal));
        if (line is null)
        {
            return [];
        }

        var value = line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..];
        return [.. value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// Compiles a canary in the Engine's directory whose method runs <paramref name="statement"/> over a page, with the
    /// options Storage's global analyzer configuration gives it, and returns the CA1806 reports of the SDK's analyzers.
    /// </summary>
    private static async Task<ImmutableArray<Diagnostic>> CA1806Async(string statement)
    {
        var root = Repository.FindRoot();
        var path = Path.Combine(root, "src", Components.Engine, "CA1806Canary.cs");
        var config = AnalyzerConfig.Parse(await File.ReadAllTextAsync(AnalyzerConfigPath, TestContext.Current.CancellationToken), AnalyzerConfigPath);
        // A global configuration's options apply to every source file; a section's, to the paths it matches.
        var set = AnalyzerConfigSet.Create(ImmutableArray.Create(config));
        var local = set.GetOptionsForSourcePath(path);
        var treeOptions = set.GlobalConfigOptions.TreeOptions.SetItems(local.TreeOptions);
        var analyzerOptions = set.GlobalConfigOptions.AnalyzerOptions.SetItems(local.AnalyzerOptions);
        var source = $$"""
            using System.IO;
            using Orleans.Durable.Storage.Engine.Wal;

            namespace Orleans.Durable.Storage.Architecture.Tests.Canary;

            internal static class CA1806Canary
            {
                internal static void Check(byte[] page)
                {
                    {{statement}}
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), path, cancellationToken: TestContext.Current.CancellationToken);
        // The canary takes the name of an assembly the Engine grants its internals to, so it can call WalFormat.
        var engine = Repository.ProductionAssemblies().Single(static assembly => assembly.Project == Components.Engine).Path;
        var compilation = CSharpCompilation.Create(
            "Orleans.Durable.Storage.Engine.Wal.Tests",
            [tree],
            [.. References.Value.Where(reference => !string.Equals(((PortableExecutableReference)reference).FilePath, engine, StringComparison.OrdinalIgnoreCase)), MetadataReference.CreateFromFile(engine)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
                .WithSpecificDiagnosticOptions(treeOptions));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var analyzers = typeof(ValidatorTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(static attribute => attribute.Key == "NetAnalyzer")
            .SelectMany(static attribute => new AnalyzerFileReference(attribute.Value!, AnalyzerLoader.Instance).GetAnalyzers(LanguageNames.CSharp))
            .ToImmutableArray();
        Assert.NotEmpty(analyzers);
        var diagnostics = await compilation
            .WithAnalyzers(analyzers, new AnalyzerOptions([], new ConfigOptionsProvider(analyzerOptions)))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return [.. diagnostics.Where(static diagnostic => diagnostic.Id == "CA1806")];
    }

    /// <summary>
    /// Gives every tree the analyzer options Storage's global analyzer configuration gives the canary's path.
    /// </summary>
    private sealed class ConfigOptionsProvider(ImmutableDictionary<string, string> analyzerOptions) : AnalyzerConfigOptionsProvider
    {
        private readonly ConfigOptions _options = new(analyzerOptions);

        public override AnalyzerConfigOptions GlobalOptions => _options;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
    }

    private sealed class ConfigOptions(ImmutableDictionary<string, string> options) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => options.TryGetValue(key, out value);
    }

    /// <summary>
    /// Loads the SDK's analyzer assemblies into the test's load context.
    /// </summary>
    private sealed class AnalyzerLoader : IAnalyzerAssemblyLoader
    {
        public static AnalyzerLoader Instance { get; } = new();

        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }
}
