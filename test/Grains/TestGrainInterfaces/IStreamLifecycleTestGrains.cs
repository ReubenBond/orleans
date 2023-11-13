using Orleans.Runtime;
using Orleans.Streams;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IStreamLifecycleConsumerGrain")]
    public interface IStreamLifecycleConsumerGrain : IGrainWithGuidKey
    {
        [Alias("GetReceivedCount")]
        Task<int> GetReceivedCount();
        [Alias("GetErrorsCount")]
        Task<int> GetErrorsCount();

        [Alias("Ping")]
        Task Ping();
        [Alias("BecomeConsumer")]
        Task BecomeConsumer(StreamId streamId, string providerName);
        [Alias("TestBecomeConsumerSlim")]
        Task TestBecomeConsumerSlim(StreamId streamId, string providerName);
        [Alias("RemoveConsumer")]
        Task RemoveConsumer(StreamId streamId, string providerName, StreamSubscriptionHandle<int> consumerHandle);
        [Alias("ClearGrain")]
        Task ClearGrain();
    }

    [Alias("UnitTests.GrainInterfaces.IFilteredStreamConsumerGrain")]
    public interface IFilteredStreamConsumerGrain : IStreamLifecycleConsumerGrain
    {
        [Alias("BecomeConsumer")]
        Task BecomeConsumer(StreamId streamId, string providerName, bool sendEvensOnly);
        [Alias("SubscribeWithBadFunc")]
        Task SubscribeWithBadFunc(StreamId streamId, string providerName);
    }

    [Alias("UnitTests.GrainInterfaces.IStreamLifecycleProducerGrain")]
    public interface IStreamLifecycleProducerGrain : IGrainWithGuidKey
    {
        [Alias("GetSendCount")]
        Task<int> GetSendCount();
        [Alias("GetErrorsCount")]
        Task<int> GetErrorsCount();

        [Alias("Ping")]
        Task Ping();

        [Alias("BecomeProducer")]
        Task BecomeProducer(StreamId streamId, string providerName);
        [Alias("ClearGrain")]
        Task ClearGrain();

        [Alias("DoDeactivateNoClose")]
        Task DoDeactivateNoClose();

        [Alias("SendItem")]
        Task SendItem(int item);
    }

    public static class StreamLifecycleConsumerGrainExtensions
    {
        public static Task BecomeConsumer(this IStreamLifecycleConsumerGrain grain, Guid streamIdGuid, string streamNamespace, string providerName)
        {
            var streamId = StreamId.Create(streamNamespace, streamIdGuid);
            return grain.BecomeConsumer(streamId, providerName);
        }

        public static Task TestBecomeConsumerSlim(this IStreamLifecycleConsumerGrain grain, Guid streamIdGuid, string streamNamespace, string providerName)
        {
            var streamId = StreamId.Create(streamNamespace, streamIdGuid);
            return grain.TestBecomeConsumerSlim(streamId, providerName);
        }

        public static  Task RemoveConsumer(this IStreamLifecycleConsumerGrain grain, Guid streamIdGuid, string streamNamespace, string providerName, StreamSubscriptionHandle<int> consumerHandle)
        {
            var streamId = StreamId.Create(streamNamespace, streamIdGuid);
            return grain.RemoveConsumer(streamId, providerName, consumerHandle);
        }

        public static Task BecomeConsumer(this IFilteredStreamConsumerGrain grain, Guid streamIdGuid, string streamNamespace, string providerName, bool sendEvensOnly)
        {
            var streamId = StreamId.Create(streamNamespace, streamIdGuid);
            return grain.BecomeConsumer(streamId, providerName, sendEvensOnly);
        }

        public static Task SubscribeWithBadFunc(this IFilteredStreamConsumerGrain grain, Guid streamIdGuid, string streamNamespace, string providerName)
        {
            var streamId = StreamId.Create(streamNamespace, streamIdGuid);
            return grain.SubscribeWithBadFunc(streamId, providerName);
        }

        public static Task BecomeProducer(this IStreamLifecycleProducerGrain grain, Guid streamIdGuid, string streamNamespace, string providerName)
        {
            var streamId = StreamId.Create(streamNamespace, streamIdGuid);
            return grain.BecomeProducer(streamId, providerName);
        }
    }
}
