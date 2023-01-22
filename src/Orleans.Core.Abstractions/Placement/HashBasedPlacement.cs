using System;

namespace Orleans.Runtime
{
    [Serializable, GenerateSerializer, Immutable, SuppressReferenceTracking]
    public sealed class HashBasedPlacement : PlacementStrategy
    {
        internal static HashBasedPlacement Singleton { get; } = new HashBasedPlacement();

        /// <inheritdoc />
        public override bool IsGrain => true;
    }
}
