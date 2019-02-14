using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal interface IMessageCenter
    {
        SiloAddress MyAddress { get; }

        void Start();

        void PrepareToStop();

        void Stop();

        ChannelReader<Message> GetReader(Message.Categories type);

        void SendMessage(Message msg);

        void RegisterLocalMessageHandler(Message.Categories category, Action<Message> handler);

        int SendQueueLength { get; }

        int ReceiveQueueLength { get; }
    }
}
