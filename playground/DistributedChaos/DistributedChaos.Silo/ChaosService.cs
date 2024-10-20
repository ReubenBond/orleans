using DistributedChaos;
using Grpc.Core;

public sealed class ChaosService(IClusterClient clusterClient) : Chaos.ChaosBase
{
    public override async Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
    {
        await clusterClient.GetGrain<IPingGrain>(request.Id).Ping();
        return new PingResponse();
    }
}
