namespace BenchmarkGrainInterfaces.GrainStorage
{
    [GenerateSerializer]
    [Alias("BenchmarkGrainInterfaces.GrainStorage.Report")]
    public class Report
    {
        [Id(1)]
        public bool Success { get; set; }

        [Id(2)]
        public TimeSpan Elapsed { get; set; }
    }

    [Alias("BenchmarkGrainInterfaces.GrainStorage.IPersistentGrain")]
    public interface IPersistentGrain : IGrainWithGuidKey
    {
        [Alias("Init")]
        Task Init(int payloadSize);
        [Alias("TrySet")]
        Task<Report> TrySet(int index);
    }
}
