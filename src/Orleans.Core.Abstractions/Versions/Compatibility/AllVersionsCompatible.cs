// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Versions.Compatibility;

/// <summary>
/// A grain interface version compatibility strategy which treats all versions of an interface compatible with any requested version.
/// </summary>
[Serializable, GenerateSerializer, Immutable, SuppressReferenceTracking]
public sealed class AllVersionsCompatible : CompatibilityStrategy
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static AllVersionsCompatible Singleton { get; } = new AllVersionsCompatible();
}
