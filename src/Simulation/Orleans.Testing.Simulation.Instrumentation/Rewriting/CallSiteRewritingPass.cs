using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Rewrites <c>call</c>/<c>callvirt</c>/<c>newobj</c> sites that match a member-operation rule:
/// redirecting them to a static replacement, redirecting a constructor to a static factory, inserting
/// a post-call wrapper, or injecting a deterministic rejection before an unsupported invocation.
/// Generic instance methods carry their type arguments onto the replacement when arity matches.
/// </summary>
internal sealed class CallSiteRewritingPass : RewritePass
{
    public CallSiteRewritingPass(RewriteSession session)
        : base(session)
    {
    }

    /// <inheritdoc/>
    protected override Instruction VisitInstruction(Instruction instruction)
    {
        bool isCall = instruction.OpCode.Code is Code.Call or Code.Callvirt;
        bool isNewObj = instruction.OpCode.Code == Code.Newobj;
        if ((!isCall && !isNewObj) || instruction.Operand is not MethodReference method)
        {
            return instruction;
        }

        if (!Session.Matcher.TryMatchInvocation(method, isNewObj, out RewriteRule rule))
        {
            return instruction;
        }

        return rule.Operation switch
        {
            RewriteOperationKind.RedirectCall => RedirectCall(instruction, method, rule),
            RewriteOperationKind.RedirectNewObj => RedirectCall(instruction, method, rule),
            RewriteOperationKind.WrapAfterCall => WrapAfterCall(instruction, method, rule),
            RewriteOperationKind.InjectRejection => InjectRejection(instruction, method, rule),
            _ => instruction,
        };
    }

    private Instruction RedirectCall(Instruction instruction, MethodReference method, RewriteRule rule)
    {
        int offset = instruction.Offset;

        // Only a virtual member has base calls: a rule which gives a non-virtual target a base-call replacement is
        // malformed, since its call sites are all non-virtual calls the base-call replacement would capture.
        if (rule.BaseCallReplacement is not null && method.Resolve() is { IsVirtual: false })
        {
            ReportContractMismatch(
                rule,
                offset,
                $"The rule has a base-call replacement, but its target '{method.FullName}' is not a virtual member, so it has no base calls.");
            return instruction;
        }

        RewriteReplacement replacement = instruction.OpCode.Code == Code.Call && rule.BaseCallReplacement is { } baseCall
            ? baseCall
            : rule.Replacement;
        if (!TryResolveMethod(rule, replacement, offset, out MethodReference open, out MethodDefinition definition))
        {
            return instruction;
        }

        _ = TryApplyGenericArguments(open, method, definition, out MethodReference target, out string? genericError);
        if (!ValidateContract(rule, method, target, offset, genericError))
        {
            return instruction;
        }

        var redirected = Instruction.Create(OpCodes.Call, target);
        Record(rule, replacement, offset, TransformationOutcome.Transformed);
        Replace(instruction, redirected);
        return redirected;
    }

    private Instruction WrapAfterCall(Instruction instruction, MethodReference method, RewriteRule rule)
    {
        int offset = instruction.Offset;
        if (!TryResolveMethod(rule, rule.Replacement, offset, out MethodReference open, out MethodDefinition definition))
        {
            return instruction;
        }

        MethodReference wrapper = open;
        if (definition.HasGenericParameters && definition.GenericParameters.Count == 1)
        {
            var generic = new GenericInstanceMethod(open);
            TypeReference returnType = ReplacementContractValidator.InflateType(method.ReturnType, method);
            generic.GenericArguments.Add(Session.TargetModule.ImportReference(returnType));
            wrapper = generic;
        }
        else if (definition.GenericParameters.Count > 1)
        {
            ReportContractMismatch(
                rule,
                offset,
                $"Post-call replacement '{definition.FullName}' has generic arity {definition.GenericParameters.Count}; only zero or one is supported.");
            return instruction;
        }

        if (!ValidateContract(rule, method, wrapper, offset, error: null))
        {
            return instruction;
        }

        var wrap = Instruction.Create(OpCodes.Call, wrapper);
        Record(rule, rule.Replacement, offset, TransformationOutcome.Transformed);
        Processor!.InsertAfter(instruction, wrap);
        IsMethodBodyModified = true;
        return wrap;
    }

