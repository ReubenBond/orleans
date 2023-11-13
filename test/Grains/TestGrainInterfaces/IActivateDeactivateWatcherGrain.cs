namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IActivateDeactivateWatcherGrain")]
    public interface IActivateDeactivateWatcherGrain : IGrainWithIntegerKey
    {
        [Alias("GetActivateCalls")]
        Task<string[]> GetActivateCalls();
        [Alias("GetDeactivateCalls")]
        Task<string[]> GetDeactivateCalls();

        [Alias("Clear")]
        Task Clear();

        [Alias("RecordActivateCall")]
        Task RecordActivateCall(string activation);
        [Alias("RecordDeactivateCall")]
        Task RecordDeactivateCall(string activation);
    }
}
