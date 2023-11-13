namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IFilteredImplicitSubscriptionGrain")]
    public interface IFilteredImplicitSubscriptionGrain : IGrainWithGuidKey
    {
        [Alias("GetCounter")]
        Task<int> GetCounter(string streamNamespace);
    }
}