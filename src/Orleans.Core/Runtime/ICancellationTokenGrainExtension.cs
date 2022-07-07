using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    [GrainInterfaceType("CancellationToken")]
    internal interface ICancellationTokenGrainExtension : IGrainExtension
    {
        [AlwaysInterleave]
        ValueTask CancelAsync(GrainId caller, CorrelationId requestId);
    }
}
