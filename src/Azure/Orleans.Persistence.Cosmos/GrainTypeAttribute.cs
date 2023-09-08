namespace Orleans.Persistence.Cosmos;

[AttributeUsage(AttributeTargets.Class)] 
public sealed class GrainTypeAttribute : Attribute
{
    /// <summary>
    /// Specifies the grain type name for this grain.
    /// </summary>
    /// <param name="grainType">The grain type name.</param>
    public GrainTypeAttribute(string grainType) => GrainType = grainType;

    /// <summary>
    /// Gets the grain type value.
    /// </summary>
    public string GrainType { get; }
}
