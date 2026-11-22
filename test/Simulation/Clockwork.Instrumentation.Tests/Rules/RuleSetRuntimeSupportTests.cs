using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Rules;

public sealed class RuleSetRuntimeSupportTests
{
    private static readonly Version Net10 = new(10, 0);
    private static readonly Version Net11 = new(11, 0);
    private static readonly Version Net12 = new(12, 0);

    private static readonly RewriteOperationKind[] MemberOperations =
    [
        RewriteOperationKind.RedirectCall,
        RewriteOperationKind.RedirectNewObj,
        RewriteOperationKind.WrapAfterCall,
        RewriteOperationKind.InjectRejection,
    ];

    public static TheoryData<RewriteOperationKind, RewriteOperationKind> OperationPairs()
    {
        var data = new TheoryData<RewriteOperationKind, RewriteOperationKind>();
        foreach (RewriteOperationKind older in MemberOperations)
        {
            foreach (RewriteOperationKind current in MemberOperations)
            {
                data.Add(older, current);
            }
        }

        return data;
    }

    [Fact]
    public void RuleWithoutAnAlternativeForTheTargetIsUnsupported()
    {
        RewriteRule now10 = Rule("now.net10", "get_Now", new RuntimeVersionRange(Net10, Net10));
        RewriteRule now11 = Rule("now.net11", "get_Now", new RuntimeVersionRange(Net11, Net12));
        RewriteRule today = Rule("today", "get_Today", RuntimeVersionRange.AtLeast(Net10));
        RewriteRule utcNow10 = Rule("utcnow.net10", "get_UtcNow", new RuntimeVersionRange(Net10, Net10));
        var ruleSet = new RewriteRuleSet("fixture", "1.0", [now10, now11, today, utcNow10]);

        RuleSetRuntimeSupport support = RuleSetRuntimeSupport.Evaluate(ruleSet, Net11);

        Assert.False(support.IsSupported);
        Assert.Equal(utcNow10, Assert.Single(support.UnsupportedRules));
        Assert.Equal([now11, today], support.SupportedRules);
        Assert.Equal(new RuntimeVersionRange(Net11, Net12), support.SupportedRuntimes);
    }

    // The matcher is the oracle: a rule set supports the target exactly when every site an out-of-range rule would
    // rewrite is rewritten by the rules the matcher indexes for the target.
    [Theory]
    [MemberData(nameof(OperationPairs))]
    public void AnAlternativeMustRewriteEverySiteTheRuleItReplacesRewrites(
        RewriteOperationKind older,
        RewriteOperationKind current)
    {
        var ruleSet = new RewriteRuleSet(
            "fixture",
            "1.0",
            [
                ConstructorRule("ctor.net10", older, new RuntimeVersionRange(Net10, Net10)),
                ConstructorRule("ctor.net11", current, new RuntimeVersionRange(Net11, Net11)),
            ]);
        bool[] siteKinds = [false, true];
        bool[] olderSites = [.. siteKinds.Where(isNewObj => Matches(older, isNewObj))];

        RuleSetRuntimeSupport support = RuleSetRuntimeSupport.Evaluate(ruleSet, Net11);

        Assert.Equal(olderSites.All(isNewObj => Matches(current, isNewObj)), support.IsSupported);
        if (support.IsSupported)
        {
            var matcher = new RewriteRuleMatcher(support);
            Assert.All(olderSites, isNewObj => Assert.True(matcher.TryMatchInvocation(ConstructorReference(), isNewObj, out _)));
        }
    }

    [Fact]
    public void BuiltInRulesSupportOnlyTheirRuntimeAndFailOtherTargetsOnce()
    {
        RewriteRuleSet ruleSet = BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies);

        RuleSetRuntimeSupport supported = RuleSetRuntimeSupport.Evaluate(ruleSet, BuiltInRuleSets.SupportedRuntime);
        RuleSetRuntimeSupport unsupported = RuleSetRuntimeSupport.Evaluate(ruleSet, Net12);
        RewriteDiagnostic diagnostic = unsupported.ToDiagnostic("configured");

