using System.Collections.Concurrent;
using System.Text.Json;
using Orleans.Durable.Storage.Architecture.Tests.PlaneFixtures.Control;
using Orleans.Durable.Storage.Architecture.Tests.PlaneFixtures.Hosting;

namespace Orleans.Durable.Storage.Architecture.Tests.PlaneFixtures;

// The data plane of a fixture layer for PlaneSeparationTests. Each fixture which reaches the fixture's control plane
// does so through one kind of reference, at the sites the test lists for it.

/// <summary>Reaches only the data plane.</summary>
public sealed class DataPlaneOnly(int number)
{
    public int Number { get; } = number;
}

/// <summary>A base type; the constructor's call of the base constructor is an IL operand.</summary>
public sealed class DerivesFromControl : ControlBase
{
    public override int Number => 1;
}

/// <summary>An interface implementation.</summary>
public sealed class ImplementsControl : IControlContract
{
    public int Number => 1;
}

/// <summary>A concrete control-plane service held through a constructor parameter and called.</summary>
public sealed class UsesControlService(ControlService service)
{
    public int Commit() => service.Commit();
}

/// <summary>A field signature, with no IL.</summary>
public static class ControlField
{
    public const ControlValue? Nothing = null;
}

/// <summary>A parameter type.</summary>
public static class TakesControlParameter
{
    public static bool IsMissing(ControlValue? value) => value is null;
}

/// <summary>A return type.</summary>
public static class ReturnsControl
{
    public static ControlValue? None() => null;
}

/// <summary>A generic argument in a signature.</summary>
public static class GenericArgumentOfControl
{
    public static bool IsMissing(IReadOnlyList<ControlValue>? values) => values is null;
}

/// <summary>A one-dimensional array in a signature.</summary>
public static class TakesControlArray
{
    public static bool IsMissing(ControlValue[]? values) => values is null;
}

/// <summary>A multi-dimensional array in a signature.</summary>
public static class TakesControlMatrix
{
    public static bool IsMissing(ControlValue[,]? values) => values is null;
}

/// <summary>A reference in a signature.</summary>
public static class TakesControlByReference
{
    public static bool IsMissing(ref ControlValue? value) => value is null;
}

/// <summary>A pointer in a signature.</summary>
public static class TakesControlPointer
{
    public static bool IsMissing(ControlPoint* point) => point == null;
}

/// <summary>A function pointer in a signature.</summary>
public static class TakesControlFunctionPointer
{
    public static bool IsMissing(delegate*<ControlValue?, bool> callback) => callback == null;
}


/// <summary>A field's type under a custom modifier: <c>volatile</c> writes <c>modreq(IsVolatile)</c>.</summary>
public sealed class VolatileControlField
{
    private volatile ControlValue? current;

    public bool IsMissing => current is null;

    public void Clear() => current = null;
}

/// <summary>
/// A pinned local, which a fixed statement declares: it pins a field of this type, whose IL operand names this type
/// rather than the field's type.
/// </summary>
public sealed class PinsControl
{
    private ControlPoint point;

    public void Move(int x) => point = new(x);

    public nint Address()
    {
        fixed (void* address = &point)
        {
            return (nint)address;
        }
    }
}

/// <summary>A generic constraint of a method.</summary>
public static class ConstrainedToControl
{
    public static bool IsMissing<T>(T? value)
        where T : ControlBase => value is null;
}

/// <summary>A generic constraint of a type.</summary>
/// <typeparam name="T">A control-plane type.</typeparam>
public sealed class ConstrainedTypeOfControl<T>
    where T : ControlBase
{
    public T? Value { get; set; }
}

/// <summary>A property type, which its getter's signature repeats.</summary>
public static class PropertyOfControl
{
    public static ControlValue? Current => null;
}

/// <summary>An event's type, which its accessors' signatures repeat.</summary>
public sealed class EventOfControl
{
    public int Subscriptions { get; private set; }

    public event EventHandler<ControlEventArgs>? Changed
    {
        add => Subscriptions += value is null ? 0 : 1;
        remove => Subscriptions -= value is null ? 0 : 1;
    }
}

/// <summary>A type token in a method body only.</summary>
public static class ChecksForControlType
{
    public static bool IsControl(object value) => value is ControlValue;
}

/// <summary>A method call in a method body only.</summary>
public static class CallsControl
{
    public static int Answer() => ControlOperations.Answer();
}

