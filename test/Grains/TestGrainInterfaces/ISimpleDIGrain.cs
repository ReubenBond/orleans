// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface ISimpleDIGrain : IGrainWithIntegerKey
    {
        Task<long> GetLongValue();
        Task<string> GetStringValue();
        Task DoDeactivate();
    }

    public interface IDIGrainWithInjectedServices : ISimpleDIGrain
    {
        Task<int> GetGrainFactoryId();
        Task<string> GetInjectedSingletonServiceValue();
        Task<string> GetInjectedScopedServiceValue();
        Task AssertCanResolveSameServiceInstances();
    }
}
