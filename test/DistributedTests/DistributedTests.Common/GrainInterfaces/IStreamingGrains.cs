namespace DistributedTests.GrainInterfaces
{
    public static class StreamingConstants
    {
        public const string StreamingProvider = "TestStreamingProvider";
        public const string StreamingNamespace = "TestStreamingNamespace";

        public const string DefaultCounterGrain = "default";
    }

    public class ReportingOptions
    {
        public DateTime ReportAt { get; set; }

        public int Duration { get; set; }
    }

    [Alias("DistributedTests.GrainInterfaces.IGrainWithCounter")]
    public interface IGrainWithCounter : IGrainWithGuidKey
    {
        [Alias("GetCounterValue")]
        Task<int> GetCounterValue(string counterName);
    }

    [Alias("DistributedTests.GrainInterfaces.IImplicitSubscriberGrain")]
    public interface IImplicitSubscriberGrain : IGrainWithCounter
    {
    }

    [Alias("DistributedTests.GrainInterfaces.ICounterGrain")]
    public interface ICounterGrain : IGrainWithStringKey
    {
        [Alias("Track")]
        Task Track(IGrainWithCounter grain);

        [Alias("GetRunDuration")]
        Task<TimeSpan> GetRunDuration();

        [Alias("WaitTimeForReport")]
        Task<TimeSpan> WaitTimeForReport();

        [Alias("GetTotalCounterValue")]
        Task<int> GetTotalCounterValue(string counterName);
    }
}
