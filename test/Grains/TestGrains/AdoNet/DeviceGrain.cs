// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Providers;
using Orleans.SqlUtils.StorageProvider.GrainInterfaces;

namespace Orleans.SqlUtils.StorageProvider.GrainClasses
{
    [StorageProvider(ProviderName = "MemoryStore")]
    public class DeviceGrain : Grain<DeviceState>, IDeviceGrain
    {
        public Task<string> GetSerialNumber()
        {
            return Task.FromResult(State.SerialNumber);
        }

        public async Task SetOwner(ICustomerGrain customer)
        {
            if (customer == null)
                throw new ArgumentNullException(nameof(customer));

            State.Owner = customer;

            await WriteStateAsync();
        }
    }
}