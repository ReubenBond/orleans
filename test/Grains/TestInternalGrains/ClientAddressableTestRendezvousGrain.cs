// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{

    public class ClientAddressableTestRendezvousGrain : Grain, IClientAddressableTestRendezvousGrain
    {
        private IClientAddressableTestProducer producer;

        public Task<IClientAddressableTestProducer> GetProducer()
        {
            return Task.FromResult(producer);
        }

        public Task SetProducer(IClientAddressableTestProducer producer)
        {
            this.producer = producer;
            return Task.CompletedTask;
        }
    }
}