    private Instruction InjectRejection(Instruction instruction, MethodReference method, RewriteRule rule)
    {
        int offset = instruction.Offset;
        if (!TryResolveMethod(rule, rule.Replacement, offset, out MethodReference open, out _))
        {
            return instruction;
        }

        if (!ValidateContract(rule, method, open, offset, error: null))
        {
            return instruction;
        }

        // Insert "ldstr <api>; call Reject(string)" before the invocation, keeping the original
        // invocation in place so the evaluation stack stays balanced and verifiable. Replace() moves
        // branch targets and handler boundaries onto the new leading instruction.
        var apiName = Instruction.Create(OpCodes.Ldstr, CecilNames.FullyQualifiedMethodName(method));
        var reject = Instruction.Create(OpCodes.Call, open);

        Record(rule, rule.Replacement, offset, TransformationOutcome.Rejected);
        Replace(instruction, apiName);
        Processor!.InsertAfter(apiName, reject);
        Processor.InsertAfter(reject, instruction);
        return instruction;
    }


    private bool TryResolveMethod(
        RewriteRule rule,
        RewriteReplacement replacement,
        int offset,
        out MethodReference open,
        out MethodDefinition definition)
    {
        if (Session.Resolver.TryResolveMethod(Session.TargetModule, replacement, out open, out definition, out string? error))
        {
            return true;
        }

        string containing = CecilNames.FullyQualifiedMethodName(Method!);
        Session.AddDiagnostic(RewriteDiagnostic.Error(
            RewriteDiagnosticIds.UnresolvedReplacement,
            $"{error} Rule '{rule.Id}' could not be applied.",
            containing,
            offset));

        Session.AddUnresolvedReference(replacement.ToCanonicalString());
        return false;
    }

    private bool TryApplyGenericArguments(
        MethodReference open,
        MethodReference matched,
        MethodDefinition definition,
        out MethodReference result,
        out string? error)
    {
        if (!definition.HasGenericParameters)
        {
            result = open;
            error = null;
            return true;
        }

        // A generic method on a closed generic type - e.g. Task<int>.ContinueWith<string>(...) - binds
        // the replacement's type parameters from the receiver's declaring-type arguments *followed by*
        // the call site's own method type arguments, in that order. The controlled shim therefore
        // declares its generic parameters declaring-type-first (e.g.
        // ControlledTask.ContinueWith<TResult, TNewResult>(Task<TResult>, Func<Task<TResult>, TNewResult>)).
        if (matched is GenericInstanceMethod methodAndType
            && matched.DeclaringType is GenericInstanceType owner
            && definition.GenericParameters.Count == owner.GenericArguments.Count + methodAndType.GenericArguments.Count)
        {
            result = Instantiate(open, [.. owner.GenericArguments, .. methodAndType.GenericArguments]);
            error = null;
            return true;
        }

        // A generic call site - e.g. Task.WhenAll<int>(...) - carries its own method type arguments,
        // which bind the generic replacement method one-for-one.
        if (matched is GenericInstanceMethod generic
            && definition.GenericParameters.Count == generic.GenericArguments.Count)
        {
            result = Instantiate(open, generic.GenericArguments);
            error = null;
            return true;
        }

        // A non-generic member on a closed generic type - e.g. Task<int>.get_Result - carries the
        // receiver's declaring-type arguments onto the generic replacement method, so a redirect to
        // ControlledTask.Result<int>(Task<int>) stays stack-balanced and correctly typed.
        if (matched is not GenericInstanceMethod
            && matched.DeclaringType is GenericInstanceType declaring
            && definition.GenericParameters.Count == declaring.GenericArguments.Count)
        {
            result = Instantiate(open, declaring.GenericArguments);
            error = null;
            return true;
        }

        int available = (matched as GenericInstanceMethod)?.GenericArguments.Count ?? 0;
        available += (matched.DeclaringType as GenericInstanceType)?.GenericArguments.Count ?? 0;
        result = open;
        error =
            $"Replacement '{definition.FullName}' has generic arity {definition.GenericParameters.Count}, " +
            $"but target '{matched.FullName}' supplies {available} generic arguments.";
        return false;
    }

