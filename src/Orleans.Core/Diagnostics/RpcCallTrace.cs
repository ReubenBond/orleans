#if ORLEANS_PROFILING
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Diagnostics;

namespace Orleans.Runtime;

internal static class RpcCallTrace
{
    internal const string ExactTraceMarker = "orleans.profiling.exact";

    internal static bool IsEnabled => RpcCallEventSource.Log.IsEnabled();

    internal static void WriteBenchmarkPhase(byte phase, byte processRole) =>
        RpcCallEventSource.Log.WriteBenchmarkPhase((RpcBenchmarkPhase)phase, (RpcProcessRole)processRole);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ShouldTrace(Message message)
    {
        var log = RpcCallEventSource.Log;
        if (!log.IsEnabled())
        {
            return false;
        }

        EnsureExactTrace(message);
        return (message.RpcTraceIdHigh | message.RpcTraceIdLow) != 0 || log.IsSampled(message.Id.ToInt64());
    }

    internal static void Write(
        Message message,
        RpcCallPhase phase,
        SiloAddress? localSilo,
        RpcCallResourceKind resourceKind = RpcCallResourceKind.None,
        long resourceId = 0,
        int queueDepth = -1,
        int batchSize = 0,
        int batchIndex = -1,
        int detail = 0,
        long durationTicks = 0)
    {
        var context = CreateContext(message, localSilo);
        RpcCallEventSource.Log.WritePhase(
            context,
            phase,
            resourceKind,
            resourceId,
            queueDepth,
            batchSize,
            batchIndex,
            detail,
            durationTicks);
    }

    internal static void WriteResponse(Message request, RpcCallPhase phase, SiloAddress localSilo)
    {
        var context = CreateContext(request, localSilo) with { Direction = (byte)Message.Directions.Response };
        RpcCallEventSource.Log.WritePhase(
            context,
            phase,
            RpcCallResourceKind.None,
            resourceId: 0,
            queueDepth: -1,
            batchSize: 0,
            batchIndex: -1,
            detail: 0,
            durationTicks: 0);
    }

    internal static RpcCallTraceContext CreateContext(Message message, SiloAddress? localSilo)
    {
        EnsureExactTrace(message);
        var origin = message.Direction is Message.Directions.Response ? message.TargetSilo : message.SendingSilo;
        return new(
            message.RpcTraceIdHigh,
            message.RpcTraceIdLow,
            message.Id.ToInt64(),
            origin?.Endpoint.Port ?? 0,
            origin?.Generation ?? 0,
            localSilo?.Endpoint.Port ?? 0,
            localSilo?.Generation ?? 0,
            (byte)message.Direction,
            (byte)((message.RpcTraceIdHigh | message.RpcTraceIdLow) != 0
                ? RpcCallSelectionMode.ExactTrace
                : RpcCallSelectionMode.DeterministicSample),
            message.RetryCount,
            message.ForwardCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long GetResourceId(object resource) => RuntimeHelpers.GetHashCode(resource);

    internal static void EnsureExactTrace(Message message)
    {
        if ((message.RpcTraceIdHigh | message.RpcTraceIdLow) != 0
            || message.RequestContextData is not { } requestContext
            || !requestContext.TryGetValue(ExactTraceMarker, out var marker)
            || marker is not true
            || requestContext.TryGetActivityContext() is not { } activityContext)
        {
            return;
        }

        Span<byte> bytes = stackalloc byte[16];
        activityContext.TraceId.CopyTo(bytes);
        message.RpcTraceIdHigh = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        message.RpcTraceIdLow = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
    }
}
#endif
