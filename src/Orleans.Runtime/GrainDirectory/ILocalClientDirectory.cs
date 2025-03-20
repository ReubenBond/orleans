// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime.GrainDirectory;

internal interface ILocalClientDirectory
{
    bool TryLocalLookup(GrainId grainId, out List<GrainAddress> addresses);
    ValueTask<List<GrainAddress>> Lookup(GrainId grainId);
}
