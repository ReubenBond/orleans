using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Placement;

namespace BenchmarkGrainInterfaces.Ping
{
    public interface IPingGrain : IGrainWithIntegerKey
    {
        ValueTask Ping();

        [AlwaysInterleave]
        ValueTask PingPongInterleave(IPingGrain other, int count);
    }

    [DefaultGrainType("ping-svc")]
    public interface IPingService : ISystemService
    {
        ValueTask Ping();
    }
}