    private GenericInstanceMethod Instantiate(MethodReference open, IEnumerable<TypeReference> arguments)
    {
        var result = new GenericInstanceMethod(open);
        foreach (TypeReference argument in arguments)
        {
            result.GenericArguments.Add(Session.TargetModule.ImportReference(argument));
        }

        return result;
    }

    private bool ValidateContract(
        RewriteRule rule,
        MethodReference method,
        MethodReference replacement,
        int offset,
        string? error)
    {
        if (error is null
            && ReplacementContractValidator.TryValidate(
                rule.Operation,
                method,
                replacement,
                IsSubstitutedTypePair,
                out error))
        {
            return true;
        }

        ReportContractMismatch(rule, offset, error ?? "The replacement contract is incompatible.");
        return false;
    }

    private bool IsSubstitutedTypePair(TypeReference original, TypeReference replacement) =>
        IsSubstitutedTypePair(
            original,
            replacement,
            new HashSet<TypePair>(TypePairComparer.Instance));

    private bool IsSubstitutedTypePair(
        TypeReference original,
        TypeReference replacement,
        HashSet<TypePair> visited)
    {
        if (!visited.Add(new TypePair(original, replacement)))
        {
            return true;
        }

        if (original is ByReferenceType || replacement is ByReferenceType)
        {
            return original is ByReferenceType left
                && replacement is ByReferenceType right
                && IsEquivalentTypeArgument(left.ElementType, right.ElementType, visited);
        }

        if (original is PointerType || replacement is PointerType)
        {
            return original is PointerType left
                && replacement is PointerType right
                && IsEquivalentTypeArgument(left.ElementType, right.ElementType, visited);
        }

        if (original is ArrayType || replacement is ArrayType)
        {
            return original is ArrayType left
                && replacement is ArrayType right
                && left.Rank == right.Rank
                && left.IsVector == right.IsVector
                && DimensionsMatch(left, right)
                && IsEquivalentTypeArgument(left.ElementType, right.ElementType, visited);
        }

        if (original is RequiredModifierType || replacement is RequiredModifierType)
        {
            return original is RequiredModifierType left
                && replacement is RequiredModifierType right
                && IsEquivalentTypeArgument(left.ModifierType, right.ModifierType, visited)
                && IsEquivalentTypeArgument(left.ElementType, right.ElementType, visited);
        }

        if (original is OptionalModifierType || replacement is OptionalModifierType)
        {
            return original is OptionalModifierType left
                && replacement is OptionalModifierType right
                && IsEquivalentTypeArgument(left.ModifierType, right.ModifierType, visited)
                && IsEquivalentTypeArgument(left.ElementType, right.ElementType, visited);
        }

        if (original is PinnedType || replacement is PinnedType)
        {
            return original is PinnedType left
                && replacement is PinnedType right
                && IsEquivalentTypeArgument(left.ElementType, right.ElementType, visited);
        }

        if (original is SentinelType || replacement is SentinelType)
        {
            return original is SentinelType left
                && replacement is SentinelType right
                && IsEquivalentTypeArgument(left.ElementType, right.ElementType, visited);
        }

        if (original is FunctionPointerType || replacement is FunctionPointerType)
        {
            return original is FunctionPointerType left
                && replacement is FunctionPointerType right
                && FunctionPointersMatch(left, right, visited);
        }

        GenericInstanceType? originalGeneric = original as GenericInstanceType;
        GenericInstanceType? replacementGeneric = replacement as GenericInstanceType;
        if ((originalGeneric is null) != (replacementGeneric is null))
        {
            return false;
        }

        string originalName = CecilNames.NormalizedTypeFullName(original);
        string replacementName = CecilNames.NormalizedTypeFullName(replacement);
        RewriteRule? substitution = Session.Matcher.TypeSubstitutionRules
            .Where(entry => string.Equals(entry.Key, originalName, StringComparison.Ordinal))
            .Select(entry => entry.Value)
            .FirstOrDefault();
        if (substitution is null
            || !string.Equals(
                substitution.Replacement.DeclaringTypeFullName,
                replacementName,
                StringComparison.Ordinal)
            || original.IsValueType != replacement.IsValueType)
        {
            return false;
        }

        if (originalGeneric is null || replacementGeneric is null)
        {
            return original.HasGenericParameters == replacement.HasGenericParameters
                && original.GenericParameters.Count == replacement.GenericParameters.Count;
        }

        return originalGeneric.GenericArguments.Count == replacementGeneric.GenericArguments.Count
            && originalGeneric.GenericArguments
                .Zip(
                    replacementGeneric.GenericArguments,
                    (left, right) => IsEquivalentTypeArgument(left, right, visited))
                .All(static equivalent => equivalent);
    }

