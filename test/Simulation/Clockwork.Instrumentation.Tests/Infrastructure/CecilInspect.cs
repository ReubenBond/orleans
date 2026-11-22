using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>
/// Small Mono.Cecil query helpers for asserting on rewritten IL structure without depending on raw
/// byte equality.
/// </summary>
internal static class CecilInspect
{
    /// <summary>Finds a method by declaring-type full name and method name (searching nested types).</summary>
    public static MethodDefinition GetMethod(ModuleDefinition module, string typeFullName, string methodName)
    {
        foreach (TypeDefinition type in module.GetTypes())
        {
            if (type.FullName != typeFullName)
            {
                continue;
            }

            foreach (MethodDefinition method in type.Methods)
            {
                if (method.Name == methodName)
                {
                    return method;
                }
            }
        }

        throw new InvalidOperationException($"Method '{typeFullName}.{methodName}' was not found.");
    }

    /// <summary>Returns the full names of every call/callvirt/newobj target in a method body.</summary>
    public static List<string> CallTargets(MethodDefinition method)
    {
        var targets = new List<string>();
        if (!method.HasBody)
        {
            return targets;
        }

        foreach (Instruction instruction in method.Body.Instructions)
        {
            if (instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj
                && instruction.Operand is MethodReference reference)
            {
                targets.Add(reference.FullName);
            }
        }

        return targets;
    }

    /// <summary>Returns <see langword="true"/> if any call target's full name contains <paramref name="fragment"/>.</summary>
    public static bool CallsAnyContaining(MethodDefinition method, string fragment) =>
        CallTargets(method).Exists(t => t.Contains(fragment, StringComparison.Ordinal));

    /// <summary>Returns the ldstr string operands within a method body.</summary>
    public static List<string> StringLiterals(MethodDefinition method)
    {
        var literals = new List<string>();
        if (!method.HasBody)
        {
            return literals;
        }

        foreach (Instruction instruction in method.Body.Instructions)
        {
            if (instruction.OpCode.Code == Code.Ldstr && instruction.Operand is string value)
            {
                literals.Add(value);
            }
        }

        return literals;
    }

    /// <summary>Returns the full names of every type-reference operand in a method body.</summary>
    public static List<string> TypeOperands(MethodDefinition method)
    {
        var operands = new List<string>();
        if (!method.HasBody)
        {
            return operands;
        }

        foreach (Instruction instruction in method.Body.Instructions)
        {
            if (instruction.Operand is TypeReference reference)
            {
                operands.Add(reference.FullName);
            }
        }

        return operands;
    }

    /// <summary>Returns <see langword="true"/> if any method in the module has a call target containing <paramref name="fragment"/>.</summary>
    public static bool AnyMethodCallsContaining(ModuleDefinition module, string fragment)
    {
        foreach (TypeDefinition type in module.GetTypes())
        {
            foreach (MethodDefinition method in type.Methods)
            {
                if (CallsAnyContaining(method, fragment))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Returns <see langword="true"/> if the assembly carries the idempotence signature marker.</summary>
    public static bool HasRewriteSignature(ModuleDefinition module) =>
        module.Assembly.CustomAttributes.Any(attribute =>
            attribute.AttributeType.Name == "ClockworkRewriteSignatureAttribute" ||
            attribute.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute" &&
            attribute.ConstructorArguments.Count == 2 &&
            string.Equals(
                attribute.ConstructorArguments[0].Value as string,
                "Clockwork.RewriteSignature",
                StringComparison.Ordinal));
}
