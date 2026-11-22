using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>Finds which of a set of named types a member reference's signature mentions.</summary>
internal static class SignatureMentions
{
    /// <summary>
    /// Returns the names in <paramref name="typeNames"/> that <paramref name="member"/>'s declaring type, return or
    /// field type, parameter types, or generic arguments mention, including through generic arguments, element types,
    /// and modifiers, in first-mention order.
    /// </summary>
    public static IReadOnlyList<string> Find(MemberReference member, IReadOnlySet<string> typeNames)
    {
        var found = new List<string>();
        if (typeNames.Count == 0)
        {
            return found;
        }

        Collect(member.DeclaringType, typeNames, found);
        switch (member)
        {
            case MethodReference method:
                Collect(method.ReturnType, typeNames, found);
                foreach (ParameterDefinition parameter in method.Parameters)
                {
                    Collect(parameter.ParameterType, typeNames, found);
                }

                if (method is GenericInstanceMethod generic)
                {
                    foreach (TypeReference argument in generic.GenericArguments)
                    {
                        Collect(argument, typeNames, found);
                    }
                }

                break;
            case FieldReference field:
                Collect(field.FieldType, typeNames, found);
                break;
        }

        return found;
    }

    /// <summary>
    /// Returns the names in <paramref name="typeNames"/> that <paramref name="type"/> mentions, including through
    /// generic arguments, element types, and modifiers, in first-mention order.
    /// </summary>
    public static IReadOnlyList<string> Find(TypeReference type, IReadOnlySet<string> typeNames)
    {
        var found = new List<string>();
        if (typeNames.Count > 0)
        {
            Collect(type, typeNames, found);
        }

        return found;
    }

    private static void Collect(TypeReference? type, IReadOnlySet<string> typeNames, List<string> found)
    {
        switch (type)
        {
            case null or GenericParameter:
                return;
            case GenericInstanceType generic:
                Collect(generic.ElementType, typeNames, found);
                foreach (TypeReference argument in generic.GenericArguments)
                {
                    Collect(argument, typeNames, found);
                }

                return;
            case FunctionPointerType pointer:
                Collect(pointer.ReturnType, typeNames, found);
                foreach (ParameterDefinition parameter in pointer.Parameters)
                {
                    Collect(parameter.ParameterType, typeNames, found);
                }

                return;
            case IModifierType modifier:
                Collect(modifier.ModifierType, typeNames, found);
                Collect(modifier.ElementType, typeNames, found);
                return;
            case TypeSpecification specification:
                Collect(specification.ElementType, typeNames, found);
                return;
            default:
                if (typeNames.Contains(type.FullName) && !found.Contains(type.FullName))
                {
                    found.Add(type.FullName);
                }

                return;
        }
    }
}
