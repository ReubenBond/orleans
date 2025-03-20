// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

[Alias("Orleans.Runtime.MembershipService.SiloMetadata.ISiloMetadataSystemTarget")]
internal interface ISiloMetadataSystemTarget : ISystemTarget
{
    [Alias("GetSiloMetadata")]
    Task<SiloMetadata> GetSiloMetadata();
}