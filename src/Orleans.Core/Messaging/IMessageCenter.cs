namespace Orleans.Runtime
{
    internal interface IMessageCenter
    {
        void SendMessage(Message msg, GrainReference targetReference = null);

        void DispatchLocalMessage(Message message);
    }
}