        Assert.True(supported.IsSupported);
        Assert.Equal(
            new RuntimeVersionRange(BuiltInRuleSets.SupportedRuntime, BuiltInRuleSets.SupportedRuntime),
            supported.SupportedRuntimes);
        Assert.Equal(ruleSet.Rules.Length, unsupported.UnsupportedRules.Length);
        Assert.Equal(RewriteDiagnosticIds.RuntimeOutOfRange, diagnostic.Id);
        Assert.True(diagnostic.IsError);
        Assert.Contains("Target runtime 12.0 (configured)", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains($"11.0 ({ruleSet.Rules.Length} rules)", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, 0, 2)]
    [InlineData(11, 0, 0)]
    public void PatchVersionsAreRejected(int major, int minor, int build)
    {
        var ruleSet = new RewriteRuleSet("fixture", "1.0", [Rule("now", "get_Now", RuntimeVersionRange.All)]);
        var patched = new RewriteRuleSet(
            "fixture",
            "1.0",
            [Rule("now", "get_Now", RuntimeVersionRange.AtMost(new Version(major, minor, build)))]);

        Assert.Throws<ArgumentException>(() => RuleSetRuntimeSupport.Evaluate(ruleSet, new Version(major, minor, build)));
        Assert.Throws<ArgumentException>(() => RuleSetRuntimeSupport.Evaluate(patched, Net11));
    }

    [Fact]
    public void IntersectionIncludesExactlyTheVersionsBothRangesInclude()
    {
        Version?[] bounds = [null, .. Enumerable.Range(9, 4).Select(major => new Version(major, 0))];
        RuntimeVersionRange[] ranges =
        [
            .. bounds.SelectMany(minimum => bounds.Select(maximum => new RuntimeVersionRange(minimum, maximum)))
                .Where(range => range.Minimum is null || range.Maximum is null || range.Minimum <= range.Maximum),
        ];
        Version[] probes = [.. Enumerable.Range(8, 6).Select(major => new Version(major, 0))];

        foreach (RuntimeVersionRange first in ranges)
        {
            foreach (RuntimeVersionRange second in ranges)
            {
                RuntimeVersionRange intersection = first.Intersect(second);
                foreach (Version probe in probes)
                {
                    Assert.True(
                        intersection.Includes(probe) == (first.Includes(probe) && second.Includes(probe)),
                        $"{first.ToDisplayString()} ∩ {second.ToDisplayString()} at {probe}");
                }
            }
        }
    }

    // Whether a lone rule rewrites a constructor site, whatever its runtimes.
    private static bool Matches(RewriteOperationKind operation, bool isNewObj)
    {
        var ruleSet = new RewriteRuleSet("fixture", "1.0", [ConstructorRule("ctor", operation, RuntimeVersionRange.All)]);
        return new RewriteRuleMatcher(RuleSetRuntimeSupport.Evaluate(ruleSet, Net11))
            .TryMatchInvocation(ConstructorReference(), isNewObj, out _);
    }

    private static MethodReference ConstructorReference() =>
        new(".ctor", new TypeReference("System", "Void", null, null), new TypeReference("Fx", "Widget", null, null))
        {
            HasThis = true,
        };

    private static RewriteRule ConstructorRule(string id, RewriteOperationKind operation, RuntimeVersionRange supportedRuntimes) =>
        new()
        {
            Id = id,
            Operation = operation,
            Target = MemberSignature.Constructor("Fx.Widget"),
            Replacement = RewriteReplacement.Method("Shims", "Shims.Widget", "Create"),
            SupportedRuntimes = supportedRuntimes,
        };

    private static RewriteRule Rule(string id, string member, RuntimeVersionRange supportedRuntimes) =>
        RewriteRule.RedirectCall(
            id,
            MemberSignature.Method("System.DateTime", member),
            RewriteReplacement.Method("Shims", "Shims.Clock", member)) with
        {
            SupportedRuntimes = supportedRuntimes,
        };
}