/// <summary>A generic method of the control plane instantiated in a method body: a method specification.</summary>
public static class CallsGenericControlMethod
{
    public static int Answer() => ControlOperations.Echo(42);
}

/// <summary>
/// A method call in a method body after a dense switch, whose operand is a count and that many jump targets. The arms
/// put the last target 36 (0x24, no opcode) bytes past the switch, so a walker which skips one target too few fails.
/// </summary>
public static class CallsControlAfterSwitch
{
    public static int Choose(int value) => value switch
    {
        0 => 1000,
        1 => 1001,
        2 => 1002,
        3 => 100,
        4 => 101,
        5 => 1,
        _ => ControlOperations.Answer(),
    };
}

/// <summary>A call in a lambda, which the compiler moves to a nested compiler-generated type.</summary>
public static class CallsControlInLambda
{
    public static Func<int> Answer() => static () => ControlOperations.Answer() + 1;
}

/// <summary>A local's type in a method body only.</summary>
public static class HoldsControlInLocal
{
    public static bool IsMissing(bool reset)
    {
        ControlValue? value = null;
        if (reset)
        {
            value = null;
        }

        return value is null;
    }
}

/// <summary>A field token in a method body only.</summary>
public static class ReadsControlField
{
    public static bool IsShared() => ControlValue.Shared is not null;
}

/// <summary>A custom attribute of a type.</summary>
[ControlMarker]
public static class MarkedByControl
{
    public static int Number => 1;
}

/// <summary>A custom attribute of a method.</summary>
public static class MethodMarkedByControl
{
    [ControlMarker]
    public static int Number() => 1;
}

/// <summary>A custom attribute of a field.</summary>
public static class FieldMarkedByControl
{
    [ControlMarker]
    public const int Number = 1;
}

/// <summary>A custom attribute of a property.</summary>
public static class PropertyMarkedByControl
{
    [ControlMarker]
    public static int Number => 1;
}

/// <summary>A custom attribute of an event.</summary>
public sealed class EventMarkedByControl
{
    public int Subscriptions { get; private set; }

    [ControlMarker]
    public event EventHandler? Changed
    {
        add => Subscriptions += value is null ? 0 : 1;
        remove => Subscriptions -= value is null ? 0 : 1;
    }
}

/// <summary>A custom attribute of a parameter.</summary>
public static class ParameterMarkedByControl
{
    public static int Identity([ControlMarker] int value) => value;
}

/// <summary>A custom attribute of a return value.</summary>
public static class ReturnValueMarkedByControl
{
    [return: ControlMarker]
    public static int Number() => 1;
}

/// <summary>A custom attribute of a generic parameter.</summary>
public static class GenericParameterMarkedByControl
{
    public static T? Default<[ControlMarker] T>() => default;
}

/// <summary>A nested type, which belongs to its outer type's namespace.</summary>
public static class Outer
{
    /// <summary>Returns a control-plane type.</summary>
    public static class Nested
    {
        public static ControlValue? None() => null;
    }
}

/// <summary>A type of another assembly in a signature.</summary>
public static class ExternalInSignature
{
    public static bool IsMissing(ConcurrentQueue<int>? queue) => queue is null;
}

/// <summary>A nested type of another assembly, whose reference is scoped by its outer type's reference.</summary>
public static class ExternalNestedInSignature
{
    public static bool IsMissing(ConcurrentDictionary<string, int>.AlternateLookup<string> lookup) => lookup.Dictionary is null;
}

/// <summary>A member of a type of another assembly in a method body.</summary>
public static class ExternalInBody
{
    public static object Create() => new ConcurrentBag<int>();
}

/// <summary>A type of another assembly in a method body, as a type token.</summary>
public static class ChecksForExternalType
{
    public static bool IsOptions(object value) => value is EnumerablePartitionerOptions;
}

/// <summary>A generic method instantiated with a control-plane type, in a method body only.</summary>
public static class InstantiatesWithControl
{
    public static int Count() => Array.Empty<ControlValue>().Length;
}

/// <summary>A catch type.</summary>
public static class CatchesControl
{
    public static bool Throws(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
            return false;
        }
        catch (ControlException)
        {
            return true;
        }
    }
}

/// <summary>A width an attribute argument encodes as an enum of this assembly.</summary>
public enum FixtureWidth
{
    None,
    Wide,
}

