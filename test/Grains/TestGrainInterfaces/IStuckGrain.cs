using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IStuckGrain")]
    public interface IStuckGrain : IGrainWithGuidKey
    {
        [Alias("RunForever")]
        Task RunForever();

        [Alias("NonBlockingCall")]
        Task NonBlockingCall();

        [Alias("GetNonBlockingCallCounter")]
        Task<int> GetNonBlockingCallCounter();

        [Alias("DidActivationTryToStart")]
        Task<bool> DidActivationTryToStart(GrainId id);

        [Alias("BlockingDeactivation")]
        Task BlockingDeactivation();
    }

    [Alias("UnitTests.GrainInterfaces.IStuckCleanGrain")]
    public interface IStuckCleanGrain : IGrainWithGuidKey
    {
        [Alias("Release")]
        Task Release(Guid key);

        [Alias("IsActivated")]
        Task<bool> IsActivated(Guid key);
    }
}
