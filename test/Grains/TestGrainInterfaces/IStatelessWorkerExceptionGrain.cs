namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IStatelessWorkerExceptionGrain")]
    public interface IStatelessWorkerExceptionGrain : IGrainWithIntegerKey
    {
        [Alias("Ping")]
        Task Ping();
    }
}
