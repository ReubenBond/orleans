using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Concurrency;

namespace BenchmarkGrainInterfaces.Ping
{
    public interface IPingGrain : IGrainWithIntegerKey
    {
        Task Run();

        [AlwaysInterleave]
        Task PingPongInterleave(IPingGrain other, int count);
    }

    public interface IEchoGrain : IGrainWithIntegerKey
    {
        ValueTask<int> Echo(int input);
    }

    [GenerateMethodSerializers(null)]
    public interface INewEchoGrain : IGrainWithIntegerKey
    {
        ValueTask<int> Echo(int input);
    }
}
