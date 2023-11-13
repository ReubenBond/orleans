namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// Consumer grain which passively reacts to subscriptions which was made on behalf of
    /// it using Programmatic Subscribing 
    /// </summary>
    [Alias("UnitTests.GrainInterfaces.IPassive_ConsumerGrain")]
    public interface IPassive_ConsumerGrain: IGrainWithGuidKey
    {
        [Alias("StopConsuming")]
        Task StopConsuming();
        [Alias("GetCountOfOnAddFuncCalled")]
        Task<int> GetCountOfOnAddFuncCalled();
        [Alias("GetNumberConsumed")]
        Task<int> GetNumberConsumed();
    }

    //the consumer grain marker interface which would unsubscribe on any subscription added by StreamSubscriptionManager
    [Alias("UnitTests.GrainInterfaces.IJerk_ConsumerGrain")]
    public interface IJerk_ConsumerGrain : IGrainWithGuidKey
    {
    }

    [Alias("UnitTests.GrainInterfaces.IImplicitSubscribeGrain")]
    public interface IImplicitSubscribeGrain: IPassive_ConsumerGrain
    {
    }

    [Alias("UnitTests.GrainInterfaces.ITypedProducerGrain")]
    public interface ITypedProducerGrain: IGrainWithGuidKey
    {
        [Alias("BecomeProducer")]
        Task BecomeProducer(Guid streamId, string streamNamespace, string providerToUse);

        [Alias("StartPeriodicProducing")]
        Task StartPeriodicProducing(TimeSpan? firePeriod = null);

        [Alias("StopPeriodicProducing")]
        Task StopPeriodicProducing();

        [Alias("GetNumberProduced")]
        Task<int> GetNumberProduced();

        [Alias("ClearNumberProduced")]
        Task ClearNumberProduced();
        [Alias("Produce")]
        Task Produce();
    }

    [Alias("UnitTests.GrainInterfaces.ITypedProducerGrainProducingInt")]
    public interface ITypedProducerGrainProducingInt : ITypedProducerGrain
    { }

    [Alias("UnitTests.GrainInterfaces.ITypedProducerGrainProducingApple")]
    public interface ITypedProducerGrainProducingApple : ITypedProducerGrain
    { }

    public interface IFruit
    {
        int GetNumber();
    }
}
