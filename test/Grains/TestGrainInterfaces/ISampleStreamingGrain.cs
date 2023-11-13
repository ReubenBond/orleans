namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ISampleStreaming_ProducerGrain")]
    public interface ISampleStreaming_ProducerGrain : IGrainWithGuidKey
    {
        [Alias("BecomeProducer")]
        Task BecomeProducer(Guid streamId, string streamNamespace, string providerToUse);

        [Alias("StartPeriodicProducing")]
        Task StartPeriodicProducing();

        [Alias("StopPeriodicProducing")]
        Task StopPeriodicProducing();

        [Alias("GetNumberProduced")]
        Task<int> GetNumberProduced();

        [Alias("ClearNumberProduced")]
        Task ClearNumberProduced();
        [Alias("Produce")]
        Task Produce();
    }

    [Alias("UnitTests.GrainInterfaces.ISampleStreaming_ConsumerGrain")]
    public interface ISampleStreaming_ConsumerGrain : IGrainWithGuidKey
    {
        [Alias("BecomeConsumer")]
        Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse);

        [Alias("StopConsuming")]
        Task StopConsuming();

        [Alias("GetNumberConsumed")]
        Task<int> GetNumberConsumed();
    }

    [Alias("UnitTests.GrainInterfaces.ISampleStreaming_InlineConsumerGrain")]
    public interface ISampleStreaming_InlineConsumerGrain : ISampleStreaming_ConsumerGrain
    {
    }

    [Alias("UnitTests.GrainInterfaces.IGrainWithGenericMethodsValue")]
    public interface IGrainWithGenericMethodsValue : IGrainWithGuidKey
    {
        [Alias("ValueTaskMethod")]
        ValueTask<int> ValueTaskMethod(bool useCache);
    }
}
