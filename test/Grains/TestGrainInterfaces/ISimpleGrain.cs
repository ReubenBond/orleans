// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface ISimpleGrain : IGrainWithIntegerKey
    {
        Task SetA(int a);
        Task SetB(int b);
        Task IncrementA();
        Task<int> GetAxB();
        Task<int> GetAxB(int a, int b);
        Task<int> GetA();
    }
}
