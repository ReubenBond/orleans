using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// The ledger replica contract stays minimal by enforcement (implementation plan, decision 12; ledger-replicas.md
/// §2.4): its component (<see cref="Components"/>) depends only on Buffers and grants no other component its internals
/// (<see cref="ComponentDependencyTests"/>), so a provider reaches it only through its public types, and those types and
/// their public and protected members are the snapshot beside this file, so a change to the contract's surface is an
/// explicit spec change.
/// </summary>
public sealed class LedgerReplicaContractTests
{
    private const string SnapshotName = nameof(LedgerReplicaContractTests) + ".PublicMembers";

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private const string Component = "Orleans.Durable.Storage.Engine.Ledgers.Abstractions";

    /// <summary>The contract's public types: those of the Engine's assembly which its component claims.</summary>
    private static IEnumerable<Type> Contract => typeof(ILedgerReplica).Assembly.GetExportedTypes()
        .Where(static type => Components.Production.Of(type.Namespace ?? string.Empty, type.FullName!)?.Name == Component);

    [Fact]
    public void TheContractGrantsNoInternals()
    {
        Assert.Equal(["Orleans.Durable.Storage.Engine.Buffers"], Components.Production.ByName[Component].Dependencies);
        Assert.Empty(Components.Production.ByName[Component].Grants);
    }

    /// <summary>
    /// The contract's public types, each with its kind, base type, and interfaces, and their public and protected
    /// members, each with its signature, parameter names, defaults, and nullability, one per line in ordinal order, are
    /// the checked-in snapshot. On a difference the test writes the rendered surface beside the snapshot as a
    /// <c>.received.txt</c> file, which git ignores.
    /// </summary>
    [Fact]
    public void TheContractsPublicMembersAreItsSnapshot()
    {
        var directory = Path.Combine(Repository.StorageRoot, "Orleans.Durable.Storage.Architecture.Tests");
        var snapshot = Path.Combine(directory, SnapshotName + ".txt");
        var received = Path.Combine(directory, SnapshotName + ".received.txt");
        var rendered = Render(Contract);
        var expected = File.Exists(snapshot) ? File.ReadAllLines(snapshot) : [];
        if (rendered.SequenceEqual(expected, StringComparer.Ordinal))
        {
            File.Delete(received);
            return;
        }

        File.WriteAllText(received, string.Join('\n', rendered) + "\n");
        var removed = expected.Except(rendered, StringComparer.Ordinal).Select(static line => "- " + line);
        var added = rendered.Except(expected, StringComparer.Ordinal).Select(static line => "+ " + line);
        Assert.Fail(
            $"The public surface of {Component} differs from {snapshot}. If the change is intended, specify it " +
            $"in ledger-replicas.md §2 and §4, then replace the snapshot with {received}.\n" +
            string.Join('\n', removed.Concat(added)));
    }


    /// <summary>Renders <paramref name="types"/> and their public and protected members, one per line, in ordinal order.</summary>
    internal static string[] Render(IEnumerable<Type> types)
    {
        var nullability = new NullabilityInfoContext();
        return
        [
            .. types
                .SelectMany(type => Members(type, nullability).Prepend(TypeLine(type)))
                .Order(StringComparer.Ordinal),
        ];
    }

    private static string TypeLine(Type type)
    {
        var bases = type.GetInterfaces()
            .Except(type.BaseType?.GetInterfaces() ?? [])
            .Select(static implemented => Name(implemented, nullability: null))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (type.IsEnum)
        {
            bases.Insert(0, Name(Enum.GetUnderlyingType(type), nullability: null));
        }
        else if (type.IsClass && type.BaseType is { } baseType && baseType != typeof(object))
        {
            bases.Insert(0, Name(baseType, nullability: null));
        }

        var kind = type switch
        {
            { IsInterface: true } => "interface",
            { IsEnum: true } => "enum",
            { IsValueType: true } => IsMarked(type, "System.Runtime.CompilerServices.IsReadOnlyAttribute") ? "readonly struct" : "struct",
            { IsAbstract: true, IsSealed: true } => "static class",
            { IsSealed: true } => "sealed class",
            { IsAbstract: true } => "abstract class",
            _ => "class",
        };
        return $"{Name(type, nullability: null)} ({kind}{(bases.Count == 0 ? string.Empty : " : " + string.Join(", ", bases))})";
    }

