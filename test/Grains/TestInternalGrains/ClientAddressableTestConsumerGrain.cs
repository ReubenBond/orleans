// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ClientAddressableTestConsumerGrain : Grain, IClientAddressableTestConsumer
    {
        private IClientAddressableTestProducer producer;
        
        public async Task<int> PollProducer()
        {
            return await producer.Poll();
        }

        public async Task Setup()
        {
            var rendezvous = GrainFactory.GetGrain<IClientAddressableTestRendezvousGrain>(0);
            producer = await rendezvous.GetProducer();
        }
    }
}
