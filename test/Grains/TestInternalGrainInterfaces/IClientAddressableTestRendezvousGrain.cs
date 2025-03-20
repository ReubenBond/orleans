// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

public interface IClientAddressableTestRendezvousGrain : IGrainWithIntegerKey
{
    Task<IClientAddressableTestProducer> GetProducer();
    Task SetProducer(IClientAddressableTestProducer producer);
}
