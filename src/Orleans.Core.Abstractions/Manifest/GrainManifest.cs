using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Provides silo-level properties for the <see cref="GrainManifest"/>.
    /// </summary>
    public interface ISiloPropertiesProvider
    {
        /// <summary>
        /// Adds silo-level properties to <paramref name="properties"/>.
        /// </summary>
        /// <param name="properties">
        /// The properties collection which calls to this method should populate.
        /// </param>
        void Populate(Dictionary<string, string> properties);
    }

    /// <summary>
    /// Information about available grains and silo properties.
    /// </summary>
    [GenerateSerializer, Immutable]
    [Alias("Orleans.Metadata.SiloManifest")]
    public sealed class GrainManifest
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrainManifest"/> class.
        /// </summary>
        /// <param name="grains">
        /// The grain properties.
        /// </param>
        /// <param name="interfaces">
        /// The interface properties.
        /// </param>
        public GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties> grains,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties> interfaces)
            : this(grains, interfaces, ImmutableDictionary<string, string>.Empty)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainManifest"/> class.
        /// </summary>
        /// <param name="grains">
        /// The grain properties.
        /// </param>
        /// <param name="interfaces">
        /// The interface properties.
        /// </param>
        /// <param name="properties">
        /// The silo-level properties.
        /// </param>
        public GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties> grains,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties> interfaces,
            ImmutableDictionary<string, string> properties)
        {
            Interfaces = interfaces;
            Grains = grains;
            Properties = properties;
        }

        /// <summary>
        /// Gets the interfaces available on this silo.
        /// </summary>
        [Id(0)]
        public ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties> Interfaces { get; }

        /// <summary>
        /// Gets the grain types available on this silo.
        /// </summary>
        [Id(1)]
        public ImmutableDictionary<GrainType, GrainProperties> Grains { get; }

        /// <summary>
        /// Gets the silo-level properties, such as capabilities and metadata.
        /// </summary>
        [Id(2)]
        public ImmutableDictionary<string, string> Properties { get; }
    }
}
