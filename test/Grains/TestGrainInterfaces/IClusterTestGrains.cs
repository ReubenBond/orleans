// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TestGrainInterfaces
{
    public interface IClusterTestGrain : IGrainWithIntegerKey
    {
        Task<int> SayHelloAsync();
        Task Deactivate();
        Task<string> GetRuntimeId();
        Task Subscribe(IClusterTestListener listener);
        Task EnableStreamNotifications();
    }

    public interface IClusterTestListener : IGrainObserver
    {
        void GotHello(int number);
    }
}
