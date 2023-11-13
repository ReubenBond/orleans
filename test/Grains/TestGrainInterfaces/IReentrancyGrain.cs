using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IReentrantGrain")]
    public interface IReentrantGrain : IGrainWithIntegerKey
    {
        [Alias("One")]
        Task<string> One();

        [Alias("Two")]
        Task<string> Two();

        [Alias("SetSelf")]
        Task SetSelf(IReentrantGrain self);
    }

    [Alias("UnitTests.GrainInterfaces.INonReentrantGrain")]
    public interface INonReentrantGrain : IGrainWithIntegerKey
    {
        [Alias("One")]
        Task<string> One();

        [Alias("Two")]
        Task<string> Two();

        [Alias("SetSelf")]
        Task SetSelf(INonReentrantGrain self);
    }

    [Alias("UnitTests.GrainInterfaces.IMayInterleaveStaticPredicateGrain")]
    public interface IMayInterleaveStaticPredicateGrain : IGrainWithIntegerKey
    {
        [Alias("One")]
        Task<string> One(string arg); // this interleaves only when arg == "reentrant"

        [Alias("Two")]
        Task<string> Two();
        [Alias("TwoReentrant")]
        Task<string> TwoReentrant();

        [Alias("Exceptional")]
        Task Exceptional();

        [Alias("SubscribeToStream")]
        Task SubscribeToStream();
        [Alias("PushToStream")]
        Task PushToStream(string item);

        [Alias("SetSelf")]
        Task SetSelf(IMayInterleaveStaticPredicateGrain self);
    }

    [Alias("UnitTests.GrainInterfaces.IMayInterleaveInstancedPredicateGrain")]
    public interface IMayInterleaveInstancedPredicateGrain : IGrainWithIntegerKey
    {
        [Alias("One")]
        Task<string> One(string arg); // this interleaves only when arg == "reentrant"

        [Alias("Two")]
        Task<string> Two();
        [Alias("TwoReentrant")]
        Task<string> TwoReentrant();

        [Alias("Exceptional")]
        Task Exceptional();

        [Alias("SubscribeToStream")]
        Task SubscribeToStream();
        [Alias("PushToStream")]
        Task PushToStream(string item);

        [Alias("SetSelf")]
        Task SetSelf(IMayInterleaveInstancedPredicateGrain self);
    }

    [Unordered]
    [Alias("UnitTests.GrainInterfaces.IUnorderedNonReentrantGrain")]
    public interface IUnorderedNonReentrantGrain : IGrainWithIntegerKey
    {
        [Alias("One")]
        Task<string> One();

        [Alias("Two")]
        Task<string> Two();

        [Alias("SetSelf")]
        Task SetSelf(IUnorderedNonReentrantGrain self);
    }

    [Alias("UnitTests.GrainInterfaces.IReentrantSelfManagedGrain")]
    public interface IReentrantSelfManagedGrain : IGrainWithIntegerKey
    {
        [Alias("GetCounter")]
        Task<int> GetCounter();

        [Alias("Ping")]
        Task Ping(int seconds);

        [Alias("SetDestination")]
        Task SetDestination(long id);
    }

    [Alias("UnitTests.GrainInterfaces.INonReentrantSelfManagedGrain")]
    public interface INonReentrantSelfManagedGrain : IGrainWithIntegerKey
    {
        [Alias("GetCounter")]
        Task<int> GetCounter();

        [Alias("Ping")]
        Task Ping(int seconds);

        [Alias("SetDestination")]
        Task SetDestination(long id);
    }

    [Alias("UnitTests.GrainInterfaces.IReentrantTaskGrain")]
    public interface IReentrantTaskGrain : IGrainWithIntegerKey
    {
        [Alias("SetDestination")]
        Task SetDestination(long id);
        [Alias("Ping")]
        Task Ping(TimeSpan wait);
        [Alias("GetCounter")]
        Task<int> GetCounter();
    }

    [Alias("UnitTests.GrainInterfaces.INonReentrantTaskGrain")]
    public interface INonReentrantTaskGrain : IGrainWithIntegerKey
    {
        [Alias("SetDestination")]
        Task SetDestination(long id);
        [Alias("Ping")]
        Task Ping(TimeSpan wait);
        [Alias("GetCounter")]
        Task<int> GetCounter();
    }

    [Alias("UnitTests.GrainInterfaces.IFanOutGrain")]
    public interface IFanOutGrain : IGrainWithIntegerKey
    {
        [Alias("FanOutReentrant")]
        Task FanOutReentrant(int offset, int num);
        [Alias("FanOutNonReentrant")]
        Task FanOutNonReentrant(int offset, int num);
        [Alias("FanOutReentrant_Chain")]
        Task FanOutReentrant_Chain(int offset, int num);
        [Alias("FanOutNonReentrant_Chain")]
        Task FanOutNonReentrant_Chain(int offset, int num);
    }

    [Alias("UnitTests.GrainInterfaces.IFanOutACGrain")]
    public interface IFanOutACGrain : IGrainWithIntegerKey
    {
        [Alias("FanOutACReentrant")]
        Task FanOutACReentrant(int offset, int num);
        [Alias("FanOutACNonReentrant")]
        Task FanOutACNonReentrant(int offset, int num);
        [Alias("FanOutACReentrant_Chain")]
        Task FanOutACReentrant_Chain(int offset, int num);
        [Alias("FanOutACNonReentrant_Chain")]
        Task FanOutACNonReentrant_Chain(int offset, int num);
    }
}
