using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using static Orleans.Durable.Storage.Architecture.Tests.Namespaces;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Pins spec README §2.6: every namespace in a production project lies within a root of a component the project compiles
/// or is the namespace of a type such a component lists, and every namespace in any other project starts with the
/// project's name; and every type is declared in a named namespace, since a type in the global namespace escapes the
/// project prefix and can collide with any assembly's. No analyzer checks this.
/// </summary>
public sealed partial class NamespaceTests
{
    /// <summary>
    /// The namespace of the binders the configuration binding generator emits in the composition, which binds the silo's
    /// options (orleans.md §6).
    /// </summary>
    private const string ConfigurationBindingNamespace = "Microsoft.Extensions.Configuration.Binder.SourceGeneration";


    /// <summary>
    /// Source files which several projects link share one namespace instead of any linking project's (spec README §2.6).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SharedSourceNamespaces =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Path.Combine("test", "Durable.Storage", "shared")] = "Orleans.Durable.Storage.Testing",
            [Path.Combine("test", "Durable.Storage", "apphost", "shared")] = "Orleans.Durable.Storage.AppHost",
        };

    /// <summary>
    /// Checks the compiled types, so generated code (JSON contexts, serializers) is covered as well as source. It reads
    /// each assembly at the path <see cref="Repository.ProductionAssemblies"/> gives, the paths the plane checks scan.
    /// </summary>
    [Fact]
    public void ProductionAssemblyTypesUseTheirProjectNamespace()
    {
        var violations = new List<string>();
        foreach (var (project, assemblyPath) in Repository.ProductionAssemblies())
        {
            Assert.True(File.Exists(assemblyPath), $"{project} is not referenced by Orleans.Durable.Storage.Architecture.Tests.");

            using var reader = new PEReader(File.OpenRead(assemblyPath));
            var metadata = reader.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                if (type.IsNested)
                {
                    continue;
                }

                var typeNamespace = metadata.GetString(type.Namespace);
                if (!IsCompilerSynthesized(metadata, type) && !IsAllowedCompiledNamespace(project, typeNamespace))
                {
                    violations.Add($"{project}: {typeNamespace}.{metadata.GetString(type.Name)}");
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Checks the declared namespaces of every project, including tests, benchmarks, the fuzz harness, and AppHosts, and of
    /// the source files which several projects share, and that a file which declares a type declares a namespace.
    /// </summary>
    [Fact]
    public void SourceNamespacesStartWithTheirProjectName()
    {
        var root = Repository.FindRoot();
        var projectDirectories = Repository.StorageProjectFiles()
            .ToDictionary(
                static projectFile => Path.GetDirectoryName(projectFile)!,
                static projectFile => Path.GetFileNameWithoutExtension(projectFile),
                StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var (directory, project) in projectDirectories)
        {
            CheckSourceNamespaces(root, directory, Allowed(project), projectDirectories, violations);
        }

        foreach (var (directory, sharedNamespace) in SharedSourceNamespaces)
        {
            CheckSourceNamespaces(root, Path.Combine(root, directory), [sharedNamespace], projectDirectories, violations);
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// The compiler emits <c>&lt;Module&gt;</c>, <c>&lt;PrivateImplementationDetails&gt;</c>, and its collection and
    /// inline-array helpers in the global namespace, and puts top-level statements in a global <c>Program</c> whose
    /// entry point is <c>&lt;Main&gt;$</c>. No namespace of these is declared in the project.
    /// </summary>
    private static bool IsCompilerSynthesized(MetadataReader metadata, TypeDefinition type)
    {
        if (!type.Namespace.IsNil)
        {
            return false;
        }

        return metadata.GetString(type.Name).StartsWith('<')
            || type.GetMethods().Any(method => metadata.StringComparer.Equals(metadata.GetMethodDefinition(method).Name, "<Main>$"));
    }

    /// <summary>
    /// BCL source generators, such as the regular expression generator, emit types in System namespaces, and Orleans' code
    /// generator emits the grain message serializers and proxies of the composition and of the technology packages Orleans
    /// hosts in <c>OrleansCodeGen</c> namespaces, in the project which owns their protocol (spec README §2.5).
    /// </summary>
    private static bool IsAllowedCompiledNamespace(string project, string typeNamespace) =>
        Allowed(project).Any(allowed => IsWithin(typeNamespace, allowed))
        || IsWithin(typeNamespace, "System")
        || LayeringTests.OrleansHosts.Contains(project) && (IsWithin(typeNamespace, "OrleansCodeGen") || IsWithin(typeNamespace, ConfigurationBindingNamespace));

    private static void CheckSourceNamespaces(
        string root,
        string directory,
        string[] expectedNamespaces,
        IReadOnlyDictionary<string, string> projectDirectories,
        List<string> violations)
    {
        foreach (var file in EnumerateSourceFiles(directory, projectDirectories))
        {
            var text = File.ReadAllText(file);
            if (!NamespaceDeclaration().IsMatch(text) && TypeDeclaration().Match(text) is { Success: true } type)
            {
                violations.Add($"{Path.GetRelativePath(root, file)}: {type.Groups["type"].Value} is declared outside any namespace");
            }

            foreach (Match match in NamespaceDeclaration().Matches(text))
            {
                var declared = match.Groups["namespace"].Value;

                if (!expectedNamespaces.Any(expected => IsWithin(declared, expected)))
                {
                    violations.Add($"{Path.GetRelativePath(root, file)}: {declared} (expected {string.Join(" or ", expectedNamespaces)})");
                }
            }
        }
    }

    /// <summary>Enumerates the C# files below a directory, excluding build output and nested projects.</summary>
    private static IEnumerable<string> EnumerateSourceFiles(string directory, IReadOnlyDictionary<string, string> projectDirectories)
    {
        var pending = new Stack<string>([directory]);
        while (pending.TryPop(out var next))
        {
            foreach (var file in Directory.EnumerateFiles(next, "*.cs"))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(next))
            {
                var name = Path.GetFileName(child);
                if (name is not ("bin" or "obj") && !projectDirectories.ContainsKey(child))
                {
                    pending.Push(child);
                }
            }
        }
    }

    [GeneratedRegex(@"^\s*namespace\s+(?<namespace>[\w.]+)", RegexOptions.Multiline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex NamespaceDeclaration();

    /// <summary>A type declaration at the start of a line: a class, struct, interface, enum, record, delegate, or union.</summary>
    [GeneratedRegex(
        @"^\s*(?:\[[^\]\n]*\]\s*)*(?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|ref|unsafe|new)\s+)*(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?|delegate\s+[\w<>?,\s.]+?|union)\s+(?<type>\w+)",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 10_000)]
    private static partial Regex TypeDeclaration();
}
