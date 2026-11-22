using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Configuration;

/// <summary>
/// Verifies the built-in rule-set selection plumbing: enabling by id, family include/exclude, the
/// lowest-precedence merge ordering and configuration-signature
/// participation so a selection change invalidates incremental outputs.
/// </summary>
public sealed class BuiltInSelectionTests
{
    private static InstrumentationConfiguration WithBuiltIns(
        IEnumerable<string>? ids = null,
        IEnumerable<string>? include = null,
        IEnumerable<string>? exclude = null) =>
        new()
        {
            BuiltInRuleSetIds = ids is null ? [BuiltInRuleSets.DeterministicBclId] : [.. ids],
            BuiltInIncludeFamilies = include is null ? [] : [.. include],
            BuiltInExcludeFamilies = exclude is null ? [] : [.. exclude],
        };

    [Fact]
    public void NoBuiltInsResolvesEmpty()
    {
        Assert.Empty(RuleSetMerge.ResolveBuiltIns(new InstrumentationConfiguration()));
    }

    [Fact]
    public void EnablingByIdResolvesEveryFamily()
    {
        RewriteRuleSet resolved = Assert.Single(RuleSetMerge.ResolveBuiltIns(WithBuiltIns()));
        Assert.Equal(BuiltInRuleSets.DeterministicBclId, resolved.Id);
        Assert.Equal(BuiltInRuleSets.DeterministicBclInventory.Length, resolved.Rules.Length);
    }

    [Fact]
    public void ControlledTaskSelectionParsesAndActivatesModernSynchronizationFamilies()
    {
        RewriteRuleSet resolved = Assert.Single(RuleSetMerge.ResolveBuiltIns(
            WithBuiltIns(ids: [BuiltInRuleSets.ControlledTasksId], include: ["ReaderWriterLockSlim"])));

        Assert.Equal(BuiltInRuleSets.ControlledTasksId, resolved.Id);
        Assert.Equal(BuiltInRuleSets.ControlledTasksVersion, resolved.Version);
        Assert.NotEmpty(resolved.Rules);
        Assert.All(resolved.Rules, rule => Assert.StartsWith("clockwork.readerwriterlockslim.", rule.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void IncludeSelectsOnlyNamedFamilies()
    {
        RewriteRuleSet resolved = Assert.Single(RuleSetMerge.ResolveBuiltIns(WithBuiltIns(include: ["Clock"])));
        Assert.All(resolved.Rules, r => Assert.StartsWith("clockwork.bcl.", r.Id, StringComparison.Ordinal));
        Assert.DoesNotContain(resolved.Rules, r => r.Id.StartsWith("clockwork.bcl.rng", StringComparison.Ordinal));
        Assert.Contains(resolved.Rules, r => r.Id == "clockwork.bcl.datetime.now");
    }

    [Fact]
    public void ExcludingCryptoRemovesItsRules()
    {
        RewriteRuleSet resolved = Assert.Single(
            RuleSetMerge.ResolveBuiltIns(WithBuiltIns(exclude: ["Crypto"])));
        Assert.DoesNotContain(resolved.Rules, r => r.Id.StartsWith("clockwork.bcl.rng", StringComparison.Ordinal));
        Assert.Contains(resolved.Rules, r => r.Id == "clockwork.bcl.guid.newguid");
    }

    [Fact]
    public void UnknownBuiltInIdThrows()
    {
        Assert.Throws<ConfigurationException>(
            () => RuleSetMerge.ResolveBuiltIns(WithBuiltIns(ids: ["clockwork.does.not.exist"])));
    }

    [Fact]
    public void UnknownFamilyThrows()
    {
        Assert.Throws<ConfigurationException>(
            () => RuleSetMerge.ResolveBuiltIns(WithBuiltIns(include: ["Nonsense"])));
    }

    [Fact]
    public void LoadAndMergeWithBuiltInsOnlySucceeds()
    {
        RuleSetMergeResult result = RuleSetMerge.LoadAndMerge(WithBuiltIns());
        Assert.NotEmpty(result.RuleSet.Rules);
        Assert.Contains(result.RuleSet.Rules, r => r.Id == "clockwork.bcl.datetime.utcnow");
    }

    [Fact]
    public void LoadAndMergeWithNoSourcesThrows()
    {
        Assert.Throws<ConfigurationException>(() => RuleSetMerge.LoadAndMerge(new InstrumentationConfiguration()));
    }

    [Fact]
    public void DocumentRuleOverridesBuiltInOfSameId()
    {
        // A document rule sharing a built-in id wins, because built-ins merge at lowest precedence.
        string document = """
            {
              "schemaVersion": 2,
              "id": "override.doc",
              "version": "1.0",
              "rules": [
                {
                  "id": "clockwork.bcl.datetime.utcnow",
                  "operation": "RedirectCall",
                  "target": { "type": "System.DateTime", "member": "get_UtcNow", "parameters": [] },
                  "replacement": { "assembly": "Custom", "type": "Custom.Clock", "member": "UtcNow", "parameters": [] }
                }
              ]
            }
            """;

        string path = Path.Combine(Path.GetTempPath(), $"clockwork-override-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, document);
        try
        {
            InstrumentationConfiguration configuration = WithBuiltIns() with { RuleSetPaths = [path] };
            RuleSetMergeResult result = RuleSetMerge.LoadAndMerge(configuration);

            RewriteRule utcNow = result.RuleSet.Rules.Single(r => r.Id == "clockwork.bcl.datetime.utcnow");
            Assert.Equal("Custom", utcNow.Replacement.AssemblyName);
            Assert.Contains(result.Overrides, o => o.RuleId == "clockwork.bcl.datetime.utcnow");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ConfigurationSignatureReflectsBuiltInSelection()
    {
        string none = new InstrumentationConfiguration().ComputeSignature();
        string enabled = WithBuiltIns().ComputeSignature();
        string clockOnly = WithBuiltIns(include: ["Clock"]).ComputeSignature();
        string excluded = WithBuiltIns(exclude: ["Crypto"]).ComputeSignature();

        var signatures = new[] { none, enabled, clockOnly, excluded };
        Assert.Equal(signatures.Length, signatures.Distinct(StringComparer.Ordinal).Count());
    }
}
