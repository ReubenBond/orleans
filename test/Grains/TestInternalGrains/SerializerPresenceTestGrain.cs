// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class SerializerPresenceTestGrain : Grain, ISerializerPresenceTest
    {
        public Task<bool> SerializerExistsForType(Type t)
        {
            return Task.FromResult(this.ServiceProvider.GetRequiredService<Serializer>().CanSerialize(t));
        }

        public Task TakeSerializedData(object data)
        {
            // nothing to do
            return Task.CompletedTask;
        }
    }
}
