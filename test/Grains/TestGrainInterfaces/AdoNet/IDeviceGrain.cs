// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.SqlUtils.StorageProvider.GrainInterfaces;

public interface IDeviceGrain : IGrainWithGuidKey
{
    Task<string> GetSerialNumber();

    Task SetOwner(ICustomerGrain customer);
}