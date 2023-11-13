using Orleans.Streams;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IMultipleSubscriptionConsumerGrain")]
    public interface IMultipleSubscriptionConsumerGrain : IGrainWithGuidKey
    {
        [Alias("BecomeConsumer")]
        Task<StreamSubscriptionHandle<int>> BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse);

        [Alias("Resume")]
        Task<StreamSubscriptionHandle<int>> Resume(StreamSubscriptionHandle<int> handle);

        [Alias("StopConsuming")]
        Task StopConsuming(StreamSubscriptionHandle<int> handle);

        [Alias("GetAllSubscriptions")]
        Task<IList<StreamSubscriptionHandle<int>>> GetAllSubscriptions(Guid streamId, string streamNamespace, string providerToUse);

        [Alias("GetNumberConsumed")]
        Task<Dictionary<StreamSubscriptionHandle<int>, Tuple<int,int>>> GetNumberConsumed();

        [Alias("ClearNumberConsumed")]
        Task ClearNumberConsumed();

        [Alias("Deactivate")]
        Task Deactivate();
    }
}
