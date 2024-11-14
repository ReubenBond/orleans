using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Grpc.Core;
using Orleans.Runtime;
using pb = Microsoft.Orleans.ProtocolBuffers;

namespace Orleans.Grpc.Server;

internal sealed class GrpcWorkerGateway : pb.GrpcWorkerGateway.GrpcWorkerGatewayBase
{
    public override async Task Connect(IAsyncStreamReader<pb.WorkerMessage> requestStream, IServerStreamWriter<pb.GatewayMessage> responseStream, ServerCallContext context)
    {
        try
        {
            await agentWorker.ConnectToWorkerProcess(requestStream, responseStream, context).ConfigureAwait(true);
        }
        catch
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                return;
            }
            throw;
        }
    }
}
