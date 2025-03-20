// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface IStuckGrain : IGrainWithGuidKey
    {
        Task RunForever();

        Task NonBlockingCall();

        Task<int> GetNonBlockingCallCounter();

        Task<bool> DidActivationTryToStart(GrainId id);

        Task BlockingDeactivation();
    }

    public interface IStuckCleanGrain : IGrainWithGuidKey
    {
        Task Release(Guid key);

        Task<bool> IsActivated(Guid key);
    }
}
