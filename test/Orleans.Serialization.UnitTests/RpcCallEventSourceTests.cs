#if ORLEANS_PROFILING
using System.Linq;
using Orleans.Serialization.Diagnostics;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
public class RpcCallEventSourceTests
{
    [Fact]
    public void DeterministicSampler_IsStableAndRespectsPowerOfTwoRates()
    {
        for (var correlationId = 0L; correlationId < 10_000; correlationId++)
        {
            Assert.Equal(
                RpcCallEventSource.IsSampled(correlationId, 64),
                RpcCallEventSource.IsSampled(correlationId, 64));
        }

        Assert.False(RpcCallEventSource.IsSampled(1, 0));
        Assert.False(RpcCallEventSource.IsSampled(1, 3));
        Assert.False(RpcCallEventSource.IsSampled(0, 64));

        var sampled = Enumerable.Range(0, 65_536)
            .Count(id => RpcCallEventSource.IsSampled(id, 64));
        Assert.InRange(sampled, 900, 1_150);
    }
}
#endif
