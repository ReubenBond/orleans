namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IFaultableConsumerGrain")]
    public interface IFaultableConsumerGrain : IGrainWithGuidKey
    {
        [Alias("BecomeConsumer")]
        Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse);

        [Alias("SetFailPeriod")]
        Task SetFailPeriod(TimeSpan failPeriod);

        [Alias("StopConsuming")]
        Task StopConsuming();

        [Alias("GetNumberConsumed")]
        Task<int> GetNumberConsumed();

        [Alias("GetNumberFailed")]
        Task<int> GetNumberFailed();

        [Alias("GetErrorCount")]
        Task<int> GetErrorCount();
    }
}
