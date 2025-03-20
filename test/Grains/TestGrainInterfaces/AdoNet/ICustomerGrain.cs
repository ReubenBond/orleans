// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.SqlUtils.StorageProvider.GrainInterfaces
{
    public interface ICustomerGrain : IGrainWithIntegerKey
    {
        Task<string> IntroduceSelf();
         
        Task Set(int customerId, string firstName, string lastName);

        Task AddDevice(IDeviceGrain device);

        Task SetRandomState();
    }
}
