namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IObserverGrain")]
    public interface IObserverGrain : IGrainWithIntegerKey
    {
        [Alias("SetTarget")]
        Task SetTarget(ISimpleObserverableGrain target);
        [Alias("Subscribe")]
        Task Subscribe(ISimpleGrainObserver observer);
    }
}
