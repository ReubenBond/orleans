using Orleans.Runtime;

namespace Orleans.Metadata
{
    public static class WellKnownGrainTypeProperties
    {
        /// <summary>
        /// The name of the placement policy for grains of this type.
        /// </summary>
        public const string PlacementPolicy = "placement-policy";

        /// <summary>
        /// The directory policy for grains of this type.
        /// </summary>
        public const string DirectoryPolicy = "directory-policy";

        /// <summary>
        /// The multi-cluster registration policy for grains of this type.
        /// </summary>
        public const string MultiClusterRegistrationPolicy = "multi-cluster-registration-policy";

        /// <summary>
        /// The legacy grain id type for this grain, if this grain implements a legacy grain interface such as <see cref="IGrainWithGuidKey"/>.
        /// For valid values, see <see cref="LegacyGrainIdTypes"/>.
        /// </summary>
        public const string LegacyGrainIdType = "legacy-grain-id-type";

        /// <summary>
        /// Whether or not messages to this grain are unordered.
        /// </summary>
        public const string Unordered = "unordered";

        public static class LegacyGrainIdTypes
        {
            public const string Guid = "guid";
            public const string Integer = "long";
            public const string String = "string";
            public const string GuidPlusString = "guid+string";
            public const string IntegerPlusString = "long+string";
        }
    }

    public static class WellKnownGrainInterfaceProperties
    {
        /// <summary>
        /// The version of this interface encoded as a decimal integer.
        /// </summary>
        public const string Version = "version";

        /// <summary>
        /// The encoded <see cref="GrainType"/> corresponding to the primary implementation of an interface.
        /// This is used for resolving a grain type from an interface.
        /// </summary>
        public const string DefaultGrainType = "primary-grain-type";
    }
}
