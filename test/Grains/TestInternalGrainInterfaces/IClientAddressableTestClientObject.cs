namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IClientAddressableTestClientObject")]
    public interface IClientAddressableTestClientObject : IGrainObserver
    {
        [Alias("OnHappyPath")]
        Task<string> OnHappyPath(string message);
        [Alias("OnSadPath")]
        Task OnSadPath(string message);
        [Alias("OnSerialStress")]
        Task<int> OnSerialStress(int n);
        [Alias("OnParallelStress")]
        Task<int> OnParallelStress(int n);
    }
}
