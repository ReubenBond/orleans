using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    [Alias("Orleans.Streams.IPubSubRendezvousGrain")]
    internal interface IPubSubRendezvousGrain : IGrainWithStringKey
    {
        [Alias("RegisterProducer")]
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId streamId, GrainId streamProducer);

        [Alias("UnregisterProducer")]
        Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer);

        [Alias("RegisterConsumer")]
        Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string filterData);

        [Alias("UnregisterConsumer")]
        Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId);

        [Alias("ProducerCount")]
        Task<int> ProducerCount(QualifiedStreamId streamId);

        [Alias("ConsumerCount")]
        Task<int> ConsumerCount(QualifiedStreamId streamId);

        [Alias("DiagGetConsumers")]
        Task<PubSubSubscriptionState[]> DiagGetConsumers(QualifiedStreamId streamId);

        [Alias("Validate")]
        Task Validate();

        [Alias("GetAllSubscriptions")]
        Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer = default);

        [Alias("FaultSubscription")]
        Task FaultSubscription(GuidId subscriptionId);
    }
}
