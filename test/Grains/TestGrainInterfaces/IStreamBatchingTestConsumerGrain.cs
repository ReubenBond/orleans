namespace UnitTests.GrainInterfaces
{
    public static class StreamBatchingTestConst
    {
        public const string ProviderName = "StreamBatchingTest";
        public const string BatchingNameSpace = "batching";
        public const string NonBatchingNameSpace = "nonbatching";
    }

    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.ConsumptionReport")]
    public class ConsumptionReport
    {
        [Id(0)]
        public int Consumed { get; set; }

        [Id(1)]
        public int MaxBatchSize { get; set; }
    }

    [Alias("UnitTests.GrainInterfaces.IStreamBatchingTestConsumerGrain")]
    public interface IStreamBatchingTestConsumerGrain : IGrainWithGuidKey
    {
        [Alias("GetConsumptionReport")]
        Task<ConsumptionReport> GetConsumptionReport();
    }
}
