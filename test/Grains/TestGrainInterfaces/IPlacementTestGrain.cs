using System.Net;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IPlacementTestGrain")]
    public interface IPlacementTestGrain : IGrainWithGuidKey
    {
        [Alias("GetEndpoint")]
        Task<IPEndPoint> GetEndpoint();
        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();
        [Alias("GetActivationId")]
        Task<string> GetActivationId();
        [Alias("StartLocalGrains")]
        Task StartLocalGrains(List<Guid> keys);
        [Alias("StartPreferLocalGrain")]
        Task<Guid> StartPreferLocalGrain(Guid key);
        [Alias("SampleLocalGrainEndpoint")]
        Task<List<IPEndPoint>> SampleLocalGrainEndpoint(Guid key, int sampleSize);
        [Alias("Nop")]
        Task Nop();
        [Alias("EnableOverloadDetection")]
        Task EnableOverloadDetection(bool enabled);
        [Alias("LatchOverloaded")]
        Task LatchOverloaded();
        [Alias("UnlatchOverloaded")]
        Task UnlatchOverloaded();
        [Alias("LatchCpuUsage")]
        Task LatchCpuUsage(float value);
        [Alias("UnlatchCpuUsage")]
        Task UnlatchCpuUsage();
        [Alias("GetLocation")]
        Task<SiloAddress> GetLocation();
    }

    [Alias("UnitTests.GrainInterfaces.IActivationCountBasedPlacementTestGrain")]
    public interface IActivationCountBasedPlacementTestGrain : IPlacementTestGrain
    { }

    [Alias("UnitTests.GrainInterfaces.IRandomPlacementTestGrain")]
    public interface IRandomPlacementTestGrain : IPlacementTestGrain
    { }

    [Alias("UnitTests.GrainInterfaces.IPreferLocalPlacementTestGrain")]
    public interface IPreferLocalPlacementTestGrain : IPlacementTestGrain
    { }

    [Alias("UnitTests.GrainInterfaces.ILocalPlacementTestGrain")]
    public interface ILocalPlacementTestGrain : IPlacementTestGrain
    { }

    internal interface IDefaultPlacementTestGrain
    {
        bool IsDefaultPlacementRandom();
    }

    //----------------------------------------------------------//
    // Interfaces for LocalContent grain case, when grain is activated on every silo by bootstrap provider.

    [Alias("UnitTests.GrainInterfaces.ILocalContentGrain")]
    public interface ILocalContentGrain : IGrainWithGuidKey
    {
        [Alias("Init")]
        Task Init();                            // a dummy call to just activate this grain.
        [Alias("GetContent")]
        Task<object> GetContent();
    }

    [Alias("UnitTests.GrainInterfaces.ITestContentGrain")]
    public interface ITestContentGrain : IGrainWithIntegerKey
    {
        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();    // just for test
        [Alias("FetchContentFromLocalGrain")]
        Task<object> FetchContentFromLocalGrain();
    }

}
