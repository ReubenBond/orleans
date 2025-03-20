// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface IBase : IGrainWithIntegerKey
    {
        Task<bool> Foo();
    }

    public interface IDerivedFromBase : IBase
    {
        Task<bool> Bar();
    }

    public interface IBase1 : IGrainWithIntegerKey
    {
        Task<bool> Foo();
    }

    public interface IBase2 : IGrainWithIntegerKey
    {
        Task<bool> Bar();
    }

    public interface IBase3 : IGrainWithIntegerKey
    {
        Task<bool> Foo();
    }

    public interface IBase4 : IGrainWithIntegerKey
    {
        Task<bool> Foo();
    }

    public interface IStringGrain : IGrainWithStringKey
    {
        Task<bool> Foo();
    }

    public interface IGuidGrain : IGrainWithGuidKey
    {
        Task<bool> Foo();
    }
}
