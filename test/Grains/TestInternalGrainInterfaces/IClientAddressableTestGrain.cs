// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface IClientAddressableTestGrain : IGrainWithIntegerKey
    {
        Task SetTarget(IClientAddressableTestClientObject target);
        Task<string> HappyPath(string message);
        Task SadPath(string message);
        Task MicroSerialStressTest(int iterationCount);
        Task MicroParallelStressTest(int iterationCount);
    }
}
