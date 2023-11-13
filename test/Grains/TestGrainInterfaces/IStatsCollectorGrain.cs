namespace UnitTests.Stats
{
    [Alias("UnitTests.Stats.IStatsCollectorGrain")]
    public interface IStatsCollectorGrain : IGrainWithIntegerKey
    {
        [Alias("ReportStatsCalled")]
        Task ReportStatsCalled();

        [Alias("GetReportStatsCallCount")]
        Task<long> GetReportStatsCallCount();
    }
}