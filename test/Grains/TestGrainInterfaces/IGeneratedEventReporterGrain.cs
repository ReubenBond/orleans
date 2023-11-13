using Orleans.Runtime;

namespace TestGrainInterfaces
{
    [Alias("TestGrainInterfaces.IGeneratedEventReporterGrain")]
    public interface IGeneratedEventReporterGrain : IGrainWithGuidKey
    {
        [Alias("ReportResult")]
        Task ReportResult(Guid streamGuid, string streamProvider, string streamNamespace, int count);

        [Alias("GetReport")]
        Task<IDictionary<Guid,int>> GetReport(string streamProvider, string streamNamespace);

        [Alias("Reset")]
        Task Reset();

        [Alias("IsLocatedOnSilo")]
        Task<bool> IsLocatedOnSilo(SiloAddress siloAddress);
    }
}
