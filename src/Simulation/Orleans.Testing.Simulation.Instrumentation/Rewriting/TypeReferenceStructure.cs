using System.Runtime.CompilerServices;
using System.Text;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>Compares and inflates Cecil type graphs without assuming that they are acyclic.</summary>
internal static class TypeReferenceStructure
{
    public static bool AreEquivalent(
        TypeReference left,
        MethodReference? leftOwner,
        TypeReference right,
        MethodReference? rightOwner) =>
        AreEquivalent(
            left,
            leftOwner,
            substituteLeft: true,
            right,
            rightOwner,
            substituteRight: true,
            new HashSet<TypePair>(TypePairComparer.Instance));

    public static string Shape(TypeReference type, MethodReference owner) =>
        Shape(type, owner, allowSubstitution: true, new Dictionary<TypeNode, int>(TypeNodeComparer.Instance));

    public static TypeReference Inflate(TypeReference type, MethodReference owner) =>
        Inflate(
            type,
            candidate => ResolveGenericParameter(candidate, owner),
            allowSubstitution: true,
            new Dictionary<TypeNode, TypeReference>(TypeNodeComparer.Instance));

    public static TypeReference Inflate(TypeReference type, FieldReference owner) =>
        Inflate(
            type,
            candidate => ResolveGenericParameter(candidate, owner),
            allowSubstitution: true,
            new Dictionary<TypeNode, TypeReference>(TypeNodeComparer.Instance));

    private static bool AreEquivalent(
        TypeReference left,
        MethodReference? leftOwner,
        bool substituteLeft,
        TypeReference right,
        MethodReference? rightOwner,
        bool substituteRight,
        HashSet<TypePair> visited)
    {
        if (ReferenceEquals(left, right)
            && ReferenceEquals(leftOwner, rightOwner)
            && substituteLeft == substituteRight)
        {
            return true;
        }

        if (!visited.Add(new TypePair(left, substituteLeft, right, substituteRight)))
        {
            return true;
        }

        TypeReference resolvedLeft = substituteLeft ? ResolveGenericParameter(left, leftOwner) : left;
        TypeReference resolvedRight = substituteRight ? ResolveGenericParameter(right, rightOwner) : right;
        if (!ReferenceEquals(resolvedLeft, left) || !ReferenceEquals(resolvedRight, right))
        {
            return AreEquivalent(
                resolvedLeft,
                leftOwner,
                ReferenceEquals(resolvedLeft, left) && substituteLeft,
                resolvedRight,
                rightOwner,
                ReferenceEquals(resolvedRight, right) && substituteRight,
                visited);
        }

        if (left is GenericParameter || right is GenericParameter)
        {
            return left is GenericParameter leftGeneric
                && right is GenericParameter rightGeneric
                && leftGeneric.Type == rightGeneric.Type
                && leftGeneric.Position == rightGeneric.Position;
        }

        return (left, right) switch
        {
            (ByReferenceType l, ByReferenceType r) =>
                AreEquivalent(
                    l.ElementType, leftOwner, substituteLeft,
                    r.ElementType, rightOwner, substituteRight,
                    visited),
            (PointerType l, PointerType r) =>
                AreEquivalent(
                    l.ElementType, leftOwner, substituteLeft,
                    r.ElementType, rightOwner, substituteRight,
                    visited),
            (ArrayType l, ArrayType r) =>
                ArraysAreEquivalent(
                    l, leftOwner, substituteLeft,
                    r, rightOwner, substituteRight,
                    visited),
            (GenericInstanceType l, GenericInstanceType r) =>
                GenericInstancesAreEquivalent(
                    l, leftOwner, substituteLeft,
                    r, rightOwner, substituteRight,
                    visited),
            (RequiredModifierType l, RequiredModifierType r) =>
                AreEquivalent(
                    l.ModifierType, leftOwner, substituteLeft,
                    r.ModifierType, rightOwner, substituteRight,
                    visited)
                && AreEquivalent(
                    l.ElementType, leftOwner, substituteLeft,
                    r.ElementType, rightOwner, substituteRight,
                    visited),
            (OptionalModifierType l, OptionalModifierType r) =>
                AreEquivalent(
                    l.ModifierType, leftOwner, substituteLeft,
                    r.ModifierType, rightOwner, substituteRight,
                    visited)
                && AreEquivalent(
                    l.ElementType, leftOwner, substituteLeft,
                    r.ElementType, rightOwner, substituteRight,
                    visited),
            (PinnedType l, PinnedType r) =>
                AreEquivalent(
                    l.ElementType, leftOwner, substituteLeft,
                    r.ElementType, rightOwner, substituteRight,
                    visited),
            (SentinelType l, SentinelType r) =>
                AreEquivalent(
                    l.ElementType, leftOwner, substituteLeft,
                    r.ElementType, rightOwner, substituteRight,
                    visited),
            (FunctionPointerType l, FunctionPointerType r) =>
                FunctionPointersAreEquivalent(
                    l, leftOwner, substituteLeft,
                    r, rightOwner, substituteRight,
                    visited),
            (TypeSpecification, _) or (_, TypeSpecification) => false,
            _ => string.Equals(left.FullName, right.FullName, StringComparison.Ordinal),
        };
    }

