using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Rewrites type-reference operands in method bodies (<c>newarr</c>, <c>castclass</c>, <c>isinst</c>,
/// <c>box</c>, <c>unbox</c>/<c>unbox.any</c>, <c>ldtoken</c>, <c>initobj</c>, <c>sizeof</c>, and the
/// <c>constrained.</c> prefix) whose type matches a <see cref="RewriteOperationKind.SubstituteType"/>
/// rule, replacing them with a reference to the substitute type imported into the target module. The
/// pass is a no-op when the rule set declares no type substitutions.
/// </summary>
internal sealed class TypeReferenceRewritingPass : RewritePass
{
    public TypeReferenceRewritingPass(RewriteSession session)
        : base(session)
    {
    }

    /// <inheritdoc/>
    protected override Instruction VisitInstruction(Instruction instruction)
    {
        if (!Session.Matcher.HasTypeSubstitutions || instruction.Operand is not TypeReference type)
        {
            return instruction;
        }

        if (!TrySubstitute(type, out TypeReference? substituted, out RewriteRule rule, out string? error))
        {
            if (error is null)
            {
                return instruction;
            }

            ReportUnresolved(rule, instruction, error);
            return instruction;
        }

        int offset = instruction.Offset;
        RewriteSession.TryGetSequencePoint(Method!, instruction, out string? file, out int line);
        instruction.Operand = substituted;
        IsMethodBodyModified = true;

        Session.AddTransformation(new ManifestTransformation(
            rule.Id,
            rule.Operation,
            TransformationOutcome.Transformed,
            rule.Policy,
            rule.Target.ToCanonicalString(),
            rule.Replacement.ToCanonicalString(),
            CecilNames.FullyQualifiedMethodName(Method!),
            offset,
            file,
            line));

        return instruction;
    }

    /// <summary>
    /// Attempts to substitute <paramref name="type"/>. Returns <see langword="true"/> with the rewritten
    /// reference when a rule applies; the <paramref name="rule"/> that matched at the top level is
    /// reported for the manifest. A generic instance (for example <c>TaskAwaiter&lt;int&gt;</c>) is
    /// rebuilt as a <em>closed</em> instance of the substitute (<c>ControlledTaskAwaiter&lt;int&gt;</c>)
    /// so the emitted operand keeps its generic argument - a plain open substitute would be invalid IL
    /// for <c>initobj</c>/<c>box</c>/<c>constrained.</c> and fail to load.
    /// </summary>
    private bool TrySubstitute(
        TypeReference type,
        out TypeReference? substituted,
        out RewriteRule rule,
        out string? error)
    {
        substituted = null;
        rule = default!;
        error = null;

        if (type is GenericInstanceType generic)
        {
            // The element (open) type is what the substitution rules target.
            if (!Session.Matcher.TryMatchType(generic.ElementType, out rule))
            {
                return false;
            }

            if (!Session.Resolver.TryResolveType(Session.TargetModule, rule.Replacement, out TypeReference imported, out error))
            {
                return false;
            }

            var closed = new GenericInstanceType(imported);
            foreach (TypeReference argument in generic.GenericArguments)
            {
                // Recursively substitute each argument so nested substitutable types are handled, but
                // fall back to the original argument (the common case, e.g. int) when nothing matches.
                closed.GenericArguments.Add(
                    TrySubstituteArgument(argument, out TypeReference? mappedArgument) ? mappedArgument! : argument);
            }

            substituted = closed;
            return true;
        }

        if (!Session.Matcher.TryMatchType(type, out rule))
        {
            return false;
        }

        if (!Session.Resolver.TryResolveType(Session.TargetModule, rule.Replacement, out TypeReference resolved, out error))
        {
            return false;
        }

        substituted = resolved;
        return true;
    }

    private bool TrySubstituteArgument(TypeReference argument, out TypeReference? mapped)
    {
        if (TrySubstitute(argument, out TypeReference? substituted, out _, out _) && substituted is not null)
        {
            mapped = substituted;
            return true;
        }

        mapped = null;
        return false;
    }

    private void ReportUnresolved(RewriteRule rule, Instruction instruction, string error)
    {
        string containing = CecilNames.FullyQualifiedMethodName(Method!);
        Session.AddDiagnostic(Diagnostics.RewriteDiagnostic.Error(
            Diagnostics.RewriteDiagnosticIds.UnresolvedReplacement,
            $"{error} Rule '{rule.Id}' could not be applied.",
            containing,
            instruction.Offset));

        Session.AddUnresolvedReference(rule.Replacement.ToCanonicalString());
    }
}
