using System.Net;
#if ORLEANS_PROFILING
using System.Diagnostics.Tracing;
#endif
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Messaging;

public class MessageTests
{
#if ORLEANS_PROFILING
    [Fact, TestCategory("BVT")]
    public void RpcTrace_ExtractsExactTraceAndResetClearsIt()
    {
        using var listener = new RpcEventListener();
        var message = new Message
        {
            Id = new CorrelationId(42),
            SendingSilo = CreateAddress(default, 1).SiloAddress,
            RequestContextData = new()
            {
                [RpcCallTrace.ExactTraceMarker] = true,
                ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            },
        };

        Assert.True(RpcCallTrace.ShouldTrace(message));
        Assert.NotEqual(0UL, message.RpcTraceIdHigh);
        Assert.NotEqual(0UL, message.RpcTraceIdLow);

        message.Reset();

        Assert.Equal(0UL, message.RpcTraceIdHigh);
        Assert.Equal(0UL, message.RpcTraceIdLow);
    }
#endif
    [Fact, TestCategory("BVT")]
    public void AddToCacheInvalidationHeader_LimitsHeaderLength()
    {
        var message = new Message();

        for (var i = 0; i < Message.MaxCacheInvalidationHeaderEntries + 1; i++)
        {
            var grainId = GrainId.Create("test", i.ToString());
            message.AddToCacheInvalidationHeader(CreateAddress(grainId, i), CreateAddress(grainId, i + 100));
        }

        var header = message.CacheInvalidationHeader;
        Assert.NotNull(header);
        Assert.Equal(Message.MaxCacheInvalidationHeaderEntries, header.Count);
        Assert.DoesNotContain(header, update => update.GrainId.Equals(GrainId.Create("test", Message.MaxCacheInvalidationHeaderEntries.ToString())));
    }

    [Fact, TestCategory("BVT")]
    public void AddToCacheInvalidationHeader_DeduplicatesByGrainId()
    {
        var message = new Message();
        var grainId = GrainId.Create("test", "duplicate");
        var invalidAddress = CreateAddress(grainId, 1);
        var validAddress = CreateAddress(grainId, 2);

        message.AddToCacheInvalidationHeader(invalidAddress, validAddress);
        message.AddToCacheInvalidationHeader(CreateAddress(grainId, 3), CreateAddress(grainId, 4));

        var header = message.CacheInvalidationHeader;
        Assert.NotNull(header);
        var update = Assert.Single(header);
        Assert.Equal(grainId, update.GrainId);
        Assert.Equal(invalidAddress, update.InvalidGrainAddress);
        Assert.Equal(validAddress, update.ValidGrainAddress);
    }

    private static GrainAddress CreateAddress(GrainId grainId, int offset) => new()
    {
        GrainId = grainId,
        ActivationId = ActivationId.NewId(),
        SiloAddress = SiloAddress.New(IPAddress.Loopback, 10_000 + offset, offset + 1)
    };

#if ORLEANS_PROFILING
    private sealed class RpcEventListener : EventListener
    {
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Orleans-RpcLatency")
            {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }
    }
#endif
}
