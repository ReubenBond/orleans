// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Placement;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains;

[SiloRoleBasedPlacement]
public class SiloRoleBasedPlacementGrain : Grain, ISiloRoleBasedPlacementGrain
{
    public Task<bool> Ping()
    {
        return Task.FromResult(true);
    }
}
