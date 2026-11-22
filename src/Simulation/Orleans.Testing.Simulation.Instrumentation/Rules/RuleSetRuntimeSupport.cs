using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;

namespace Orleans.Testing.Simulation.Instrumentation.Rules;

/// <summary>
/// How a rule set's rules support one target runtime. A rule set supports a target when every rule supports it or has
/// alternatives that do: rules with the same target which together rewrite every kind of site it rewrites, as when a
/// rule set carries one rule per runtime for an API. A rule for an API the target does not have supports the target
/// trivially, since no call site can name it. Rewriting requires that support, so every site a rule matches is
/// rewritten by a rule classified for the target.
/// </summary>
public sealed class RuleSetRuntimeSupport
{
    private const int ExampleRuleCount = 3;

    private RuleSetRuntimeSupport(
        RewriteRuleSet ruleSet,
        Version targetRuntime,
        RuntimeVersionRange supportedRuntimes,
        ImmutableArray<RewriteRule> supportedRules,
        ImmutableArray<RewriteRule> unsupportedRules)
    {
        RuleSet = ruleSet;
        TargetRuntime = targetRuntime;
        SupportedRuntimes = supportedRuntimes;
        SupportedRules = supportedRules;
        UnsupportedRules = unsupportedRules;
    }

    /// <summary>Gets the evaluated rule set.</summary>
    public RewriteRuleSet RuleSet { get; }

    /// <summary>Gets the target runtime's feature version.</summary>
    public Version TargetRuntime { get; }

    /// <summary>
    /// Gets the runtimes every rule in <see cref="SupportedRules"/> supports: the range recorded on each rewritten
    /// assembly and checked when a simulation runs it.
    /// </summary>
    public RuntimeVersionRange SupportedRuntimes { get; }

    /// <summary>Gets the rules that support <see cref="TargetRuntime"/>, in rule-set order: the rules a rewrite applies.</summary>
    public ImmutableArray<RewriteRule> SupportedRules { get; }

    /// <summary>Gets the rules that neither support the target nor have alternatives that do.</summary>
    public ImmutableArray<RewriteRule> UnsupportedRules { get; }

    /// <summary>Gets a value indicating whether the rule set supports <see cref="TargetRuntime"/>.</summary>
    public bool IsSupported => UnsupportedRules.IsEmpty;

    /// <summary>Evaluates how <paramref name="ruleSet"/> supports <paramref name="targetRuntime"/>.</summary>
    /// <param name="ruleSet">The rule set.</param>
    /// <param name="targetRuntime">The target runtime's feature version.</param>
    /// <returns>The rule set's support for the target.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="targetRuntime"/> or a rule's supported-runtime bound is not a feature version.
    /// </exception>
    public static RuleSetRuntimeSupport Evaluate(RewriteRuleSet ruleSet, Version targetRuntime)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(targetRuntime);
        if (!RuntimeFeatureVersion.IsFeatureVersion(targetRuntime))
        {
            throw new ArgumentException(
                $"Target runtime '{targetRuntime}' is not a feature version (major.minor).", nameof(targetRuntime));
        }

        var supportedSites = new Dictionary<string, RewriteSites>(StringComparer.Ordinal);
        var supportedRules = ImmutableArray.CreateBuilder<RewriteRule>();
        RuntimeVersionRange supportedRuntimes = RuntimeVersionRange.All;
        foreach (RewriteRule rule in ruleSet.Rules)
        {
            if (!rule.SupportedRuntimes.HasFeatureVersionBounds)
            {
                throw new ArgumentException(
                    $"Rule '{rule.Id}' supports runtimes {rule.SupportedRuntimes.ToDisplayString()}, whose bounds are not " +
                    "feature versions (major.minor).",
                    nameof(ruleSet));
            }

            if (rule.SupportedRuntimes.Includes(targetRuntime))
            {
                string target = rule.Target.ToCanonicalString();
                supportedSites[target] = supportedSites.GetValueOrDefault(target) | rule.Sites;
                supportedRules.Add(rule);
                supportedRuntimes = supportedRuntimes.Intersect(rule.SupportedRuntimes);
            }
        }

        ImmutableArray<RewriteRule> unsupportedRules =
        [
            .. ruleSet.Rules.Where(rule =>
                !rule.SupportedRuntimes.Includes(targetRuntime)
                && (rule.Sites & ~supportedSites.GetValueOrDefault(rule.Target.ToCanonicalString())) != RewriteSites.None),
        ];
        return new RuleSetRuntimeSupport(
            ruleSet,
            targetRuntime,
            supportedRuntimes,
            supportedRules.ToImmutable(),
            unsupportedRules);
    }

    /// <summary>Creates the error reporting that the rule set does not support the target runtime.</summary>
    /// <param name="targetRuntimeOrigin">Where the target runtime came from, such as <c>OrleansSimulationTargetRuntime</c>.</param>
    /// <returns>The <see cref="RewriteDiagnosticIds.RuntimeOutOfRange"/> error.</returns>
    /// <exception cref="InvalidOperationException">The rule set supports the target runtime.</exception>
    public RewriteDiagnostic ToDiagnostic(string targetRuntimeOrigin)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetRuntimeOrigin);
        if (IsSupported)
        {
            throw new InvalidOperationException($"Rule set '{RuleSet.Id}' supports target runtime {TargetRuntime}.");
        }

        string ranges = string.Join(
            ", ",
            UnsupportedRules
                .GroupBy(rule => rule.SupportedRuntimes)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.ToDisplayString(), StringComparer.Ordinal)
                .Select(group => $"{group.Key.ToDisplayString()} ({group.Count()} rules)"));
        string examples = string.Join(", ", UnsupportedRules.Take(ExampleRuleCount).Select(rule => $"'{rule.Id}'"));
        return RewriteDiagnostic.Error(
            RewriteDiagnosticIds.RuntimeOutOfRange,
            $"Target runtime {TargetRuntime} ({targetRuntimeOrigin}) is outside the runtimes {UnsupportedRules.Length} of " +
            $"the {RuleSet.Rules.Length} rules in rule set '{RuleSet.Id}' v{RuleSet.Version} support: {ranges}; for " +
            $"example {examples}. Instrument for a runtime the rules support, or select rules classified for " +
            $"{TargetRuntime}. Nothing was rewritten.");
    }
}
