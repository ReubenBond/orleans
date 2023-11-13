using System;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams
{
    // This is the extension interface for stream consumers
    [Alias("Orleans.Streams.IStreamConsumerExtension")]
    internal interface IStreamConsumerExtension : IGrainExtension
    {
        [Alias("DeliverImmutable")]
        Task<StreamHandshakeToken> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, [Immutable] object item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken);
        [Alias("DeliverMutable")]
        Task<StreamHandshakeToken> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken);
        [Alias("DeliverBatch")]
        Task<StreamHandshakeToken> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, [Immutable] IBatchContainer item, StreamHandshakeToken handshakeToken);
        [Alias("CompleteStream")]
        Task CompleteStream(GuidId subscriptionId);
        [Alias("ErrorInStream")]
        Task ErrorInStream(GuidId subscriptionId, Exception exc);
        [Alias("GetSequenceToken")]
        Task<StreamHandshakeToken> GetSequenceToken(GuidId subscriptionId);
    }

    // This is the extension interface for stream producers
    [Alias("Orleans.Streams.IStreamProducerExtension")]
    internal interface IStreamProducerExtension : IGrainExtension
    {
        [AlwaysInterleave]
        [Alias("AddSubscriber")]
        Task AddSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string filterData);

        [AlwaysInterleave]
        [Alias("RemoveSubscriber")]
        Task RemoveSubscriber(GuidId subscriptionId, QualifiedStreamId streamId);
    }
}
