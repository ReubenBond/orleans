// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface ISimpleObserverableGrain : ISimpleGrain
    {
        Task Subscribe(ISimpleGrainObserver observer);
        Task Unsubscribe(ISimpleGrainObserver observer);
        Task<string> GetRuntimeInstanceId();
    }

    public interface ISimpleGrainObserver : IGrainObserver
    {
        void StateChanged(int a, int b);
    }
}
