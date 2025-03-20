// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface ISimpleGenericGrain<T> : IGrainWithIntegerKey
    {
        Task Set(T t);

        Task Transform();

        Task<T> Get();

        Task CompareGrainReferences(ISimpleGenericGrain<T> clientRef);
    }
}
