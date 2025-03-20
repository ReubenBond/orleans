// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface IActivateDeactivateWatcherGrain : IGrainWithIntegerKey
    {
        Task<string[]> GetActivateCalls();
        Task<string[]> GetDeactivateCalls();

        Task Clear();

        Task RecordActivateCall(string activation);
        Task RecordDeactivateCall(string activation);
    }
}
