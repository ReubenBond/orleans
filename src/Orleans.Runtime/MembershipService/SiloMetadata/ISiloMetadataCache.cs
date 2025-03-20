// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

namespace Orleans.Runtime.MembershipService.SiloMetadata;

public interface ISiloMetadataCache
{
    SiloMetadata GetSiloMetadata(SiloAddress siloAddress);
}