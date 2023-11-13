using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces;

[Alias("IStatelessWorkerScalingGrain")]
public interface IStatelessWorkerScalingGrain : IGrainWithIntegerKey
{
    [Alias("Wait")]
    Task Wait();

    [AlwaysInterleave]
    [Alias("Release")]
    Task Release();

    [AlwaysInterleave]
    [Alias("GetActivationCount")]
    Task<int> GetActivationCount();

    [AlwaysInterleave]
    [Alias("GetWaitingCount")]
    Task<int> GetWaitingCount();
}
