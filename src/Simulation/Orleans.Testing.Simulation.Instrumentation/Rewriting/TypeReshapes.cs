using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Finds the types and methods whose shape - a base type, an interface, or a generic parameter constraint - is composed
/// from a substituted type, such as <c>List&lt;Barrier&gt;</c>, and where code on the other side of the rewrite
/// boundary mentions them. The rewrite substitutes such a part in the types it rewrites, while a type excluded from
/// rewriting, or of an assembly the closure does not rewrite, keeps the original. Code on one side may use a type of the
/// other through the base type or interface it sees, a conversion IL does not spell out, or instantiate a generic
/// parameter with a type that satisfies only the constraint it sees. The runtime would then fail to bind the member or
/// the instantiation, or invoke a member of one base type on an object of the other, so any mention counts.
/// </summary>
internal static class TypeReshapes
{
    /// <summary>
    /// Returns the first part of <paramref name="type"/>'s shape - its base type, then its interfaces, then its generic
    /// parameter constraints - for which <paramref name="changed"/> holds.
    /// </summary>
    public static TypeReshape? Find(TypeDefinition type, Func<TypeReference, bool> changed)
    {
        if (type.BaseType is { } baseType && changed(baseType))
        {
            return new TypeReshape(type.FullName, "base type", baseType);
        }

        foreach (InterfaceImplementation implementation in type.Interfaces)
        {
            if (changed(implementation.InterfaceType))
            {
                return new TypeReshape(type.FullName, "interface", implementation.InterfaceType);
            }
        }

        return FindConstraint(type.FullName, type.GenericParameters, changed);
    }

    /// <summary>
    /// Returns the first generic parameter constraint of <paramref name="method"/> for which <paramref name="changed"/>
    /// holds.
    /// </summary>
    public static TypeReshape? Find(MethodDefinition method, Func<TypeReference, bool> changed) =>
        FindConstraint(method.FullName, method.GenericParameters, changed);

    /// <summary>
    /// Returns each mention, in <paramref name="type"/>'s declaration, fields, method signatures, and method bodies,
    /// of a type or method whose shape the other side of the boundary does not share.
    /// </summary>
    /// <param name="type">A type on one side of the boundary.</param>
    /// <param name="reshapeOf">The unshared shape of a type that is not a type specification, or null.</param>
    /// <param name="methodReshapeOf">The unshared constraints of a referenced method, or null.</param>
    public static IReadOnlyList<ReshapedReference> FindReferences(
        TypeDefinition type,
        Func<TypeReference, TypeReshape?> reshapeOf,
        Func<MethodReference, TypeReshape?> methodReshapeOf)
    {
        var collector = new Collector(reshapeOf, methodReshapeOf);
        string name = type.FullName;
        collector.Type(type.BaseType, name, offset: -1);
        foreach (InterfaceImplementation implementation in type.Interfaces)
        {
            collector.Type(implementation.InterfaceType, name, offset: -1);
        }

        collector.Constraints(type.GenericParameters, name);
        foreach (FieldDefinition field in type.Fields)
        {
            collector.Type(field.FieldType, name, offset: -1);
        }

        foreach (MethodDefinition method in type.Methods)
        {
            string containing = CecilNames.FullyQualifiedMethodName(method);
            collector.Type(method.ReturnType, containing, offset: -1);
            foreach (ParameterDefinition parameter in method.Parameters)
            {
                collector.Type(parameter.ParameterType, containing, offset: -1);
            }

            collector.Constraints(method.GenericParameters, containing);
            foreach (MethodReference implemented in method.Overrides)
            {
                collector.Member(implemented, containing, offset: -1);
            }

            if (!method.HasBody)
            {
                continue;
            }

            foreach (VariableDefinition variable in method.Body.Variables)
            {
                collector.Type(variable.VariableType, containing, offset: -1);
            }

            foreach (ExceptionHandler handler in method.Body.ExceptionHandlers)
            {
                collector.Type(handler.CatchType, containing, offset: -1);
            }

            foreach (Instruction instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case TypeReference operand:
                        collector.Type(operand, containing, instruction.Offset);
                        break;
                    case MemberReference operand:
                        collector.Member(operand, containing, instruction.Offset);
                        break;
                    case CallSite callSite:
                        collector.Type(callSite.ReturnType, containing, instruction.Offset);
                        foreach (ParameterDefinition parameter in callSite.Parameters)
                        {
                            collector.Type(parameter.ParameterType, containing, instruction.Offset);
                        }

                        break;
                }
            }
        }

