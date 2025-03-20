// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Runtime.MembershipService.SiloMetadata;

namespace UnitTests.PlacementFilterTests;

internal class TestSiloMetadataCache : ISiloMetadataCache
{
    private readonly Dictionary<SiloAddress, SiloMetadata> _metadata;

    public TestSiloMetadataCache(Dictionary<SiloAddress, SiloMetadata> metadata)
    {
        _metadata = metadata;
    }

    public SiloMetadata GetSiloMetadata(SiloAddress siloAddress) => _metadata.GetValueOrDefault(siloAddress) ?? SiloMetadata.Empty;
}