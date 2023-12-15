namespace Orleans.Runtime
{
    public interface IMessageTargetCache
    {
        object MessageReceiver { get; set; }
    }
}