        return collector.Found;
    }

    private static TypeReshape? FindConstraint(
        string owner,
        Mono.Collections.Generic.Collection<GenericParameter> parameters,
        Func<TypeReference, bool> changed)
    {
        foreach (GenericParameter parameter in parameters)
        {
            foreach (GenericParameterConstraint constraint in parameter.Constraints)
            {
                if (changed(constraint.ConstraintType))
                {
                    return new TypeReshape(owner, $"constraint on '{parameter.Name}'", constraint.ConstraintType);
                }
            }
        }

        return null;
    }

    private sealed class Collector(
        Func<TypeReference, TypeReshape?> reshapeOf,
        Func<MethodReference, TypeReshape?> methodReshapeOf)
    {
        public List<ReshapedReference> Found { get; } = [];

        public void Type(TypeReference? type, string containing, int offset)
        {
            switch (type)
            {
                case null or GenericParameter:
                    return;
                case GenericInstanceType generic:
                    Type(generic.ElementType, containing, offset);
                    foreach (TypeReference argument in generic.GenericArguments)
                    {
                        Type(argument, containing, offset);
                    }

                    return;
                case FunctionPointerType pointer:
                    Type(pointer.ReturnType, containing, offset);
                    foreach (ParameterDefinition parameter in pointer.Parameters)
                    {
                        Type(parameter.ParameterType, containing, offset);
                    }

                    return;
                case IModifierType modifier:
                    Type(modifier.ModifierType, containing, offset);
                    Type(modifier.ElementType, containing, offset);
                    return;
                case TypeSpecification specification:
                    Type(specification.ElementType, containing, offset);
                    return;
                default:
                    if (reshapeOf(type) is { } reshape)
                    {
                        Found.Add(new ReshapedReference(type.FullName, reshape, containing, offset));
                    }

                    return;
            }
        }

        public void Constraints(Mono.Collections.Generic.Collection<GenericParameter> parameters, string containing)
        {
            foreach (GenericParameter parameter in parameters)
            {
                foreach (GenericParameterConstraint constraint in parameter.Constraints)
                {
                    Type(constraint.ConstraintType, containing, offset: -1);
                }
            }
        }

        public void Member(MemberReference member, string containing, int offset)
        {
            Type(member.DeclaringType, containing, offset);
            switch (member)
            {
                case MethodReference method:
                    Type(method.ReturnType, containing, offset);
                    foreach (ParameterDefinition parameter in method.Parameters)
                    {
                        Type(parameter.ParameterType, containing, offset);
                    }

                    if (method is GenericInstanceMethod generic)
                    {
                        foreach (TypeReference argument in generic.GenericArguments)
                        {
                            Type(argument, containing, offset);
                        }
                    }

                    if (methodReshapeOf(method) is { } reshape)
                    {
                        Found.Add(new ReshapedReference(reshape.Owner, reshape, containing, offset));
                    }

                    break;
                case FieldReference field:
                    Type(field.FieldType, containing, offset);
                    break;
            }
        }
    }
}

/// <summary>
/// A part of a type's or method's shape composed from a substituted type: one the rewrite substitutes, or one a type
/// it does not rewrite keeps.
/// </summary>
/// <param name="Owner">The full name of the type or method whose shape names the substituted type.</param>
/// <param name="Kind">The part: <c>base type</c>, <c>interface</c>, or <c>constraint on 'T'</c>.</param>
/// <param name="Part">The type the part names.</param>
/// <param name="Cause">For a part the rewrite does not substitute, why: <see cref="Owner"/> is not rewritten.</param>
internal sealed record TypeReshape(string Owner, string Kind, TypeReference Part, string? Cause = null)
{
    /// <summary>
    /// Names the part, and, when <paramref name="mentioned"/> is a type deriving from <see cref="Owner"/>, the type that
    /// inherits it.
    /// </summary>
    public string Describe(string mentioned) =>
        $"the {Kind} of '{Owner}'" + (mentioned == Owner ? string.Empty : $" (inherited by '{mentioned}')");
}

/// <summary>A mention of a type or method whose shape one side of the rewrite boundary does not share.</summary>
/// <param name="Mentioned">The full name of the mentioned type or method.</param>
/// <param name="Reshape">How the rewrite reshapes it.</param>
/// <param name="Containing">The type or method whose declaration or body mentions it.</param>
/// <param name="Offset">The offset of the mentioning instruction, or -1 for a declaration.</param>
internal readonly record struct ReshapedReference(string Mentioned, TypeReshape Reshape, string Containing, int Offset);
