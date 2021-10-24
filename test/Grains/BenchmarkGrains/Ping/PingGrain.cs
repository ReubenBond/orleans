using Orleans;
using BenchmarkGrainInterfaces.Ping;
using System.Threading.Tasks;
using Orleans.Runtime;
using System.Threading;
using Orleans.Placement;

namespace BenchmarkGrains.Ping
{
    public class PingGrain : IGrainBase, IPingGrain
    {
        private IPingGrain _self;

        public PingGrain(IGrainContext context)
        {
            GrainContext = context;
        }

        public IGrainContext GrainContext { get; set; }

        public Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _self = this.AsReference<IPingGrain>();
            return Task.CompletedTask;
        }

        public ValueTask Ping() => default;

        public ValueTask PingPongInterleave(IPingGrain other, int count)
        {
            if (count == 0) return default;
            return other.PingPongInterleave(_self, count - 1);
        }
    }
        
    [SystemServicePlacement]
    [GrainType("ping-svc")]
    public class PingService : IPingService
    {
        public ValueTask Ping() => default;
    }
}
