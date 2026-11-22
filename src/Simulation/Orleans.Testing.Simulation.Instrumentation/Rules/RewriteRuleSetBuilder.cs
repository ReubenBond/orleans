namespace Orleans.Testing.Simulation.Instrumentation.Rules;

/// <summary>
/// A fluent builder for <see cref="RewriteRuleSet"/>. Rules are added in order; the first rule that
/// matches a given site wins during rewriting.
/// </summary>
public sealed class RewriteRuleSetBuilder
{
    private readonly string _id;
    private readonly string _version;
    private readonly List<RewriteRule> _rules = [];

    internal RewriteRuleSetBuilder(string id, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(version);
        _id = id;
        _version = version;
    }

    /// <summary>Adds a rule to the set.</summary>
    public RewriteRuleSetBuilder Add(RewriteRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rules.Add(rule);
        return this;
    }

    /// <summary>Adds a static/instance call redirection rule.</summary>
    public RewriteRuleSetBuilder RedirectCall(string id, MemberSignature target, RewriteReplacement replacement) =>
        Add(RewriteRule.RedirectCall(id, target, replacement));

    /// <summary>Adds a constructor (<c>newobj</c>) redirection rule.</summary>
    public RewriteRuleSetBuilder RedirectNewObj(string id, MemberSignature target, RewriteReplacement replacement) =>
        Add(RewriteRule.RedirectNewObj(id, target, replacement));

    /// <summary>Adds a type-substitution rule.</summary>
    public RewriteRuleSetBuilder SubstituteType(string id, string targetTypeFullName, RewriteReplacement replacementType) =>
        Add(RewriteRule.SubstituteType(id, targetTypeFullName, replacementType));

    /// <summary>Adds a post-call wrapper rule.</summary>
    public RewriteRuleSetBuilder WrapAfterCall(string id, MemberSignature target, RewriteReplacement replacement) =>
        Add(RewriteRule.WrapAfterCall(id, target, replacement));

    /// <summary>Adds a deterministic rejection-injection rule.</summary>
    public RewriteRuleSetBuilder InjectRejection(string id, MemberSignature target, RewriteReplacement rejectionMethod) =>
        Add(RewriteRule.InjectRejection(id, target, rejectionMethod));

    /// <summary>Builds the immutable rule set.</summary>
    public RewriteRuleSet Build() => new(_id, _version, _rules);
}
