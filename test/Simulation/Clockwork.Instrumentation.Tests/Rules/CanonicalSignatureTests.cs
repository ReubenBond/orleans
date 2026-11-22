using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Rules;

/// <summary>Verifies that signature inputs use one unambiguous, versioned canonical encoding.</summary>
public sealed class CanonicalSignatureTests
{
    [Fact]
    public void MemberSignatureCanonicalEncodingHasStableGoldenValue()
    {
        MemberSignature signature =
            MemberSignature.Method("Type,|:\n", "M|,\n", "A,B", "C");

        Assert.Equal(
            "clockwork-canonical-v1;F5:$typeS15:MemberSignature;" +
            "F21:DeclaringTypeFullNameS8:Type,|:\n;" +
            "F10:MemberNameS4:M|,\n;" +
            "F22:ParameterTypeFullNamesA2:S3:A,B;S1:C;;",
            signature.ToCanonicalString());
    }

    [Fact]
    public void MemberParameterBoundariesCannotCollide()
    {
        var oneCommaContainingParameter = new MemberSignature("T", "M", ["A,B"]);
        var twoParameters = new MemberSignature("T", "M", ["A", "B"]);
        var unconstrained = new MemberSignature("T", "M");
        var noParameters = new MemberSignature("T", "M", []);
        var typeOnly = new MemberSignature("T");
        var emptyMemberName = new MemberSignature("T", string.Empty);

        Assert.NotEqual(
            oneCommaContainingParameter.ToCanonicalString(),
            twoParameters.ToCanonicalString());
        Assert.NotEqual(unconstrained.ToCanonicalString(), noParameters.ToCanonicalString());
        Assert.NotEqual(typeOnly.ToCanonicalString(), emptyMemberName.ToCanonicalString());
    }

    [Fact]
    public void ReplacementParameterAndFieldBoundariesCannotCollide()
    {
        var oneCommaContainingParameter =
            new RewriteReplacement("A!|,\n", "T::|,\n", "M|,\n", ["P,Q"]);
        var twoParameters =
            new RewriteReplacement("A!|,\n", "T::|,\n", "M|,\n", ["P", "Q"]);
        var unconstrained = new RewriteReplacement("A", "T", "M");
        var noParameters = new RewriteReplacement("A", "T", "M", []);
        var typeOnly = new RewriteReplacement("A", "T");
        var emptyMemberName = new RewriteReplacement("A", "T", string.Empty);

        Assert.NotEqual(
            oneCommaContainingParameter.ToCanonicalString(),
            twoParameters.ToCanonicalString());
        Assert.NotEqual(unconstrained.ToCanonicalString(), noParameters.ToCanonicalString());
        Assert.NotEqual(typeOnly.ToCanonicalString(), emptyMemberName.ToCanonicalString());
    }

    [Fact]
    public void RuleAndRuleSetDelimiterAdversariesProduceDistinctSignatures()
    {
        RewriteReplacement replacement = RewriteReplacement.Type("A", "T");
        RewriteRule delimiterInId = RewriteRule.SubstituteType(
            "id|SubstituteType",
            "Target",
            replacement);
        RewriteRule delimiterInTarget = RewriteRule.SubstituteType(
            "id",
            "SubstituteType|Target",
            replacement);
        var newlineInId = new RewriteRuleSet("set\nversion:value", "tail", []);
        var newlineInVersion = new RewriteRuleSet("set", "value\nversion:tail", []);

        Assert.NotEqual(delimiterInId.ToCanonicalString(), delimiterInTarget.ToCanonicalString());
        Assert.NotEqual(
            new RewriteRuleSet("rules", "1", [delimiterInId]).ComputeSignature(),
            new RewriteRuleSet("rules", "1", [delimiterInTarget]).ComputeSignature());
        Assert.NotEqual(newlineInId.ComputeSignature(), newlineInVersion.ComputeSignature());
    }

    [Fact]
    public void OptionalRuleAndRuntimeFieldsAreDistinct()
    {
        RewriteRule baseline = RewriteRule.SubstituteType(
            "id",
            "Target",
            RewriteReplacement.Type("A", "T"));

        Assert.NotEqual(
            baseline.ToCanonicalString(),
            (baseline with { Description = string.Empty }).ToCanonicalString());
        Assert.NotEqual(
            RuntimeVersionRange.All.ToCanonicalString(),
            RuntimeVersionRange.AtLeast(new Version(1, 0)).ToCanonicalString());
        Assert.NotEqual(
            RuntimeVersionRange.AtLeast(new Version(1, 0)).ToCanonicalString(),
            RuntimeVersionRange.AtMost(new Version(1, 0)).ToCanonicalString());
    }

    [Fact]
    public void ConfigurationAndRewriteOptionListsCannotCollide()
    {
        var escapedComma = new InstrumentationConfiguration
        {
            IncludePatterns = ["A,B"],
        };
        var literalEscapeText = new InstrumentationConfiguration
        {
            IncludePatterns = ["A%2CB"],
        };
        var onePath = new RewriteOptions
        {
            TargetRuntime = Infrastructure.RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = ["A,B"],
        };
        var twoPaths = new RewriteOptions
        {
            TargetRuntime = Infrastructure.RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = ["A", "B"],
        };

        Assert.NotEqual(escapedComma.ComputeSignature(), literalEscapeText.ComputeSignature());
        Assert.NotEqual(onePath.ComputeSemanticFingerprint(), twoPaths.ComputeSemanticFingerprint());
    }

    [Fact]
    public void RuleOrderingRemainsDeterministicAndSignificant()
    {
        RewriteRule first = RewriteRule.SubstituteType(
            "first",
            "Target.First",
            RewriteReplacement.Type("A", "T.First"));
        RewriteRule second = RewriteRule.SubstituteType(
            "second",
            "Target.Second",
            RewriteReplacement.Type("A", "T.Second"));

        string baseline = new RewriteRuleSet("rules", "1", [first, second]).ComputeSignature();

        Assert.Equal(
            baseline,
            new RewriteRuleSet("rules", "1", [first, second]).ComputeSignature());
        Assert.NotEqual(
            baseline,
            new RewriteRuleSet("rules", "1", [second, first]).ComputeSignature());
    }
}
