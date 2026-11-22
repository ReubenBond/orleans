using Orleans.Testing.Simulation.Instrumentation.Rules;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>Validates replacement method stack contracts before call-site IL is edited.</summary>
internal static class ReplacementContractValidator
{
    public static bool TryValidate(
        RewriteOperationKind operation,
        MethodReference original,
        MethodReference replacement,
        Func<TypeReference, TypeReference, bool> additionalTypeCompatibility,
        out string? error)
    {
        if (replacement.HasThis)
        {
            error = $"Replacement '{MethodShape(replacement)}' must be static.";
            return false;
        }

        return operation switch
        {
            RewriteOperationKind.RedirectCall => ValidateRedirect(
                original, replacement, isConstructor: false, additionalTypeCompatibility, out error),
            RewriteOperationKind.RedirectNewObj => ValidateRedirect(
                original, replacement, isConstructor: true, additionalTypeCompatibility, out error),
            RewriteOperationKind.WrapAfterCall => ValidateWrapper(
                original, replacement, additionalTypeCompatibility, out error),
            RewriteOperationKind.InjectRejection => ValidateRejection(replacement, out error),
            _ => Success(out error),
        };
    }

    private static bool ValidateRedirect(
        MethodReference original,
        MethodReference replacement,
        bool isConstructor,
        Func<TypeReference, TypeReference, bool> additionalTypeCompatibility,
        out string? error)
    {
        int receiverCount = !isConstructor && original.HasThis ? 1 : 0;
        int expectedCount = original.Parameters.Count + receiverCount;
        if (replacement.Parameters.Count != expectedCount)
        {
            error =
                $"Replacement '{MethodShape(replacement)}' consumes {replacement.Parameters.Count} stack arguments, " +
                $"but target '{MethodShape(original)}' requires {expectedCount} including its receiver.";
            return false;
        }

        int replacementIndex = 0;
        TypeReference receiverType = original.DeclaringType.IsValueType
            ? new ByReferenceType(original.DeclaringType)
            : original.DeclaringType;
        if (receiverCount == 1
            && !SameType(
                receiverType,
                original,
                replacement.Parameters[replacementIndex++].ParameterType,
                replacement,
                additionalTypeCompatibility))
        {
            error =
                $"Replacement '{MethodShape(replacement)}' has an incompatible receiver parameter for " +
                $"'{MethodShape(original)}'.";
            return false;
        }

        for (int i = 0; i < original.Parameters.Count; i++)
        {
            if (!SameType(
                original.Parameters[i].ParameterType,
                original,
                replacement.Parameters[replacementIndex + i].ParameterType,
                replacement,
                additionalTypeCompatibility))
            {
                error =
                    $"Replacement '{MethodShape(replacement)}' parameter {replacementIndex + i} is incompatible with " +
                    $"target parameter {i} of '{MethodShape(original)}'.";
                return false;
            }
        }

        TypeReference expectedReturn = isConstructor ? original.DeclaringType : original.ReturnType;
        if (!SameType(
            expectedReturn,
            original,
            replacement.ReturnType,
            replacement,
            additionalTypeCompatibility))
        {
            error =
                $"Replacement '{MethodShape(replacement)}' returns '{Shape(replacement.ReturnType, replacement)}', " +
                $"but target '{MethodShape(original)}' requires '{Shape(expectedReturn, original)}'.";
            return false;
        }

        return Success(out error);
    }

    private static bool ValidateWrapper(
        MethodReference original,
        MethodReference replacement,
        Func<TypeReference, TypeReference, bool> additionalTypeCompatibility,
        out string? error)
    {
        if (original.ReturnType.MetadataType == MetadataType.Void)
        {
            error = $"Post-call rule cannot wrap void target '{MethodShape(original)}'.";
            return false;
        }

        if (replacement.Parameters.Count != 1
            || !SameType(
                original.ReturnType,
                original,
                replacement.Parameters[0].ParameterType,
                replacement,
                additionalTypeCompatibility)
            || !SameType(
                original.ReturnType,
                original,
                replacement.ReturnType,
                replacement,
                additionalTypeCompatibility))
        {
            error =
                $"Post-call replacement '{MethodShape(replacement)}' must be static and consume and return exactly " +
                $"'{Shape(original.ReturnType, original)}'.";
            return false;
        }

        return Success(out error);
    }

    private static bool ValidateRejection(MethodReference replacement, out string? error)
    {
        if (replacement.Parameters.Count != 1
            || replacement.Parameters[0].ParameterType.FullName != "System.String"
            || replacement.ReturnType.MetadataType != MetadataType.Void)
        {
            error =
                $"Rejection replacement '{MethodShape(replacement)}' must be a static void method taking exactly one System.String.";
            return false;
        }

        return Success(out error);
    }

    private static bool SameType(
        TypeReference left,
        MethodReference leftOwner,
        TypeReference right,
        MethodReference rightOwner,
        Func<TypeReference, TypeReference, bool> additionalTypeCompatibility) =>
        TypeReferenceStructure.AreEquivalent(left, leftOwner, right, rightOwner)
        || additionalTypeCompatibility(left, right);

    private static string Shape(TypeReference type, MethodReference owner) =>
        TypeReferenceStructure.Shape(type, owner);

    private static string MethodShape(MethodReference method)
    {
        string genericArguments = method is GenericInstanceMethod generic
            ? "<" + string.Join(",", generic.GenericArguments.Select(argument => Shape(argument, method))) + ">"
            : string.Empty;
        string parameters = string.Join(
            ",",
            method.Parameters.Select(parameter => Shape(parameter.ParameterType, method)));
        return Shape(method.ReturnType, method)
            + " " + Shape(method.DeclaringType, method)
            + "::" + method.Name + genericArguments
            + "(" + parameters + ")";
    }

    public static TypeReference InflateType(TypeReference type, MethodReference owner) =>
        TypeReferenceStructure.Inflate(type, owner);

    private static bool Success(out string? error)
    {
        error = null;
        return true;
    }
}
