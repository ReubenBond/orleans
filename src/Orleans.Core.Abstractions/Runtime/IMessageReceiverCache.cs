namespace Orleans.Runtime;

internal interface IMessageReceiverCache
{
    object? MessageReceiver { get; }

    bool CompareExchangeMessageReceiver(object? value, object? comparand);
}

internal interface IMessageDestinationCache : IMessageReceiverCache
{
    SiloAddress? TargetSilo { get; }

    bool CompareExchangeTargetSilo(SiloAddress? value, SiloAddress? comparand);
}
