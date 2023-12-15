namespace Orleans.Runtime;

internal interface IMessageReceiver
{
    void ReceiveMessage(Message message, IMessageTargetCache cache);
}
