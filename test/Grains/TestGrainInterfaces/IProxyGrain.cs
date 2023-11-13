namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IProxyGrain")]
    public interface IProxyGrain : IGrainWithIntegerKey
    {
        [Alias("CreateProxy")]
        Task CreateProxy(long key);

        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();

        [Alias("GetProxyRuntimeInstanceId")]
        Task<string> GetProxyRuntimeInstanceId();
    }
}
