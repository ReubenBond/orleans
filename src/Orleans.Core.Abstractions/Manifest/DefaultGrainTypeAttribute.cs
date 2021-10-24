using System;
using System.Collections.Generic;
using Orleans.Metadata;

namespace Orleans;

/// <summary>
/// Specifies the default grain type to use when constructing a grain reference for this interface without specifying a grain type.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false)]
public sealed class DefaultGrainTypeAttribute : Attribute, IGrainInterfacePropertiesProviderAttribute
{
    private readonly string grainType;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultGrainTypeAttribute"/> class.
    /// </summary>
    /// <param name="grainType">
    /// The grain type.
    /// </param>
    public DefaultGrainTypeAttribute(string grainType)
    {
        this.grainType = grainType;
    }

    /// <inheritdoc />
    void IGrainInterfacePropertiesProviderAttribute.Populate(IServiceProvider services, Type type, Dictionary<string, string> properties)
    {
        properties[WellKnownGrainInterfaceProperties.DefaultGrainType] = this.grainType;
    }
}