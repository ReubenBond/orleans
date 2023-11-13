namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// Stream consumer grain that just counts the events it consumes
    /// </summary>
    [Alias("UnitTests.GrainInterfaces.IConsumerEventCountingGrain")]
    public interface IConsumerEventCountingGrain : IGrainWithGuidKey
    {
        [Alias("BecomeConsumer")]
        Task BecomeConsumer(Guid streamId, string providerToUse);

        [Alias("StopConsuming")]
        Task StopConsuming();

        [Alias("GetNumberConsumed")]
        Task<int> GetNumberConsumed();
    }
}