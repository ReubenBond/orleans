namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IStatelessWorkerStreamConsumerGrain")]
    public interface IStatelessWorkerStreamConsumerGrain : IGrainWithIntegerKey
    {
        [Alias("BecomeConsumer")]
        Task BecomeConsumer(Guid streamId, string providerToUse);
    }
}
