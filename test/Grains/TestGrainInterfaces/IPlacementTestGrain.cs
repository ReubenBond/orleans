// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface IPlacementTestGrain : IGrainWithGuidKey
    {
        Task<IPEndPoint> GetEndpoint();
        Task<string> GetRuntimeInstanceId();
        Task<string> GetActivationId();
        Task StartLocalGrains(List<Guid> keys);
        Task<Guid> StartPreferLocalGrain(Guid key);
        Task<List<IPEndPoint>> SampleLocalGrainEndpoint(Guid key, int sampleSize);
        Task Nop();
        Task EnableOverloadDetection(bool enabled);
        Task LatchOverloaded();
        Task UnlatchOverloaded();
        Task LatchCpuUsage(float value);
        Task UnlatchCpuUsage();
        Task<SiloAddress> GetLocation();
    }

    public interface IActivationCountBasedPlacementTestGrain : IPlacementTestGrain
    { }

    public interface IRandomPlacementTestGrain : IPlacementTestGrain
    { }

    public interface IPreferLocalPlacementTestGrain : IPlacementTestGrain
    { }

    public interface IStatelessWorkerPlacementTestGrain : IPlacementTestGrain
    {
        ValueTask<int> GetWorkerLimit();
    }
    
    public interface IOtherStatelessWorkerPlacementTestGrain : IStatelessWorkerPlacementTestGrain
    {
    }

    internal interface IDefaultPlacementTestGrain
    {
        bool IsDefaultPlacementRandom();
    }

    //----------------------------------------------------------//
    // Interfaces for LocalContent grain case, when grain is activated on every silo by bootstrap provider.

    public interface ILocalContentGrain : IGrainWithGuidKey
    {
        Task Init();                            // a dummy call to just activate this grain.
        Task<object> GetContent();
    }

    public interface ITestContentGrain : IGrainWithIntegerKey
    {
        Task<string> GetRuntimeInstanceId();    // just for test
        Task<object> FetchContentFromLocalGrain();
    }

}
