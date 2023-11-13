namespace UnitTests.GrainInterfaces.Directories
{
    [Alias("UnitTests.GrainInterfaces.Directories.ICommonDirectoryGrain")]
    public interface ICommonDirectoryGrain : IGrainWithGuidKey
    {
        [Alias("Ping")]
        Task<int> Ping();

        [Alias("Reset")]
        Task Reset();

        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();

        [Alias("ProxyPing")]
        Task<int> ProxyPing(ICommonDirectoryGrain grain);
    }
}
