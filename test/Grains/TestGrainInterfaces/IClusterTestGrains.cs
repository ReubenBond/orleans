namespace TestGrainInterfaces
{
    [Alias("TestGrainInterfaces.IClusterTestGrain")]
    public interface IClusterTestGrain : IGrainWithIntegerKey
    {
        [Alias("SayHelloAsync")]
        Task<int> SayHelloAsync();
        [Alias("Deactivate")]
        Task Deactivate();
        [Alias("GetRuntimeId")]
        Task<string> GetRuntimeId();
        [Alias("Subscribe")]
        Task Subscribe(IClusterTestListener listener);
        [Alias("EnableStreamNotifications")]
        Task EnableStreamNotifications();
    }

    [Alias("TestGrainInterfaces.IClusterTestListener")]
    public interface IClusterTestListener : IGrainObserver
    {
        [Alias("GotHello")]
        void GotHello(int number);
    }
}
