using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class EmissionSyntaxTests
{
    [Theory]
    [InlineData("name", "@name")]
    [InlineData("instance.Value", "instance . Value")]
    [InlineData("codec.Read(ref reader, header)", "codec.Read( ref reader , header )")]
    public void EquivalentFragments_HaveEqualHashes(string left, string right)
    {
        var first = new EmissionSyntax<ExpressionSyntax>(SyntaxFactory.ParseExpression(left));
        var second = new EmissionSyntax<ExpressionSyntax>(SyntaxFactory.ParseExpression(right));
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Single(new HashSet<EmissionSyntax<ExpressionSyntax>> { first, second });
    }

    [Theory]
    [InlineData("instance.First", "instance.Second")]
    [InlineData("codec.Read(ref reader, header)", "codec.Read(reader, header)")]
    [InlineData("new Payload()", "default(Payload)")]
    public void ChangedFragments_AreUnequal(string left, string right)
        => Assert.NotEqual(
            new EmissionSyntax<ExpressionSyntax>(SyntaxFactory.ParseExpression(left)),
            new EmissionSyntax<ExpressionSyntax>(SyntaxFactory.ParseExpression(right)));
}
