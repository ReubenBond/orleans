//#define USE_GENERICS

using Orleans.Runtime;
using Orleans.Streams;

namespace UnitTests.GrainInterfaces
{
#if USE_GENERICS
    public interface IStreamReliabilityTestGrain<in T> : IGrainWithIntegerKey
#else
    [Alias("UnitTests.GrainInterfaces.IStreamReliabilityTestGrain")]
    public interface IStreamReliabilityTestGrain : IGrainWithIntegerKey
#endif
    {
        [Alias("GetReceivedCount")]
        Task<int> GetReceivedCount();
        [Alias("GetErrorsCount")]
        Task<int> GetErrorsCount();
        [Alias("GetConsumerCount")]
        Task<int> GetConsumerCount();

        [Alias("Ping")]
        Task Ping();
#if USE_GENERICS
        Task<StreamSubscriptionHandle<T>> AddConsumer(Guid streamId, string providerName);
        Task RemoveConsumer(Guid streamId, string providerName, StreamSubscriptionHandle<T> consumerHandle);
#else
        [Alias("AddConsumer")]
        Task<StreamSubscriptionHandle<int>> AddConsumer(Guid streamId, string providerName);
        [Alias("RemoveConsumer")]
        Task RemoveConsumer(Guid streamId, string providerName, StreamSubscriptionHandle<int> consumerHandle);
#endif

        [Alias("BecomeProducer")]
        Task BecomeProducer(Guid streamId, string providerName);
        [Alias("RemoveProducer")]
        Task RemoveProducer(Guid streamId, string providerName);
        [Alias("ClearGrain")]
        Task ClearGrain();
        [Alias("RemoveAllConsumers")]
        Task RemoveAllConsumers();

        [Alias("IsConsumer")]
        Task<bool> IsConsumer();
        [Alias("IsProducer")]
        Task<bool> IsProducer();
        [Alias("GetConsumerHandlesCount")]
        Task<int> GetConsumerHandlesCount();
        [Alias("GetConsumerObserversCount")]
        Task<int> GetConsumerObserversCount();

#if USE_GENERICS
        Task SendItem(T item);
#else
        [Alias("SendItem")]
        Task SendItem(int item);
#endif

        [Alias("GetLocation")]
        Task<SiloAddress> GetLocation();
    }

    [Alias("UnitTests.GrainInterfaces.IStreamUnsubscribeTestGrain")]
    public interface IStreamUnsubscribeTestGrain : IGrainWithIntegerKey
    {
        [Alias("Subscribe")]
        Task Subscribe(Guid streamId, string providerName);
        [Alias("UnSubscribeFromAllStreams")]
        Task UnSubscribeFromAllStreams();
    }
}