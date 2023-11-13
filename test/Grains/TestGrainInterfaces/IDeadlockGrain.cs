using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IDeadlockNonReentrantGrain")]
    public interface IDeadlockNonReentrantGrain : IGrainWithIntegerKey
    {
        [Alias("CallNext_1")]
        Task CallNext_1(List<(long GrainId, bool Blocking)> callChain, int currCallIndex);
        [Alias("CallNext_2")]
        Task CallNext_2(List<(long GrainId, bool Blocking)> callChain, int currCallIndex);
    }

    [Alias("UnitTests.GrainInterfaces.IDeadlockReentrantGrain")]
    public interface IDeadlockReentrantGrain : IGrainWithIntegerKey
    {
        [Alias("CallNext_1")]
        Task CallNext_1(List<(long GrainId, bool Blocking)> callChain, int currCallIndex);
        [Alias("CallNext_2")]
        Task CallNext_2(List<(long GrainId, bool Blocking)> callChain, int currCallIndex);
    }

    [Alias("UnitTests.GrainInterfaces.ICallChainObserver")]
    public interface ICallChainObserver : IGrainObserver
    {
        [Alias("OnEnter")]
        Task OnEnter(string grain, int callIndex);
        [Alias("OnExit")]
        Task OnExit(string grain, int callIndex);
    }

    [Alias("UnitTests.GrainInterfaces.ICallChainReentrancyGrain")]
    public interface ICallChainReentrancyGrain : IGrainWithStringKey
    {
        [Alias("CallChain")]
        Task CallChain(ICallChainObserver observer, List<(string TargetGrain, ReentrancyCallType CallType)> callChain, int callIndex);

        [AlwaysInterleave]
        [Alias("UnblockWaiters")]
        Task UnblockWaiters();
    }

    [GenerateSerializer]
    public enum ReentrancyCallType
    {
        Regular,
        AllowCallChainReentrancy,
        SuppressCallChainReentrancy,
    }
}

