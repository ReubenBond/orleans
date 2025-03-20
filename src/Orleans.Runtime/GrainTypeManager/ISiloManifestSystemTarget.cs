// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Metadata;

namespace Orleans.Runtime;

internal interface ISiloManifestSystemTarget : ISystemTarget
{
    ValueTask<GrainManifest> GetSiloManifest();
}