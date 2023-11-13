namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ISimpleObserverableGrain")]
    public interface ISimpleObserverableGrain : ISimpleGrain
    {
        [Alias("Subscribe")]
        Task Subscribe(ISimpleGrainObserver observer);
        [Alias("Unsubscribe")]
        Task Unsubscribe(ISimpleGrainObserver observer);
        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();
    }

    [Alias("UnitTests.GrainInterfaces.ISimpleGrainObserver")]
    public interface ISimpleGrainObserver : IGrainObserver
    {
        [Alias("StateChanged")]
        void StateChanged(int a, int b);
    }
}
