// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface IImplicitSubscriptionCounterGrain : IGrainWithGuidKey
    {
        Task<int> GetEventCounter();

        Task<int> GetErrorCounter();

        Task Deactivate();

        Task DeactivateOnEvent(bool deactivate);
    }

    public interface IFastImplicitSubscriptionCounterGrain : IImplicitSubscriptionCounterGrain
    { }

    public interface ISlowImplicitSubscriptionCounterGrain : IImplicitSubscriptionCounterGrain
    { }
}