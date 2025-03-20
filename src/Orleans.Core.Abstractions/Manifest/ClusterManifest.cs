// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Orleans.Metadata;

/// <summary>
/// Information about types which are available in the cluster.
/// </summary>
[Serializable, GenerateSerializer, Immutable]
public sealed class ClusterManifest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterManifest"/> class.
    /// </summary>
    /// <param name="version">
    /// The manifest version.
    /// </param>
    /// <param name="silos">
    /// The silo manifests.
    /// </param>
    public ClusterManifest(
        MajorMinorVersion version,
        ImmutableDictionary<SiloAddress, GrainManifest> silos)
    {
        Version = version;
        Silos = silos;
        AllGrainManifests = silos.Values.ToImmutableArray();
    }

    /// <summary>
    /// Gets the version of this instance.
    /// </summary>
    [Id(0)]
    public MajorMinorVersion Version { get; }

    /// <summary>
    /// Gets the manifests for each silo in the cluster.
    /// </summary>
    [Id(1)]
    public ImmutableDictionary<SiloAddress, GrainManifest> Silos { get; }

    /// <summary>
    /// Gets all grain manifests.
    /// </summary>
    [Id(2)]
    public ImmutableArray<GrainManifest> AllGrainManifests { get; }
}
