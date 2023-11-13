namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IFilteredImplicitSubscriptionWithExtensionGrain")]
    public interface IFilteredImplicitSubscriptionWithExtensionGrain : IGrainWithGuidCompoundKey
    {
        [Alias("GetCounter")]
        Task<int> GetCounter();
    }
}