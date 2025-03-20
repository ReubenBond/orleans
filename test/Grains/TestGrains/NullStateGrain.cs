// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class NullStateGrain : Grain<NullableState>, INullStateGrain
    {
        public async Task SetStateAndDeactivate(NullableState state)
        {
            this.State = state;
            await WriteStateAsync();
            DeactivateOnIdle();
        }

        public Task<NullableState> GetState()
        {
            return Task.FromResult(this.State);
        }
    }
}