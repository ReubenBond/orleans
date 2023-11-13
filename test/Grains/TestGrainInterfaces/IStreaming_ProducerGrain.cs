using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces
{
    //------- GRAIN interfaces ----//
    [Alias("UnitTests.GrainInterfaces.IStreaming_ProducerGrain")]
    public interface IStreaming_ProducerGrain : IGrainWithGuidKey
    {
        [Alias("BecomeProducer")]
        Task BecomeProducer(Guid streamId, string providerToUse, string streamNamespace);
        [Alias("StopBeingProducer")]
        Task StopBeingProducer();
        [Alias("ProduceSequentialSeries")]
        Task ProduceSequentialSeries(int count);
        [Alias("ProduceParallelSeries")]
        Task ProduceParallelSeries(int count);
        [Alias("ProducePeriodicSeries")]
        Task ProducePeriodicSeries(int count);
        [Alias("GetExpectedItemsProduced")]
        Task<int> GetExpectedItemsProduced();
        [Alias("GetItemsProduced")]
        Task<int> GetItemsProduced();
        [Alias("AddNewConsumerGrain")]
        Task AddNewConsumerGrain(Guid consumerGrainId);
        [Alias("GetProducerCount")]
        Task<int> GetProducerCount();
        [Alias("DeactivateProducerOnIdle")]
        Task DeactivateProducerOnIdle();

        [AlwaysInterleave]
        [Alias("VerifyFinished")]
        Task VerifyFinished();
    }
}