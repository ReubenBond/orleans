namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IImplicitSubscriptionCounterGrain")]
    public interface IImplicitSubscriptionCounterGrain : IGrainWithGuidKey
    {
        [Alias("GetEventCounter")]
        Task<int> GetEventCounter();

        [Alias("GetErrorCounter")]
        Task<int> GetErrorCounter();

        [Alias("Deactivate")]
        Task Deactivate();

        [Alias("DeactivateOnEvent")]
        Task DeactivateOnEvent(bool deactivate);
    }
}