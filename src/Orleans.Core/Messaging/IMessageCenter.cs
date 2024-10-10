namespace Orleans.Runtime
{
    internal interface IMessageCenter
    {
        void SendMessage(Message msg, IMessageReceiverCache receiverHint);

        void DispatchLocalMessage(Message message);
    }
}
