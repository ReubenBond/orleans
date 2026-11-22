using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Keeps every hand-written union under the union laws (conventions.md §15): each <c>[Union]</c> type of <c>src</c>
/// which declares <c>HasValue</c>, as the non-boxing pattern does and a union declaration does not, has an adapter under
/// <c>tests</c>, a type implementing <c>IUnionLawsAdapter&lt;TUnion&gt;</c> for it, which a test passes to
/// <c>UnionLaws.Check</c>.
/// </summary>
public sealed class UnionLawsCoverageTests
{
    [Fact]
    public void EveryHandWrittenUnionHasAUnionLawsAdapterWhichATestChecks()
    {
        var unions = HandWrittenUnions();
        Assert.NotEmpty(unions);
        var missing = unions.Except(CheckedUnions()).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            $"Pass an IUnionLawsAdapter (tests/shared/UnionLaws) for each hand-written union to UnionLaws.Check: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Returns the full name of every hand-written union in a production assembly.
    /// </summary>
    private static HashSet<string> HandWrittenUnions() =>
    [
        .. Repository.ProductionAssemblies()
            .Select(static assembly => Assembly.Load(Path.GetFileNameWithoutExtension(assembly.Path)))
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => type.IsDefined(typeof(UnionAttribute), inherit: false)
                && type.GetProperty("HasValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly) is not null)
            .Select(static type => type.FullName!),
    ];

    /// <summary>
    /// Returns the full names the union of each adapter under <c>tests</c> may have, for every adapter a call
    /// <c>UnionLaws.Check(new Adapter(), …)</c> in its file checks: the union's name as written, and qualified by each
    /// namespace the file imports or encloses it in.
    /// </summary>
    private static HashSet<string> CheckedUnions()
    {
        var tests = Repository.StorageRoot;
        var unions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
            var checkedAdapters = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(static invocation => invocation.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "UnionLaws" }, Name.Identifier.Text: "Check" })
                .Select(static invocation => invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression)
                .OfType<ObjectCreationExpressionSyntax>()
                .Select(static creation => creation.Type.ToString())
                .ToHashSet(StringComparer.Ordinal);
            if (checkedAdapters.Count == 0)
            {
                continue;
            }

            var imports = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(static directive => directive.Alias is null && directive.StaticKeyword.Text.Length == 0 && directive.Name is not null)
                .Select(static directive => directive.Name!.ToString())
                .ToList();
            foreach (var adapter in root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(adapter => checkedAdapters.Contains(adapter.Identifier.Text)))
            {
                var enclosing = adapter.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(static space => space.Name.ToString()).ToList();
                var namespaces = imports.Concat(enclosing.SelectMany(static space => Prefixes(space))).ToList();
                foreach (var union in adapter.BaseList?.Types.Select(static baseType => baseType.Type).OfType<GenericNameSyntax>()
                    .Where(static name => name.Identifier.Text == "IUnionLawsAdapter" && name.TypeArgumentList.Arguments.Count == 1)
                    .Select(static name => name.TypeArgumentList.Arguments[0].ToString()) ?? [])
                {
                    _ = unions.Add(union);
                    unions.UnionWith(namespaces.Select(space => space + "." + union));
                }
            }
        }

        return unions;
    }

    /// <summary>
    /// Returns a namespace and every namespace enclosing it, whose types its code names unqualified.
    /// </summary>
    private static IEnumerable<string> Prefixes(string space)
    {
        for (var end = space.Length; end > 0; end = space.LastIndexOf('.', end - 1))
        {
            yield return space[..end];
        }
    }
}
