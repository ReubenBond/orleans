using System;

namespace Orleans
{
    using Orleans.Runtime;

    /// <summary>
    /// Marker interface for grains
    /// </summary>
    [Alias("Orleans.IGrain")]
    public interface IGrain : IAddressable
    {
    }

    /// <summary>
    /// Marker interface for grains with <see cref="Guid"/> keys.
    /// </summary>
    [Alias("Orleans.IGrainWithGuidKey")]
    public interface IGrainWithGuidKey : IGrain
    {
    }

    /// <summary>
    /// Marker interface for grains with <see cref="long"/> keys.
    /// </summary>
    [Alias("Orleans.IGrainWithIntegerKey")]
    public interface IGrainWithIntegerKey : IGrain
    {
    }

    /// <summary>
    /// Marker interface for grains with <see cref="string"/> keys.
    /// </summary>
    [Alias("Orleans.IGrainWithStringKey")]
    public interface IGrainWithStringKey : IGrain
    {
    }

    /// <summary>
    /// Marker interface for grains with compound keys.
    /// </summary>
    [Alias("Orleans.IGrainWithGuidCompoundKey")]
    public interface IGrainWithGuidCompoundKey : IGrain
    {
    }

    /// <summary>
    /// Marker interface for grains with compound keys.
    /// </summary>
    [Alias("Orleans.IGrainWithIntegerCompoundKey")]
    public interface IGrainWithIntegerCompoundKey : IGrain
    {
    }
}
