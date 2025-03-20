// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface IMultifacetFactoryTestGrain : IGrainWithIntegerKey
    {
        Task<IMultifacetReader> GetReader(IMultifacetTestGrain grain);
        Task<IMultifacetReader> GetReader();
        Task<IMultifacetWriter> GetWriter(IMultifacetTestGrain grain);
        Task<IMultifacetWriter> GetWriter();
        Task SetReader(IMultifacetReader reader);
        Task SetWriter(IMultifacetWriter writer);
    }
}