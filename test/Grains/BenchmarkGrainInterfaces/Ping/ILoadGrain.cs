namespace BenchmarkGrainInterfaces.Ping
{
    [GenerateSerializer]
    [Alias("BenchmarkGrainInterfaces.Ping.Report")]
    public class Report
    {
        [Id(1)]
        public long Succeeded { get; set; }
        [Id(2)]
        public long Failed { get; set; }
        [Id(3)]
        public TimeSpan Elapsed { get; set; }
    }

    [Alias("BenchmarkGrainInterfaces.Ping.ILoadGrain")]
    public interface ILoadGrain : IGrainWithGuidKey
    {
        [Alias("Generate")]
        Task Generate(int run, int conncurrent);
        [Alias("TryGetReport")]
        Task<Report> TryGetReport();
    }
}
