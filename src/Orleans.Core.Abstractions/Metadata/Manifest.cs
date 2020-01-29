using System;
using System.Collections.Immutable;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    [Serializable]
    public readonly struct GrainInterfaceId : IEquatable<GrainInterfaceId>
    {
        public readonly string TypeName;

        public GrainInterfaceId(string typeName) => this.TypeName = typeName;

        public static GrainInterfaceId Create(Type type) => new GrainInterfaceId(RuntimeTypeNameFormatter.Format(type));

        public override bool Equals(object obj) => obj is GrainInterfaceId id && this.Equals(id);

        public bool Equals(GrainInterfaceId other) => string.Equals(this.TypeName, other.TypeName, StringComparison.Ordinal);

        public override int GetHashCode() => HashCode.Combine(this.TypeName);
    }

    /// <summary>
    /// Information about available a silo.
    /// </summary>
    [Serializable]
    public class SiloManifest
    {
        // Map from formatted CLR type name -> interface metadata
        public ImmutableDictionary<GrainInterfaceId, GrainInterfaceMetadata> Interfaces { get; }

        // Map from grain type -> grain (class) metadata
        // Should this be called 'routes'?
        // Can GrainType ever be a template? Eg, how are generics encoded into GrainType?
        public ImmutableDictionary<GrainType, GrainMetadata> Grains { get; }
    }

    /// <summary>
    /// Information about types which are available in the cluster.
    /// </summary>
    [Serializable]
    public class ClusterManifest
    {
        /// <summary>
        /// Manifests for each silo in the cluster.
        /// </summary>
        public ImmutableDictionary<SiloAddress, SiloManifest> Silos { get; }
    }
}