/// <summary>A data-plane attribute which names a type.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NamesTypeAttribute(Type type, FixtureWidth width) : Attribute
{
    public Type Type { get; } = type;

    public FixtureWidth Width { get; } = width;

    /// <summary>
    /// Gets or sets a comparison, a named argument of an enum of another assembly, which the attribute encodes by its
    /// serialized name.
    /// </summary>
    public StringComparison Comparison { get; set; }

    /// <summary>Gets or sets a related type, a named argument.</summary>
    public Type? Related { get; set; }
}

/// <summary>A data-plane attribute which names types in an array argument.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NamesTypesAttribute(params Type[] types) : Attribute
{
    public IReadOnlyList<Type> Types { get; } = types;
}

/// <summary>A data-plane attribute with a boxed argument, which names a boxed enum by its type.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class HoldsValueAttribute(object value) : Attribute
{
    public object Value { get; } = value;
}

/// <summary>
/// An attribute whose arguments are enums of an assembly other than the core library: a positional one, which its
/// type reference resolves, and a boxed named one, which only its assembly-qualified serialized name resolves.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CommentHandlingAttribute(JsonCommentHandling handling) : Attribute
{
    public JsonCommentHandling Handling { get; } = handling;

    public object? Value { get; set; }
}

/// <summary>A type argument of a custom attribute.</summary>
[NamesType(typeof(ControlValue), FixtureWidth.Wide)]
public static class NamesControlInAttribute
{
    public static int Number => 1;
}

/// <summary>An array of a control-plane type, which a type argument of a custom attribute names.</summary>
[NamesType(typeof(ControlValue[]), FixtureWidth.None)]
public static class NamesControlArrayInAttribute
{
    public static int Number => 1;
}

/// <summary>A generic argument of a type which a type argument of a custom attribute names.</summary>
[NamesType(typeof(IReadOnlyList<ControlValue>), FixtureWidth.None)]
public static class NamesControlGenericArgumentInAttribute
{
    public static int Number => 1;
}

/// <summary>A generic type of the control plane, which a type argument of a custom attribute names.</summary>
[NamesType(typeof(ControlBox<int>), FixtureWidth.None)]
public static class NamesControlGenericInAttribute
{
    public static int Number => 1;
}

/// <summary>A named argument of a custom attribute.</summary>
[NamesType(typeof(DataPlaneOnly), FixtureWidth.None, Related = typeof(ControlValue))]
public static class NamesControlInNamedArgument
{
    public static int Number => 1;
}

/// <summary>An element of an array argument of a custom attribute.</summary>
[NamesTypes(typeof(DataPlaneOnly), typeof(ControlValue))]
public static class NamesControlInArrayArgument
{
    public static int Number => 1;
}

/// <summary>A control-plane enum boxed in an argument of a custom attribute, which only the argument's type names.</summary>
[HoldsValue(ControlLevel.High)]
public static class BoxesControlEnumInAttribute
{
    public static int Number => 1;
}

/// <summary>
/// A type argument of a custom attribute which names the data plane, a named argument of an enum of another
/// assembly, and enums of an assembly other than the core library.
/// </summary>
[NamesType(typeof(DataPlaneOnly), FixtureWidth.None, Comparison = StringComparison.Ordinal)]
[CommentHandling(JsonCommentHandling.Allow, Value = JsonCommentHandling.Skip)]
public static class NamesDataPlaneInAttribute
{
    public static int Number => 1;
}

/// <summary>A data-plane type which calls the hosting namespace.</summary>
public static class UsesHosting
{
    public static int Count() => FixtureWiring.Registrations;
}

/// <summary>
/// A call of a member of a generic hosting type whose signature names the control plane: a member reference, whose
/// parent names the hosting namespace and whose method signature the control plane.
/// </summary>
public static class CallsWiringWithControlSignature
{
    public static bool Accepts(FixtureWiring<int> wiring)
    {
        ArgumentNullException.ThrowIfNull(wiring);
        return wiring.Accepts(null);
    }
}

/// <summary>
/// A read of a field of a generic hosting type whose type is of the control plane: a member reference, whose parent
/// names the hosting namespace and whose field signature the control plane.
/// </summary>
public static class ReadsWiringControlField
{
    public static bool IsMissing(FixtureWiring<int> wiring)
    {
        ArgumentNullException.ThrowIfNull(wiring);
        return wiring.Control is null;
    }
}
