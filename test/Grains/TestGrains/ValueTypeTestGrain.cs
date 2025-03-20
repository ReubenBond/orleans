// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class ValueTypeTestGrain : Grain<ValueTypeTestData>, IValueTypeTestGrain
    {
        public ValueTypeTestGrain()
        {
        }

        public async Task<ValueTypeTestData> GetStateData()
        {
            await ReadStateAsync();
            return State;
        }

        public Task SetStateData(ValueTypeTestData d)
        {
            State = d;
            return WriteStateAsync();
        }
    }
}
