namespace DistributedTests.GrainInterfaces
{
    [Alias("DistributedTests.GrainInterfaces.IPingGrain")]
    public interface IPingGrain : IGrainWithGuidKey
    {
        [Alias("Ping")]
        ValueTask Ping();
    }
}
