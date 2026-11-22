using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Rules;

/// <summary>
/// Checks the controlled-task rule set against the reference pack this project compiles against. A type substitution
/// rewrites every member reference whose signature mentions the substituted type, but a framework member keeps its
/// declaring type and original signature, so a call to one fails the rewrite with <c>CWR0001</c> unless a rule handles
/// it. Every public or protected member of a non-substituted type whose signature mentions a substituted type must
/// therefore be redirected to a shim, be an awaiter source the member-aware pass rebuilds, or be listed below with the
/// reason it is left to fail closed.
/// </summary>
[Collection(ReferencePackGroup.Name)]
public sealed class BuiltInRuleSetCompletenessTests
{
    private static readonly Version TargetRuntime = BuiltInRuleSets.SupportedRuntime;

    // Members deliberately left to fail the rewrite with CWR0001, keyed by full name, with the reason. Empty: every
    // reference-pack member that mentions a substituted type is redirected or rebuilt.
    private static readonly Dictionary<string, string> FailClosed = new(StringComparer.Ordinal);

    [Fact]
    public void EveryReferencePackMemberMentioningASubstitutedTypeIsHandled()
    {
        RewriteRuleSet ruleSet = BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies);
        HashSet<string> substituted =
        [
            .. new RewriteRuleMatcher(RuleSetRuntimeSupport.Evaluate(ruleSet, TargetRuntime)).TypeSubstitutionRules.Select(entry => entry.Key),
        ];
        var handled = new List<string>();
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        var unhandled = new List<string>();

        // A substituted type's own members are mapped onto its substitute.
        foreach (TypeDefinition type in ReferencePack.Shared.Types.Values
            .Where(type => !substituted.Contains(type.FullName))
            .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            foreach (MemberReference member in ReferencePack.VisibleMembers(type).Cast<MemberReference>()
                .Where(member => SignatureMentions.Find(member, substituted).Count > 0))
            {
                if (member is MethodDefinition method && (BuiltInRuleClassifier.Shared.IsAwaiterSource(method) || IsRedirected(ruleSet, method)))
                {
                    handled.Add(member.FullName);
                }
                else if (FailClosed.ContainsKey(member.FullName))
                {
                    allowed.Add(member.FullName);
                }
                else
                {
                    unhandled.Add(member.FullName);
                }
            }
        }

        Assert.Contains("System.Runtime.CompilerServices.TaskAwaiter System.Threading.Tasks.Task::GetAwaiter()", handled);
        Assert.Contains("System.Runtime.CompilerServices.YieldAwaitable System.Threading.Tasks.Task::Yield()", handled);
        Assert.True(unhandled.Count == 0, "Unhandled members:" + Environment.NewLine + string.Join(Environment.NewLine, unhandled));
        Assert.Empty(FailClosed.Keys.Except(allowed, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryRuleTargetIsAReferencePackMember()
    {
        var missing = new List<string>();
        foreach ((_, RewriteRule rule) in BuiltInRuleSets.DeterministicBclInventory.Concat(BuiltInRuleSets.ControlledTasksInventory))
        {
            MemberSignature target = rule.Target;
            if (!ReferencePack.Shared.Types.TryGetValue(target.DeclaringTypeFullName, out TypeDefinition? type))
            {
                missing.Add($"{rule.Id}: {target.DeclaringTypeFullName}");
            }
            else if (rule.Operation != RewriteOperationKind.SubstituteType
                && !type.Methods.Any(method => ReferencePack.IsVisible(method) && TargetMatches(target, method)))
            {
                missing.Add($"{rule.Id}: {target.ToCanonicalString()}");
            }
        }

        Assert.True(missing.Count == 0, "Rule targets missing from the reference pack:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    // A call-site redirect replaces the reference itself; a wrapper or injected rejection keeps it.
    private static bool IsRedirected(RewriteRuleSet ruleSet, MethodDefinition method) =>
        ruleSet.Rules.Any(rule =>
            (method.IsConstructor ? rule.Operation == RewriteOperationKind.RedirectNewObj : rule.Operation == RewriteOperationKind.RedirectCall)
            && rule.SupportedRuntimes.Includes(TargetRuntime)
            && rule.Target.DeclaringTypeFullName == method.DeclaringType.FullName
            && TargetMatches(rule.Target, method));

    private static bool TargetMatches(MemberSignature target, MethodDefinition method) =>
        method.Name == target.MemberName
        && (!target.HasParameterConstraint
            || target.ParameterTypeFullNames.SequenceEqual(method.Parameters.Select(parameter => ReferencePack.CallSiteName(parameter.ParameterType))));
}
