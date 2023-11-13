using Orleans.Concurrency;

namespace BenchmarkGrainInterfaces.Ping
{
    [Alias("BenchmarkGrainInterfaces.Ping.IPingGrain")]
    public interface IPingGrain : IGrainWithIntegerKey
    {
        [Alias("Run")]
        ValueTask Run();

        [AlwaysInterleave]
        [Alias("PingPongInterleave")]
        ValueTask PingPongInterleave(IPingGrain other, int count);
    }
}
