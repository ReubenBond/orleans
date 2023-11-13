namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IClientAddressableTestProducer")]
    public interface IClientAddressableTestProducer : IGrainObserver
    {
        [Alias("Poll")]
        Task<int> Poll();
    }
}
