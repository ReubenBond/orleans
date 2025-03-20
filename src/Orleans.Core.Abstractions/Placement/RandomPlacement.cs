// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// The random placement strategy specifies that new activations of a grain should be placed on a random, compatible server.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable, SuppressReferenceTracking]
    public sealed class RandomPlacement : PlacementStrategy
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        internal static RandomPlacement Singleton { get; } = new RandomPlacement();
    }
}
