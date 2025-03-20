// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Compatibility;

internal class BackwardCompatilityDirector : ICompatibilityDirector
{
    public bool IsCompatible(ushort requestedVersion, ushort currentVersion)
    {
        return requestedVersion <= currentVersion;
    }
}
