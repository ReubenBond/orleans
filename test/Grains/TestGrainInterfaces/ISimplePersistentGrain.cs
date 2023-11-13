namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ISimplePersistentGrain")]
    public interface ISimplePersistentGrain : ISimpleGrain
    {
        [Alias("SetA")]
        Task SetA(int a, bool deactivate);
        [Alias("GetVersion")]
        Task<Guid> GetVersion();
        [Alias("GetRequestContext")]
        Task<object> GetRequestContext();
        [Alias("SetRequestContext")]
        Task SetRequestContext(int data);
    }
}
