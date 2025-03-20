// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    internal interface IPubSubRendezvousGrain : IGrainWithStringKey
    {
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId streamId, GrainId streamProducer);

        Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer);

        Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string filterData);

        Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId);

        Task<int> ProducerCount(QualifiedStreamId streamId);

        Task<int> ConsumerCount(QualifiedStreamId streamId);

        Task<PubSubSubscriptionState[]> DiagGetConsumers(QualifiedStreamId streamId);

        Task Validate();

        Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer = default);

        Task FaultSubscription(GuidId subscriptionId);
    }
}
