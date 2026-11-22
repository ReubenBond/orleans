using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Rules;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Configuration;

/// <summary>
/// Verifies deterministic rule-set merging: a single source passes through unchanged, a later
/// document overrides an earlier document's same-id rule (recording the override), first-seen order
/// is preserved, and the merged version is a stable content hash.
/// </summary>
public sealed class RuleSetMergeTests
{
    private static RewriteRuleSet Set(string id, params (string RuleId, string Member)[] rules)
    {
        var builder = RewriteRuleSet.CreateBuilder(id, "1.0");
        foreach ((string ruleId, string member) in rules)
        {
            builder.RedirectCall(
                ruleId,
                MemberSignature.Method("App.T", member),
                RewriteReplacement.Method("Shims", "Shims.R", member));
        }

        return builder.Build();
    }

    [Fact]
    public void SingleSourcePassesThrough()
    {
        RewriteRuleSet only = Set("only", ("a", "A"));
        RuleSetMergeResult result = RuleSetMerge.Merge([("only.json", only)]);

        Assert.Same(only, result.RuleSet);
        Assert.Empty(result.Overrides);
    }

    [Fact]
    public void LaterDocumentOverridesEarlierRule()
    {
        RewriteRuleSet first = Set("first", ("shared", "Old"), ("keep", "Keep"));
        RewriteRuleSet second = Set("second", ("shared", "New"));

        RuleSetMergeResult result = RuleSetMerge.Merge([("first.json", first), ("second.json", second)]);

        Assert.Equal(RuleSetMerge.MergedId, result.RuleSet.Id);
        Assert.Equal(2, result.RuleSet.Rules.Length);

        RewriteRule shared = result.RuleSet.Rules.Single(r => r.Id == "shared");
        Assert.Equal("New", shared.Target.MemberName);

        // First-seen order is preserved: "shared" (seen first) precedes "keep".
        Assert.Equal("shared", result.RuleSet.Rules[0].Id);
        Assert.Equal("keep", result.RuleSet.Rules[1].Id);

        RuleSetOverride overridden = Assert.Single(result.Overrides);
        Assert.Equal("shared", overridden.RuleId);
        Assert.Equal("second.json", overridden.WinningSource);
    }

    [Fact]
    public void MergedVersionIsDeterministicContentHash()
    {
        RewriteRuleSet a = Set("a", ("x", "X"));
        RewriteRuleSet b = Set("b", ("y", "Y"));

        string first = RuleSetMerge.Merge([("a", a), ("b", b)]).RuleSet.Version;
        string second = RuleSetMerge.Merge([("a", a), ("b", b)]).RuleSet.Version;
        string reordered = RuleSetMerge.Merge([("b", b), ("a", a)]).RuleSet.Version;

        Assert.Equal(first, second);
        Assert.NotEqual(first, reordered);
    }
}
