namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IStatelessWorkerStreamProducerGrain")]
    public interface IStatelessWorkerStreamProducerGrain : IGrainWithIntegerKey
    {
        [Alias("Produce")]
        Task Produce(Guid streamId, string providerToUse, string message);
    }
}
