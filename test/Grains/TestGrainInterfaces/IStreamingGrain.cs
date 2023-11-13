using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IStreaming_ConsumerGrain")]
    public interface IStreaming_ConsumerGrain : IGrainWithGuidKey
    {
        [Alias("BecomeConsumer")]
        Task BecomeConsumer(Guid streamId, string providerToUse, string streamNamespace);
        [Alias("StopBeingConsumer")]
        Task StopBeingConsumer();
        [Alias("GetItemsConsumed")]
        Task<int> GetItemsConsumed();
        [Alias("GetConsumerCount")]
        Task<int> GetConsumerCount();
        [Alias("DeactivateConsumerOnIdle")]
        Task DeactivateConsumerOnIdle();
    }

    [Alias("UnitTests.GrainInterfaces.IPersistentStreaming_ProducerGrain")]
    public interface IPersistentStreaming_ProducerGrain : IStreaming_ProducerGrain
    {
    }

    [Alias("UnitTests.GrainInterfaces.IPersistentStreaming_ConsumerGrain")]
    public interface IPersistentStreaming_ConsumerGrain : IStreaming_ConsumerGrain
    {
    }

    [Alias("UnitTests.GrainInterfaces.IStreaming_ProducerConsumerGrain")]
    public interface IStreaming_ProducerConsumerGrain : IGrainWithIntegerKey, IStreaming_ProducerGrain, IStreaming_ConsumerGrain
    {
    }

    [Alias("UnitTests.GrainInterfaces.IStreaming_Reentrant_ProducerConsumerGrain")]
    public interface IStreaming_Reentrant_ProducerConsumerGrain : IGrainWithIntegerKey, IStreaming_ProducerGrain, IStreaming_ConsumerGrain
    {
    }

    [Alias("UnitTests.GrainInterfaces.IStreaming_ImplicitlySubscribedConsumerGrain")]
    public interface IStreaming_ImplicitlySubscribedConsumerGrain : IGrainWithIntegerKey, IStreaming_ConsumerGrain
    {
    }

    [Alias("UnitTests.GrainInterfaces.IStreaming_ImplicitlySubscribedGenericConsumerGrain`1")]
    public interface IStreaming_ImplicitlySubscribedGenericConsumerGrain<T> : IGrainWithIntegerKey, IStreaming_ConsumerGrain
    {
    }


    //------- STATE interfaces ----//

    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.Streaming_ProducerGrain_State")]
    public class Streaming_ProducerGrain_State
    {
        [Id(0)]
        public List<IProducerObserver> Producers { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.Streaming_ConsumerGrain_State")]
    public class Streaming_ConsumerGrain_State
    {
        [Id(0)]
        public List<IConsumerObserver> Consumers { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.Streaming_ProducerConsumerGrain_State")]
    public class Streaming_ProducerConsumerGrain_State
    {
        [Id(0)]
        public List<IProducerObserver> Producers { get; set; }
        [Id(1)]
        public List<IConsumerObserver> Consumers { get; set; }
    }

    //------- POCO interfaces for objects that implement the actual test logic ----///

    public interface IProducerObserver
    {
        void BecomeProducer(Guid streamId, IStreamProvider streamProvider, string streamNamespace);
        void RenewProducer(ILogger logger, IStreamProvider streamProvider);
        Task StopBeingProducer();
        Task ProduceSequentialSeries(int count);
        Task ProduceParallelSeries(int count);
        Task ProducePeriodicSeries(Func<Func<object, Task>, IDisposable> createTimerFunc, int count);
        Task<int> ExpectedItemsProduced { get; }
        Task<int> ItemsProduced { get; }
        Task AddNewConsumerGrain(Guid consumerGrainId);
        Task<int> ProducerCount { get; }
        Task VerifyFinished();
        string ProviderName { get; }
    }

    public interface IConsumerObserver
    {
        Task BecomeConsumer(Guid streamId, IStreamProvider streamProvider, string streamNamespace);
        Task RenewConsumer(ILogger logger, IStreamProvider streamProvider);
        Task StopBeingConsumer(IStreamProvider streamProvider);
        Task<int> ItemsConsumed { get; }
        Task<int> ConsumerCount { get; }
        string ProviderName { get; }
    }    
}