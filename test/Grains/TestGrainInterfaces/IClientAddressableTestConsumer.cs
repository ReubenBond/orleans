namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IClientAddressableTestConsumer")]
    public interface IClientAddressableTestConsumer : IGrainWithIntegerKey
    {
        [Alias("PollProducer")]
        Task<int> PollProducer();
        [Alias("Setup")]
        Task Setup();
    }
}
