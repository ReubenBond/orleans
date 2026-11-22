using System.Globalization;

namespace Orleans.Testing.Simulation.Runtime.Racing;

/// <summary>Classifies a logical memory location tracked during race exploration.</summary>
public enum RaceMemoryLocationKind
{
    /// <summary>A field on a specific weakly identified object.</summary>
    InstanceField,

    /// <summary>A process-wide static field.</summary>
    StaticField,

    /// <summary>An element in a specific weakly identified array.</summary>
    ArrayElement,

    /// <summary>A mutable collection instance.</summary>
    Collection,
}

/// <summary>
/// Stable logical identity for a tracked location. Object identities are scheduler-assigned numbers;
/// no target object is retained by this value.
/// </summary>
/// <param name="Kind">The location category.</param>
/// <param name="ObjectId">The weak target identity, or zero for a static field.</param>
/// <param name="Member">The field or collection member description.</param>
/// <param name="ElementIndex">The array element index, or <see langword="null"/>.</param>
public readonly record struct RaceMemoryLocation(
    RaceMemoryLocationKind Kind,
    long ObjectId,
    string Member,
    long? ElementIndex = null)
{
    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        RaceMemoryLocationKind.StaticField when ObjectId == 0 => Member,
        RaceMemoryLocationKind.StaticField => string.Create(
            CultureInfo.InvariantCulture,
            $"static#{ObjectId}:{Member}"),
        RaceMemoryLocationKind.ArrayElement => string.Create(
            CultureInfo.InvariantCulture,
            $"array#{ObjectId}[{ElementIndex}]"),
        RaceMemoryLocationKind.Collection => string.Create(
            CultureInfo.InvariantCulture,
            $"collection#{ObjectId}:{Member}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"object#{ObjectId}:{Member}"),
    };
}
