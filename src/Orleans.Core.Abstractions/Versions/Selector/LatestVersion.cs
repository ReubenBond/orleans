using System;

namespace Orleans.Versions.Selector
{
    /// <summary>
    /// Grain interface version selector which always selects the highest compatible version.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable, SuppressReferenceTracking]
    [Alias("Orleans.Versions.Selector.LatestVersion")]
    public sealed class LatestVersion : VersionSelectorStrategy
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        public static LatestVersion Singleton { get; } = new LatestVersion();
    }
}