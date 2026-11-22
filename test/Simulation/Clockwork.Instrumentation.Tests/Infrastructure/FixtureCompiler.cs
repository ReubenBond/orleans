using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>
/// The debug-symbol form a compiled fixture assembly should carry.
/// </summary>
internal enum FixtureSymbols
{
    /// <summary>No PDB is emitted (release, no symbols).</summary>
    None,

    /// <summary>A separate portable <c>.pdb</c> file is emitted alongside the assembly.</summary>
    PortableFile,

    /// <summary>A portable PDB is embedded into the assembly.</summary>
    Embedded,
}

/// <summary>
/// Compiles small C# source strings into on-disk assemblies at test time using Roslyn, so the golden
/// corpus can exercise the rewrite engine against real IL shapes (calls, generics, by-ref, arrays,
/// modifiers, constrained calls, delegates, async/iterators, nested types, exception handlers) and
/// real portable/embedded/absent symbols - without adding fixture projects to the build.
/// </summary>
internal static class FixtureCompiler
{
    private static readonly MetadataReference[] RuntimeReferences = LoadRuntimeReferences();

    /// <summary>
    /// Compiles <paramref name="source"/> into <paramref name="outputDirectory"/> and returns the
    /// path of the produced assembly.
    /// </summary>
    public static string Compile(
        string assemblyName,
        string source,
        string outputDirectory,
        FixtureSymbols symbols,
        bool optimize,
        IEnumerable<string>? additionalReferencePaths = null,
        string? strongNameKeyFile = null,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest),
            path: assemblyName + ".cs",
            encoding: Encoding.UTF8);

        var references = new List<MetadataReference>(RuntimeReferences);
        if (additionalReferencePaths is not null)
        {
            foreach (string path in additionalReferencePaths)
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var options = new CSharpCompilationOptions(
            outputKind,
            optimizationLevel: optimize ? OptimizationLevel.Release : OptimizationLevel.Debug,
            allowUnsafe: true,
            deterministic: true);

        if (strongNameKeyFile is not null)
        {
            options = options
                .WithCryptoKeyFile(strongNameKeyFile)
                .WithStrongNameProvider(new DesktopStrongNameProvider());
        }

        CSharpCompilation compilation = CSharpCompilation.Create(assemblyName, [tree], references, options);

        Directory.CreateDirectory(outputDirectory);
        string assemblyPath = Path.Combine(outputDirectory, assemblyName + ".dll");
        string pdbPath = Path.ChangeExtension(assemblyPath, "pdb");

        EmitResult result;
        using (var assemblyStream = new FileStream(assemblyPath, FileMode.Create, FileAccess.Write))
        {
            switch (symbols)
            {
                case FixtureSymbols.PortableFile:
                    using (var pdbStream = new FileStream(pdbPath, FileMode.Create, FileAccess.Write))
                    {
                        result = compilation.Emit(
                            assemblyStream,
                            pdbStream,
                            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
                    }

                    break;

                case FixtureSymbols.Embedded:
                    result = compilation.Emit(
                        assemblyStream,
                        options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
                    break;

                default:
                    result = compilation.Emit(assemblyStream);
                    break;
            }
        }

        if (!result.Success)
        {
            var builder = new StringBuilder($"Fixture '{assemblyName}' failed to compile:");
            foreach (Diagnostic diagnostic in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                builder.Append('\n').Append(diagnostic);
            }

            throw new InvalidOperationException(builder.ToString());
        }

        return assemblyPath;
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
}