    private bool IsEquivalentTypeArgument(
        TypeReference original,
        TypeReference replacement,
        HashSet<TypePair> visited) =>
        TypeReferenceStructure.AreEquivalent(original, null, replacement, null)
        || IsSubstitutedTypePair(original, replacement, visited);

    private bool FunctionPointersMatch(
        FunctionPointerType original,
        FunctionPointerType replacement,
        HashSet<TypePair> visited)
    {
        if (original.HasThis != replacement.HasThis
            || original.ExplicitThis != replacement.ExplicitThis
            || original.CallingConvention != replacement.CallingConvention
            || original.GenericParameters.Count != replacement.GenericParameters.Count
            || original.Parameters.Count != replacement.Parameters.Count
            || !IsEquivalentTypeArgument(original.ReturnType, replacement.ReturnType, visited))
        {
            return false;
        }

        for (int i = 0; i < original.Parameters.Count; i++)
        {
            if (!IsEquivalentTypeArgument(
                original.Parameters[i].ParameterType,
                replacement.Parameters[i].ParameterType,
                visited))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DimensionsMatch(ArrayType original, ArrayType replacement)
    {
        if (original.Dimensions.Count != replacement.Dimensions.Count)
        {
            return false;
        }

        for (int i = 0; i < original.Dimensions.Count; i++)
        {
            if (original.Dimensions[i].LowerBound != replacement.Dimensions[i].LowerBound
                || original.Dimensions[i].UpperBound != replacement.Dimensions[i].UpperBound)
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct TypePair(TypeReference Original, TypeReference Replacement);

    private sealed class TypePairComparer : IEqualityComparer<TypePair>
    {
        public static TypePairComparer Instance { get; } = new();

        public bool Equals(TypePair x, TypePair y) =>
            ReferenceEquals(x.Original, y.Original)
            && ReferenceEquals(x.Replacement, y.Replacement);

        public int GetHashCode(TypePair obj) =>
            HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Original),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Replacement));
    }

    private void ReportContractMismatch(RewriteRule rule, int offset, string message)
    {
        Session.AddDiagnostic(RewriteDiagnostic.Error(
            RewriteDiagnosticIds.ReplacementContractMismatch,
            $"{message} Rule '{rule.Id}' was not applied.",
            CecilNames.FullyQualifiedMethodName(Method!),
            offset));
    }

    // Records the replacement the site was rewritten to, which for a base call is the rule's base-call replacement.
    private void Record(RewriteRule rule, RewriteReplacement replacement, int offset, TransformationOutcome outcome)
    {
        string containing = CecilNames.FullyQualifiedMethodName(Method!);
        RewriteSession.TryGetSequencePoint(Method!, FindInstruction(offset), out string? file, out int line);

        Session.AddTransformation(new ManifestTransformation(
            rule.Id,
            rule.Operation,
            outcome,
            rule.Policy,
            rule.Target.ToCanonicalString(),
            replacement.ToCanonicalString(),
            containing,
            offset,
            file,
            line));
    }

    private Instruction FindInstruction(int offset)
    {
        foreach (Instruction instruction in Method!.Body.Instructions)
        {
            if (instruction.Offset == offset)
            {
                return instruction;
            }
        }

        return Method!.Body.Instructions[0];
    }
}
