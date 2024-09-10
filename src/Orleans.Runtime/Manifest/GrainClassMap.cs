#nullable enable
using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Metadata;

/// <summary>
/// Mapping between <see cref="GrainType"/> and implementing <see cref="Type"/>.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="GrainClassMap"/> class.
/// </remarks>
/// <param name="typeConverter">The type converter.</param>
/// <param name="classes">The grain classes.</param>
public class GrainClassMap(TypeConverter typeConverter, ImmutableDictionary<GrainType, Type> classes)
{

    /// <summary>
    /// Returns the grain class type corresponding to the provided grain type.
    /// </summary>
    /// <param name="grainType">Type of the grain.</param>
    /// <param name="grainClass">The grain class.</param>
    /// <returns><see langword="true"/> if a corresponding grain class was found, <see langword="false"/> otherwise.</returns>
    public bool TryGetGrainClass(GrainType grainType, [NotNullWhen(true)] out Type? grainClass)
    {
        GrainType lookupType;
        Type[]? args;
        if (GenericGrainType.TryParse(grainType, out var genericId))
        {
            lookupType = genericId.GetUnconstructedGrainType().GrainType;
            args = genericId.GetArguments(typeConverter);
        }
        else
        {
            lookupType = grainType;
            args = default;
        }

        if (!classes.TryGetValue(lookupType, out grainClass))
        {
            return false;
        }

        if (args is not null)
        {
            grainClass = grainClass.MakeGenericType(args);
        }

        return true;
    }
}
