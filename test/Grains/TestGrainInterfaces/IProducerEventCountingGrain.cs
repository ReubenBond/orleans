namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// Stream producer grain that sends a single event at a time (when told, see SendEvent) and tracks the number of events sent
    /// </summary>
    [Alias("UnitTests.GrainInterfaces.IProducerEventCountingGrain")]
    public interface IProducerEventCountingGrain : IGrainWithGuidKey
    {
        [Alias("BecomeProducer")]
        Task BecomeProducer(Guid streamId, string providerToUse);

        /// <summary>
        /// Sends a single event and, upon successful completion, updates the number of events produced.
        /// </summary>
        /// <returns></returns>
        [Alias("SendEvent")]
        Task SendEvent();

        [Alias("GetNumberProduced")]
        Task<int> GetNumberProduced();
    }
}