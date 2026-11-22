using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Pins how tests use <c>DebugAssertions.RaisedCount</c> (verification §11.2): it counts every assertion the test host
/// has raised, in every test running in parallel, so it is only a mark, the first sequence number an owner takes its own
/// records from with <c>TakeRaisedBy(test, since)</c> or <c>FirstRaisedBy(test, since)</c>. A member which compares it
/// with itself, reading it before and after, judges other tests' assertions too (#366). So each member outside
/// <c>DebugAssertions</c> reads it at most once.
/// </summary>
public sealed class DebugAssertionMarkTests
{
    /// <summary>Each member of the test sources reads <c>DebugAssertions.RaisedCount</c> at most once.</summary>
    [Fact]
    public void EachMemberReadsTheRaisedCountOnlyAsOneMark()
    {
        var root = Repository.FindRoot();
        var violations = Directory.EnumerateFiles(Repository.StorageRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static file => !Repository.IsBuildOutput(file))
            .SelectMany(file => RepeatedReads(Path.GetRelativePath(root, file), File.ReadAllText(file)))
            .ToList();

        Assert.True(violations.Count == 0, "Members which read DebugAssertions.RaisedCount more than once, comparing the count of every test's assertions; take a mark once and pass it to TakeRaisedBy or FirstRaisedBy:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>The rule fires on a member which compares the count before and after, as #366's test did.</summary>
    [Fact]
    public void TheRuleFlagsAMemberWhichComparesTheCountBeforeAndAfter()
    {
        const string Compares = """
            class Fixture
            {
                async Task Compares()
                {
                    var raised = DebugAssertions.RaisedCount;
                    await RunAsync();
                    Assert.Equal(raised, DebugAssertions.RaisedCount);
                }

                async Task Marks()
                {
                    var (test, since) = (TestContext.Current.Test, DebugAssertions.RaisedCount);
                    await RunAsync();
                    Assert.Null(DebugAssertions.FirstRaisedBy(test, since));
                }
            }
            """;

        Assert.Equal(["Fixture.cs: Compares reads it 2 times"], RepeatedReads("Fixture.cs", Compares));
    }

    /// <summary>
    /// Returns, for each member of <paramref name="source"/> which reads <c>DebugAssertions.RaisedCount</c> more than
    /// once, its file, name, and count of reads.
    /// </summary>
    private static IEnumerable<string> RepeatedReads(string file, string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(static access => access is { Expression: IdentifierNameSyntax { Identifier.ValueText: "DebugAssertions" }, Name.Identifier.ValueText: "RaisedCount" })
            .GroupBy(static access => access.Ancestors().First(static node => node is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseFieldDeclarationSyntax))
            .Where(static reads => reads.Count() > 1)
            .Select(reads => $"{file}: {Name(reads.Key)} reads it {reads.Count()} times");

    private static string Name(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        BaseFieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(static variable => variable.Identifier.ValueText)),
        _ => member.ToString(),
    };
}
