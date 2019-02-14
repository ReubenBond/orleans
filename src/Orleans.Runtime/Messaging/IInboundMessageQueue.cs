using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Orleans.Runtime.Messaging
{
    internal interface IInboundMessageQueue : IDisposable
    {
        int Count { get; }

        void Stop();

        ChannelReader<Message> GetReader(Message.Categories type);

        void PostMessage(Message message);
    }
}
