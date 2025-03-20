// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Placement;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [HashBasedPlacement]
    public class HashBasedBasedPlacementGrain : Grain, IHashBasedPlacementGrain
    {

        public Task<SiloAddress> GetSiloAddress()
        {
            return Task.FromResult(this.Runtime.SiloAddress);
        }
    }
}