    private static bool ArraysAreEquivalent(
        ArrayType left,
        MethodReference? leftOwner,
        bool substituteLeft,
        ArrayType right,
        MethodReference? rightOwner,
        bool substituteRight,
        HashSet<TypePair> visited)
    {
        if (left.Rank != right.Rank
            || left.IsVector != right.IsVector
            || left.Dimensions.Count != right.Dimensions.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Dimensions.Count; i++)
        {
            ArrayDimension leftDimension = left.Dimensions[i];
            ArrayDimension rightDimension = right.Dimensions[i];
            if (leftDimension.LowerBound != rightDimension.LowerBound
                || leftDimension.UpperBound != rightDimension.UpperBound)
            {
                return false;
            }
        }

        return AreEquivalent(
            left.ElementType, leftOwner, substituteLeft,
            right.ElementType, rightOwner, substituteRight,
            visited);
    }

    private static bool GenericInstancesAreEquivalent(
        GenericInstanceType left,
        MethodReference? leftOwner,
        bool substituteLeft,
        GenericInstanceType right,
        MethodReference? rightOwner,
        bool substituteRight,
        HashSet<TypePair> visited)
    {
        if (left.GenericArguments.Count != right.GenericArguments.Count
            || !AreEquivalent(
                left.ElementType, leftOwner, substituteLeft,
                right.ElementType, rightOwner, substituteRight,
                visited))
        {
            return false;
        }

        for (int i = 0; i < left.GenericArguments.Count; i++)
        {
            if (!AreEquivalent(
                left.GenericArguments[i],
                leftOwner,
                substituteLeft,
                right.GenericArguments[i],
                rightOwner,
                substituteRight,
                visited))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FunctionPointersAreEquivalent(
        FunctionPointerType left,
        MethodReference? leftOwner,
        bool substituteLeft,
        FunctionPointerType right,
        MethodReference? rightOwner,
        bool substituteRight,
        HashSet<TypePair> visited)
    {
        if (left.HasThis != right.HasThis
            || left.ExplicitThis != right.ExplicitThis
            || left.CallingConvention != right.CallingConvention
            || left.GenericParameters.Count != right.GenericParameters.Count
            || left.Parameters.Count != right.Parameters.Count
            || !AreEquivalent(
                left.ReturnType, leftOwner, substituteLeft,
                right.ReturnType, rightOwner, substituteRight,
                visited))
        {
            return false;
        }

        for (int i = 0; i < left.Parameters.Count; i++)
        {
            if (!AreEquivalent(
                left.Parameters[i].ParameterType,
                leftOwner,
                substituteLeft,
                right.Parameters[i].ParameterType,
                rightOwner,
                substituteRight,
                visited))
            {
                return false;
            }
        }

        return true;
    }

    private static string Shape(
        TypeReference type,
        MethodReference owner,
        bool allowSubstitution,
        Dictionary<TypeNode, int> active)
    {
        TypeReference resolved = allowSubstitution ? ResolveGenericParameter(type, owner) : type;
        if (!ReferenceEquals(resolved, type))
        {
            return Shape(resolved, owner, allowSubstitution: false, active);
        }

        var node = new TypeNode(type, allowSubstitution);
        if (active.TryGetValue(node, out int recursionIndex))
        {
            return "#" + recursionIndex;
        }

        active.Add(node, active.Count);
        try
        {
            if (type is GenericParameter parameter)
            {
                return GenericParameterShape(parameter);
            }

            return type switch
            {
                ByReferenceType byReference =>
                    Shape(byReference.ElementType, owner, allowSubstitution, active) + "&",
                PointerType pointer =>
                    Shape(pointer.ElementType, owner, allowSubstitution, active) + "*",
                ArrayType array => ArrayShape(array, owner, allowSubstitution, active),
                GenericInstanceType generic => GenericInstanceShape(generic, owner, allowSubstitution, active),
                RequiredModifierType modifier =>
                    Shape(modifier.ElementType, owner, allowSubstitution, active)
                    + " modreq(" + Shape(modifier.ModifierType, owner, allowSubstitution, active) + ")",
                OptionalModifierType modifier =>
                    Shape(modifier.ElementType, owner, allowSubstitution, active)
                    + " modopt(" + Shape(modifier.ModifierType, owner, allowSubstitution, active) + ")",
                PinnedType pinned => Shape(pinned.ElementType, owner, allowSubstitution, active) + " pinned",
                SentinelType sentinel => Shape(sentinel.ElementType, owner, allowSubstitution, active) + " sentinel",
                FunctionPointerType functionPointer =>
                    FunctionPointerShape(functionPointer, owner, allowSubstitution, active),
                _ => type.FullName,
            };
        }
        finally
        {
            active.Remove(node);
        }
    }

    private static string ArrayShape(
        ArrayType array,
        MethodReference owner,
        bool allowSubstitution,
        Dictionary<TypeNode, int> active)
    {
        if (array.IsVector)
        {
            return Shape(array.ElementType, owner, allowSubstitution, active) + "[]";
        }

        var builder = new StringBuilder(
            Shape(array.ElementType, owner, allowSubstitution, active)).Append('[');
        for (int i = 0; i < array.Dimensions.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            ArrayDimension dimension = array.Dimensions[i];
            if (dimension.IsSized)
            {
                builder.Append(dimension.LowerBound).Append("...").Append(dimension.UpperBound);
            }
        }

        return builder.Append(']').ToString();
    }

    private static string GenericInstanceShape(
        GenericInstanceType generic,
        MethodReference owner,
        bool allowSubstitution,
        Dictionary<TypeNode, int> active)
    {
        var builder = new StringBuilder(generic.ElementType.FullName).Append('<');
        for (int i = 0; i < generic.GenericArguments.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(Shape(generic.GenericArguments[i], owner, allowSubstitution, active));
        }

        return builder.Append('>').ToString();
    }

    private static string FunctionPointerShape(
        FunctionPointerType functionPointer,
        MethodReference owner,
        bool allowSubstitution,
        Dictionary<TypeNode, int> active)
    {
        var builder = new StringBuilder("method[")
            .Append(functionPointer.CallingConvention)
            .Append(";this=")
            .Append(functionPointer.HasThis)
            .Append(";explicit=")
            .Append(functionPointer.ExplicitThis)
            .Append("] ")
            .Append(Shape(functionPointer.ReturnType, owner, allowSubstitution, active))
            .Append(" *(");
        for (int i = 0; i < functionPointer.Parameters.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(Shape(
                functionPointer.Parameters[i].ParameterType,
                owner,
                allowSubstitution,
                active));
        }

        return builder.Append(')').ToString();
    }

    private static TypeReference Inflate(
        TypeReference type,
        Func<TypeReference, TypeReference> resolve,
        bool allowSubstitution,
        Dictionary<TypeNode, TypeReference> memo)
    {
        var node = new TypeNode(type, allowSubstitution);
        if (memo.TryGetValue(node, out TypeReference? cached))
        {
            return cached;
        }

        TypeReference resolved = allowSubstitution ? resolve(type) : type;
        if (!ReferenceEquals(resolved, type))
        {
            TypeReference inflated = Inflate(resolved, resolve, allowSubstitution: false, memo);
            memo[node] = inflated;
            return inflated;
        }

        TypeReference result;
        switch (type)
        {
            case GenericInstanceType generic:
                {
                    var instance = new GenericInstanceType(
                        Inflate(generic.ElementType, resolve, allowSubstitution, memo));
                    memo[node] = instance;
                    foreach (TypeReference argument in generic.GenericArguments)
                    {
                        instance.GenericArguments.Add(Inflate(argument, resolve, allowSubstitution, memo));
                    }

                    return instance;
                }

            case FunctionPointerType functionPointer:
                {
                    var inflated = new FunctionPointerType
                    {
                        HasThis = functionPointer.HasThis,
                        ExplicitThis = functionPointer.ExplicitThis,
                        CallingConvention = functionPointer.CallingConvention,
                    };
                    memo[node] = inflated;
                    inflated.ReturnType = Inflate(
                        functionPointer.ReturnType, resolve, allowSubstitution, memo);
                    foreach (ParameterDefinition parameter in functionPointer.Parameters)
                    {
                        inflated.Parameters.Add(new ParameterDefinition(
                            parameter.Name,
                            parameter.Attributes,
                            Inflate(parameter.ParameterType, resolve, allowSubstitution, memo)));
                    }

                    return inflated;
                }

            case ByReferenceType byReference:
                result = new ByReferenceType(
                    Inflate(byReference.ElementType, resolve, allowSubstitution, memo));
                break;
            case PointerType pointer:
                result = new PointerType(Inflate(pointer.ElementType, resolve, allowSubstitution, memo));
                break;
            case ArrayType array:
                {
                    TypeReference element = Inflate(array.ElementType, resolve, allowSubstitution, memo);
                    var inflated = array.IsVector ? new ArrayType(element) : new ArrayType(element, array.Rank);
                    for (int i = 0; i < array.Dimensions.Count; i++)
                    {
                        ArrayDimension dimension = inflated.Dimensions[i];
                        dimension.LowerBound = array.Dimensions[i].LowerBound;
                        dimension.UpperBound = array.Dimensions[i].UpperBound;
                        inflated.Dimensions[i] = dimension;
                    }

                    result = inflated;
                    break;
                }

            case RequiredModifierType modifier:
                result = new RequiredModifierType(
                    Inflate(modifier.ModifierType, resolve, allowSubstitution, memo),
                    Inflate(modifier.ElementType, resolve, allowSubstitution, memo));
                break;
            case OptionalModifierType modifier:
                result = new OptionalModifierType(
                    Inflate(modifier.ModifierType, resolve, allowSubstitution, memo),
                    Inflate(modifier.ElementType, resolve, allowSubstitution, memo));
                break;
            case PinnedType pinned:
                result = new PinnedType(Inflate(pinned.ElementType, resolve, allowSubstitution, memo));
                break;
            case SentinelType sentinel:
                result = new SentinelType(
                    Inflate(sentinel.ElementType, resolve, allowSubstitution, memo));
                break;
            default:
                result = type;
                break;
        }

        memo[node] = result;
        return result;
    }

    private static TypeReference ResolveGenericParameter(TypeReference type, MethodReference? owner)
    {
        if (type is not GenericParameter parameter || owner is null)
        {
            return type;
        }

        if (parameter.Type == GenericParameterType.Method
            && owner is GenericInstanceMethod genericMethod
            && parameter.Position < genericMethod.GenericArguments.Count
            && HasSameOwner(parameter.Owner, genericMethod.ElementMethod))
        {
            return genericMethod.GenericArguments[parameter.Position];
        }

        if (parameter.Type == GenericParameterType.Type
            && owner.DeclaringType is GenericInstanceType genericType
            && parameter.Position < genericType.GenericArguments.Count
            && HasSameOwner(parameter.Owner, genericType.ElementType))
        {
            return genericType.GenericArguments[parameter.Position];
        }

        return type;
    }

    private static TypeReference ResolveGenericParameter(TypeReference type, FieldReference owner)
    {
        if (type is GenericParameter parameter
            && parameter.Type == GenericParameterType.Type
            && owner.DeclaringType is GenericInstanceType genericType
            && parameter.Position < genericType.GenericArguments.Count
            && HasSameOwner(parameter.Owner, genericType.ElementType))
        {
            return genericType.GenericArguments[parameter.Position];
        }

        return type;
    }

    private static bool HasSameOwner(IGenericParameterProvider parameterOwner, IGenericParameterProvider candidate)
    {
        if (ReferenceEquals(parameterOwner, candidate))
        {
            return true;
        }

        return (parameterOwner, candidate) switch
        {
            (TypeReference left, TypeReference right) =>
                string.Equals(left.FullName, right.FullName, StringComparison.Ordinal),
            (MethodReference left, MethodReference right) =>
                string.Equals(left.FullName, right.FullName, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static string GenericParameterShape(GenericParameter parameter) =>
        (parameter.Type == GenericParameterType.Method ? "!!" : "!") + parameter.Position;

    private readonly record struct TypeNode(TypeReference Type, bool AllowSubstitution);

    private readonly record struct TypePair(
        TypeReference Left,
        bool SubstituteLeft,
        TypeReference Right,
        bool SubstituteRight);

    private sealed class TypeNodeComparer : IEqualityComparer<TypeNode>
    {
        public static TypeNodeComparer Instance { get; } = new();

        public bool Equals(TypeNode x, TypeNode y) =>
            ReferenceEquals(x.Type, y.Type) && x.AllowSubstitution == y.AllowSubstitution;

        public int GetHashCode(TypeNode obj) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Type), obj.AllowSubstitution);
    }

    private sealed class TypePairComparer : IEqualityComparer<TypePair>
    {
        public static TypePairComparer Instance { get; } = new();

        public bool Equals(TypePair x, TypePair y) =>
            ReferenceEquals(x.Left, y.Left)
            && x.SubstituteLeft == y.SubstituteLeft
            && ReferenceEquals(x.Right, y.Right)
            && x.SubstituteRight == y.SubstituteRight;

        public int GetHashCode(TypePair obj) =>
            HashCode.Combine(
                RuntimeHelpers.GetHashCode(obj.Left),
                obj.SubstituteLeft,
                RuntimeHelpers.GetHashCode(obj.Right),
                obj.SubstituteRight);
    }
}
