namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IClientAddressableTestRendezvousGrain")]
    public interface IClientAddressableTestRendezvousGrain : IGrainWithIntegerKey
    {
        [Alias("GetProducer")]
        Task<IClientAddressableTestProducer> GetProducer();
        [Alias("SetProducer")]
        Task SetProducer(IClientAddressableTestProducer producer);
    }
}
