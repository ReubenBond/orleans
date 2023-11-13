using Orleans.CodeGeneration;

namespace TestVersionGrainInterfaces
{
#if VERSION_1
    [Version(1)]
    [Alias("TestVersionGrainInterfaces.IVersionUpgradeTestGrain")]
#else
    [Version(2)]
    [Alias("TestVersionGrainInterfaces.IVersionUpgradeTestGrain")]
#endif
    public interface IVersionUpgradeTestGrain : IGrainWithIntegerKey
    {
        [Alias("GetVersion")]
        Task<int> GetVersion();

        [Alias("ProxyGetVersion")]
        Task<int> ProxyGetVersion(IVersionUpgradeTestGrain other);

        [Alias("LongRunningTask")]
        Task<bool> LongRunningTask(TimeSpan taskTime);
    }

#if VERSION_1
    [Version(1)]
    [Alias("TestVersionGrainInterfaces.IVersionPlacementTestGrain")]
#else
    [Version(2)]
    [Alias("TestVersionGrainInterfaces.IVersionPlacementTestGrain")]
#endif
    public interface IVersionPlacementTestGrain : IGrainWithIntegerKey
    {
        [Alias("GetVersion")]
        Task<int> GetVersion();
    }
}