    private static IEnumerable<string> Members(Type type, NullabilityInfoContext nullability)
    {
        var owner = Name(type, nullability: null);
        foreach (var member in type.GetMembers(Declared).Where(static member => !member.Name.StartsWith('<') && member is not FieldInfo { IsSpecialName: true }))
        {
            var line = member switch
            {
                ConstructorInfo constructor when IsVisible(constructor) =>
                    $"{owner}.{constructor.Name}({Parameters(constructor.GetParameters(), nullability)}){Access(constructor)}",
                MethodInfo method when IsVisible(method) && (!method.IsSpecialName || method.Name.StartsWith("op_", StringComparison.Ordinal)) =>
                    $"{owner}.{method.Name}{GenericParameters(method)}({Parameters(method.GetParameters(), nullability)}): " +
                    $"{Static(method)}{Name(method.ReturnType, nullability.Create(method.ReturnParameter))}{Access(method)}",
                PropertyInfo property when property.GetAccessors(nonPublic: true).Any(IsVisible) => PropertyLine(owner, property, nullability),
                FieldInfo field when type.IsEnum && field.IsStatic =>
                    $"{owner}.{field.Name} = {Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture)}",
                FieldInfo field when field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly =>
                    $"{owner}.{field.Name}: {(field.IsLiteral ? "const " : string.Empty)}{(field.IsStatic && !field.IsLiteral ? "static " : string.Empty)}" +
                    $"{(field.IsInitOnly ? "readonly " : string.Empty)}{Name(field.FieldType, nullability.Create(field))}" +
                    $"{(field.IsLiteral ? " = " + Literal(field.GetRawConstantValue(), field.FieldType) : string.Empty)}{Access(field)}",
                EventInfo @event when @event.AddMethod is { } add && IsVisible(add) =>
                    $"{owner}.{@event.Name}: event {Name(@event.EventHandlerType!, nullability.Create(@event))}{Access(add)}",
                _ => null,
            };
            if (line is not null)
            {
                yield return line;
            }
        }
    }

    private static string PropertyLine(string owner, PropertyInfo property, NullabilityInfoContext nullability)
    {
        var getter = property.GetMethod is { } get && IsVisible(get) ? $"{Protected(get)}get; " : string.Empty;
        var setter = property.SetMethod is { } set && IsVisible(set)
            ? $"{Protected(set)}{(set.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) ? "init" : "set")}; "
            : string.Empty;
        var indexer = property.GetIndexParameters() is { Length: > 0 } index ? $"[{Parameters(index, nullability)}]" : string.Empty;
        var accessor = property.GetAccessors(nonPublic: true).First(IsVisible);
        return $"{owner}.{property.Name}{indexer}: {Static(accessor)}{(property.IsDefined(typeof(RequiredMemberAttribute)) ? "required " : string.Empty)}" +
            $"{Name(property.PropertyType, nullability.Create(property))} {{ {getter}{setter}}}";
    }

    private static string Parameters(ParameterInfo[] parameters, NullabilityInfoContext nullability) =>
        string.Join(", ", parameters.Select(parameter =>
        {
            var type = parameter.ParameterType;
            var modifier = !type.IsByRef ? string.Empty : parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref ";
            var prefix = parameter.IsDefined(typeof(ParamArrayAttribute)) ? "params " : modifier;
            var rendered = $"{prefix}{Name(type.IsByRef ? type.GetElementType()! : type, nullability.Create(parameter))} {parameter.Name}";
            return parameter.HasDefaultValue ? $"{rendered} = {Literal(parameter.DefaultValue, type)}" : rendered;
        }));

    private static string GenericParameters(MethodInfo method) =>
        method.IsGenericMethodDefinition ? $"<{string.Join(", ", method.GetGenericArguments().Select(static argument => argument.Name))}>" : string.Empty;

    /// <summary>
    /// A type's name: its full name, with generic arguments in angle brackets, and <c>?</c> where it is a nullable value
    /// type or <paramref name="nullability"/> marks a reference nullable.
    /// </summary>
    private static string Name(Type type, NullabilityInfo? nullability)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Name(underlying, nullability: null) + "?";
        }

        var nullable = !type.IsValueType && nullability?.ReadState == NullabilityState.Nullable ? "?" : string.Empty;
        if (type.IsArray)
        {
            return $"{Name(type.GetElementType()!, nullability?.ElementType)}[{new string(',', type.GetArrayRank() - 1)}]{nullable}";
        }

        if (type.IsGenericParameter || !type.IsGenericType)
        {
            return (type.IsGenericParameter ? type.Name : type.FullName ?? type.Name) + nullable;
        }

        var definition = type.GetGenericTypeDefinition().FullName!;
        var arguments = type.GetGenericArguments()
            .Select((argument, index) => Name(argument, nullability?.GenericTypeArguments.ElementAtOrDefault(index)));
        return $"{definition[..definition.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", arguments)}>{nullable}";
    }

    private static string Literal(object? value, Type type) => value switch
    {
        null => type.IsValueType ? "default" : "null",
        string text => $"\"{text}\"",
        bool flag => flag ? "true" : "false",
        Enum member => $"{Name(member.GetType(), nullability: null)}.{member}",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };

    private static bool IsVisible(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static string Static(MethodBase method) => method.IsStatic ? "static " : string.Empty;

    private static string Protected(MethodBase method) => method.IsPublic ? string.Empty : "protected ";

    private static string Access(MethodBase method) => method.IsPublic ? string.Empty : " [protected]";

    private static string Access(FieldInfo field) => field.IsPublic ? string.Empty : " [protected]";

    private static bool IsMarked(Type type, string attribute) =>
        type.CustomAttributes.Any(data => string.Equals(data.AttributeType.FullName, attribute, StringComparison.Ordinal));
}
