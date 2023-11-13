namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IStatelessWorkerGrain")]
    public interface IStatelessWorkerGrain : IGrainWithIntegerKey
    {
        [Alias("LongCall")]
        Task LongCall();
        [Alias("GetCallStats")]
        Task<Tuple<Guid, string, List<Tuple<DateTime, DateTime>>>> GetCallStats();

        [Alias("DummyCall")]
        Task DummyCall();
    }
}
