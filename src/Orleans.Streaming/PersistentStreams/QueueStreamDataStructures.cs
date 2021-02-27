using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Streams
{
    [Serializable]
    internal enum StreamConsumerDataState
    {
        Active, // Indicates that events are activly being delivered to this consumer.
        Inactive, // Indicates that events are not activly being delivered to this consumers.  If adapter produces any events on this consumers stream, the agent will need begin delivering events
    }

    [Serializable]
    [Hagar.GenerateSerializer]
    internal class StreamConsumerData
    {
        [Hagar.Id(1)]
        public GuidId SubscriptionId;
        [Hagar.Id(2)]
        public InternalStreamId StreamId;
        [Hagar.Id(3)]
        public IStreamConsumerExtension StreamConsumer;
        [Hagar.Id(4)]
        public StreamConsumerDataState State = StreamConsumerDataState.Inactive;
        [Hagar.Id(5)]
        public IQueueCacheCursor Cursor;
        [Hagar.Id(6)]
        public StreamHandshakeToken LastToken;
        [Hagar.Id(7)]
        public string FilterData;

        public StreamConsumerData(GuidId subscriptionId, InternalStreamId streamId, IStreamConsumerExtension streamConsumer, string filterData)
        {
            SubscriptionId = subscriptionId;
            StreamId = streamId;
            StreamConsumer = streamConsumer;
            FilterData = filterData;
        }

        internal void SafeDisposeCursor(ILogger logger)
        {
            try
            {
                if (Cursor != null)
                {
                    // kill cursor activity and ensure it does not start again on this consumer data.
                    Utils.SafeExecute(Cursor.Dispose, logger,
                        () => String.Format("Cursor.Dispose on stream {0}, StreamConsumer {1} has thrown exception.", StreamId, StreamConsumer));
                }
            }
            finally
            {
                Cursor = null;
            }
        }
    }
}
