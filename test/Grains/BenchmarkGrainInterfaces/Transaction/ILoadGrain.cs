namespace BenchmarkGrainInterfaces.Transaction
{
    [GenerateSerializer]
    [Alias("BenchmarkGrainInterfaces.Transaction.Report")]
    public class Report
    {
        [Id(1)]
        public int Succeeded { get; set; }

        [Id(2)]
        public int Failed { get; set; }

        [Id(3)]
        public int Throttled { get; set; }

        [Id(4)]
        public TimeSpan Elapsed { get; set; }
    }

    [Alias("BenchmarkGrainInterfaces.Transaction.ILoadGrain")]
    public interface ILoadGrain : IGrainWithGuidKey
    {
        [Alias("Generate")]
        Task Generate(int run, int transactions, int conncurrent);
        [Alias("TryGetReport")]
        Task<Report> TryGetReport();
    }
}