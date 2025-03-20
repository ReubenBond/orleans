// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Orleans.Versions.Selector
{
    /// <summary>
    /// Grain interface version selector which always selects the highest compatible version.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable, SuppressReferenceTracking]
    public sealed class LatestVersion : VersionSelectorStrategy
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        public static LatestVersion Singleton { get; } = new LatestVersion();
    }
}