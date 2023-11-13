namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IMultipleImplicitSubscriptionGrain")]
    public interface IMultipleImplicitSubscriptionGrain : IGrainWithGuidKey
    {
        [Alias("GetCounters")]
        Task<Tuple<int, int>> GetCounters();
    }
}
