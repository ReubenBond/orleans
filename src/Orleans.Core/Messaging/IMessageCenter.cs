using System;
using System.Threading.Channels;

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

        void OnReceivedMessage(Message message);

        void RegisterLocalMessageHandler(Message.Categories category, Action<Message> handler);

        /// <summary>
        /// Called immediately prior to transporting a message.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>Whether or not to continue transporting the message.</returns>
        bool PrepareMessageForSend(Message msg);

        void OnMessageSerializationFailure(Message msg, Exception exc);

        int SendQueueLength { get; }

        int ReceiveQueueLength { get; }
    }
}
