using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IReentrantStressTestGrain")]
    public interface IReentrantStressTestGrain : IGrainWithIntegerKey
    {
        [Alias("Echo")]
        Task<byte[]> Echo(byte[] data);

        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();

        [Alias("Ping")]
        Task Ping(byte[] data);

        [Alias("PingWithDelay")]
        Task PingWithDelay(byte[] data, TimeSpan delay);

        [Alias("PingMutableArray")]
        Task PingMutableArray(byte[] data, long nextGrain, bool nextGrainIsRemote);

        [Alias("PingImmutableArray")]
        Task PingImmutableArray(Immutable<byte[]> data, long nextGrain, bool nextGrainIsRemote);

        [Alias("PingMutableDictionary")]
        Task PingMutableDictionary(Dictionary<int, string> data, long nextGrain, bool nextGrainIsRemote);

        [Alias("PingImmutableDictionary")]
        Task PingImmutableDictionary(Immutable<Dictionary<int, string>> data, long nextGrain, bool nextGrainIsRemote);

        [Alias("InterleavingConsistencyTest")]
        Task InterleavingConsistencyTest(int numItems);
    }

    [Alias("UnitTests.GrainInterfaces.IReentrantLocalStressTestGrain")]
    public interface IReentrantLocalStressTestGrain : IGrainWithIntegerKey
    {
        [Alias("Echo")]
        Task<byte[]> Echo(byte[] data);

        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();

        [Alias("Ping")]
        Task Ping(byte[] data);

        [Alias("PingWithDelay")]
        Task PingWithDelay(byte[] data, TimeSpan delay);

        [Alias("PingMutableArray")]
        Task PingMutableArray(byte[] data, long nextGrain, bool nextGrainIsRemote);

        [Alias("PingImmutableArray")]
        Task PingImmutableArray(Immutable<byte[]> data, long nextGrain, bool nextGrainIsRemote);

        [Alias("PingMutableDictionary")]
        Task PingMutableDictionary(Dictionary<int, string> data, long nextGrain, bool nextGrainIsRemote);

        [Alias("PingImmutableDictionary")]
        Task PingImmutableDictionary(Immutable<Dictionary<int, string>> data, long nextGrain, bool nextGrainIsRemote);
    }
}
