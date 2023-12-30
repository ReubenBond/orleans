namespace Orleans.Runtime
{
    internal interface IMessageCenter
    {
        void SendMessage(Message msg, IMessageTargetCache targetCache);

        void DispatchLocalMessage(Message message);
    }
}
