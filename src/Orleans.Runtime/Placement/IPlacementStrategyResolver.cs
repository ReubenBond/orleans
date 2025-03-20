// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Metadata;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Associates a <see cref="PlacementStrategy"/> with a <see cref="GrainType"/>.
    /// </summary>
    public interface IPlacementStrategyResolver
    {
        /// <summary>
        /// Gets the placement strategy associated with the provided grain type.
        /// </summary>
        bool TryResolvePlacementStrategy(GrainType grainType, GrainProperties properties, out PlacementStrategy result);
    }
}
