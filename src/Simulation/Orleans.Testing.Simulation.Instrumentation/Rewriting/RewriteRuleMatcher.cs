using Orleans.Testing.Simulation.Instrumentation.Rules;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Indexes the rules of a <see cref="RewriteRuleSet"/> that support one target runtime for fast, deterministic
/// matching of Mono.Cecil references during a single rewrite. Member-operation rules (call/newobj/wrap/reject) are
/// keyed by declaring-type full name; type-substitution rules are keyed by target-type full name.
/// </summary>
internal sealed class RewriteRuleMatcher
{
    private readonly Dictionary<string, List<RewriteRule>> _memberRules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RewriteRule> _typeSubstitutions = new(StringComparer.Ordinal);

    /// <summary>Indexes the rules that support the evaluated target runtime.</summary>
    /// <exception cref="InvalidOperationException">
    /// The rule set does not support the target: a rule it skips would leave sites it targets unrewritten. Callers
    /// report the rule set's support before rewriting.
    /// </exception>
    public RewriteRuleMatcher(RuleSetRuntimeSupport support)
    {
        if (!support.IsSupported)
        {
            throw new InvalidOperationException(
                $"Rule set '{support.RuleSet.Id}' does not support target runtime {support.TargetRuntime}; its support is checked before rewriting.");
        }

        foreach (RewriteRule rule in support.SupportedRules)
        {
            if (rule.Operation == RewriteOperationKind.SubstituteType)
            {
                // First rule for a given type wins (rule sets forbid duplicate ids, but two rules
                // could target the same type; the earlier one takes precedence deterministically).
                _ = _typeSubstitutions.TryAdd(rule.Target.DeclaringTypeFullName, rule);
            }
            else
            {
                if (!_memberRules.TryGetValue(rule.Target.DeclaringTypeFullName, out List<RewriteRule>? list))
                {
                    list = [];
                    _memberRules[rule.Target.DeclaringTypeFullName] = list;
                }

                list.Add(rule);
            }
        }
    }

    /// <summary>Gets a value indicating whether the rule set contains any type-substitution rules.</summary>
    public bool HasTypeSubstitutions => _typeSubstitutions.Count > 0;

    /// <summary>
    /// Gets the type-substitution rules, keyed by the substituted type's Cecil full name. Used by the member-aware
    /// substitution pass to build its type map (fields, locals, method/field references), which the operand-only
    /// <see cref="TypeReferenceRewritingPass"/> does not cover.
    /// </summary>
    public IEnumerable<KeyValuePair<string, RewriteRule>> TypeSubstitutionRules => _typeSubstitutions;

    /// <summary>
    /// Returns the Cecil full names of the substituted types and of their substitutes: the types whose
    /// presence in a member reference's signature makes whether it binds depend on the rewrite.
    /// </summary>
    public IReadOnlySet<string> GetSubstitutionTypeNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, RewriteRule> entry in TypeSubstitutionRules)
        {
            names.Add(entry.Key);
            names.Add(entry.Value.Replacement.DeclaringTypeFullName);
        }

        return names;
    }

    /// <summary>
    /// Finds the first rule (in declared order) matching an invocation of <paramref name="method"/>
    /// at a call/newobj site. Returns <see langword="false"/> if no rule applies.
    /// </summary>
    public bool TryMatchInvocation(MethodReference method, bool isNewObj, out RewriteRule matched)
    {
        matched = null!;

        string declaringType = CecilNames.NormalizedTypeFullName(method.DeclaringType);
        if (!_memberRules.TryGetValue(declaringType, out List<RewriteRule>? rules))
        {
            return false;
        }

        foreach (RewriteRule rule in rules)
        {
            if (!AppliesTo(rule, isNewObj))
            {
                continue;
            }

            if (rule.Target.MemberName != method.Name)
            {
                continue;
            }

            if (!MatchesParameters(rule.Target, method))
            {
                continue;
            }

            matched = rule;
            return true;
        }

        return false;
    }

    /// <summary>Finds the type-substitution rule for <paramref name="type"/>, if any.</summary>
    public bool TryMatchType(TypeReference type, out RewriteRule matched)
    {
        matched = null!;
        string key = CecilNames.NormalizedTypeFullName(type);
        if (_typeSubstitutions.TryGetValue(key, out RewriteRule? rule))
        {
            matched = rule;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the type-substitution rules referenced by <paramref name="type"/>,
    /// including rules targeting element types and generic arguments.
    /// </summary>
    public IReadOnlyList<RewriteRule> GetMatchingTypeRules(TypeReference type)
    {
        var rules = new List<RewriteRule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectTypeRules(type, rules, seen);
        return rules;
    }

    private void CollectTypeRules(
        TypeReference type,
        List<RewriteRule> rules,
        HashSet<string> seen)
    {
        TypeReference candidate = type is GenericInstanceType generic ? generic.ElementType : type;
        if (_typeSubstitutions.TryGetValue(candidate.FullName, out RewriteRule? rule)
            && seen.Add(rule.Id))
        {
            rules.Add(rule);
        }

        switch (type)
        {
            case GenericInstanceType instance:
                foreach (TypeReference argument in instance.GenericArguments)
                {
                    CollectTypeRules(argument, rules, seen);
                }

                break;
            case TypeSpecification specification:
                CollectTypeRules(specification.ElementType, rules, seen);
                break;
        }
    }

    private static bool AppliesTo(RewriteRule rule, bool isNewObj) =>
        (rule.Sites & (isNewObj ? RewriteSites.NewObj : RewriteSites.Call)) != RewriteSites.None;

    private static bool MatchesParameters(MemberSignature target, MethodReference method)
    {
        if (!target.HasParameterConstraint)
        {
            return true;
        }

        if (target.ParameterTypeFullNames.Length != method.Parameters.Count)
        {
            return false;
        }

        for (int i = 0; i < method.Parameters.Count; i++)
        {
            if (target.ParameterTypeFullNames[i] != CecilNames.ParameterFullName(method.Parameters[i].ParameterType))
            {
                return false;
            }
        }

        return true;
    }
}
