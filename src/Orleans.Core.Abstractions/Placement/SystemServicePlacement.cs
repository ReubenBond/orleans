using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>
    /// The system service placement strategy treats the grain identity as an encoded <see cref="SiloAddress"/> and uses that as the destination or placement target.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    internal class SystemServicePlacement : PlacementStrategy
    {
        public const string SystemServiceGrainPropertyName = "sys-svc";
        public const string SystemServiceGrainPropertyValue = "true";

        public static SystemServicePlacement Singleton { get; } = new();

        /// <inheritdoc/>
        public override bool IsUsingGrainDirectory => false;

        /// <inheritdoc/>
        internal override bool IsDeterministicActivationId => true;

        /// <summary>
        /// Initializes a new instance of the <see cref="SystemServicePlacement"/> class.
        /// </summary>
        public SystemServicePlacement()
        {
        }

        public override void PopulateGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            base.PopulateGrainProperties(services, grainClass, grainType, properties);
            properties[SystemServiceGrainPropertyName] = SystemServiceGrainPropertyValue;
        }
    }
}
