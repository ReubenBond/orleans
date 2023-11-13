namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ISlowConsumingGrain")]
    public interface ISlowConsumingGrain : IGrainWithGuidKey
    {
        [Alias("BecomeConsumer")]
        Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse);

        [Alias("StopConsuming")]
        Task StopConsuming();

        [Alias("GetNumberConsumed")]
        Task<int> GetNumberConsumed();
    }
}
