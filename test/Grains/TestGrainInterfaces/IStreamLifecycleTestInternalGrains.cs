namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IStreamLifecycleProducerInternalGrain")]
    public interface IStreamLifecycleProducerInternalGrain : IStreamLifecycleProducerGrain
    {
        [Alias("DoBadDeactivateNoClose")]
        Task DoBadDeactivateNoClose();
        [Alias("TestInternalRemoveProducer")]
        Task TestInternalRemoveProducer(Guid streamId, string providerName);
    }

    [Alias("UnitTests.GrainInterfaces.IStreamLifecycleConsumerInternalGrain")]
    public interface IStreamLifecycleConsumerInternalGrain : IStreamLifecycleConsumerGrain
    {
        [Alias("TestBecomeConsumerSlim")]
        Task TestBecomeConsumerSlim(Guid streamId, string providerName);
    }
}