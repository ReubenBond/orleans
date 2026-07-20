#if ORLEANS_PROFILING
using System;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Orleans.Serialization.Diagnostics;

public enum RpcCallPhase : byte
{
    RequestCreated = 1,
    RequestAddressingComplete = 2,
    TransportQueued = 3,
    SerializeStart = 4,
    SerializeStop = 5,
    FlushStart = 6,
    FlushStop = 7,
    FrameDecoded = 8,
    DispatchBuffered = 9,
    DispatchQueued = 10,
    DispatchBatchStart = 11,
    DispatchStart = 12,
    RuntimeReceived = 13,
    ActivationQueued = 14,
    InvocationStart = 15,
    InvocationStop = 16,
    ResponseCreated = 17,
    CallbackStart = 18,
    CompletionSignaled = 19,
    ContinuationStart = 20,
    CallbackComplete = 21,
    Failure = 22,
    Rejection = 23,
    Timeout = 24,
    Cancellation = 25,
    Forwarding = 26,
    Retry = 27,
}

internal enum RpcCallSelectionMode : byte
{
    DeterministicSample = 1,
    ExactTrace = 2,
}

public enum RpcCallResourceKind : byte
{
    None = 0,
    ConnectionSend = 1,
    PipeFlush = 2,
    InboundDispatch = 3,
    Activation = 4,
    Continuation = 5,
}

internal enum RpcBenchmarkPhase : byte
{
    Startup = 1,
    WarmupStart = 2,
    WarmupStop = 3,
    MeasurementStart = 4,
    MeasurementStop = 5,
    Shutdown = 6,
}

internal enum RpcProcessRole : byte
{
    Unknown = 0,
    Driver = 1,
    Target = 2,
}

public readonly record struct RpcCallTraceContext(
    ulong TraceIdHigh,
    ulong TraceIdLow,
    long CorrelationId,
    int OriginSiloPort,
    int OriginSiloGeneration,
    int LocalSiloPort,
    int LocalSiloGeneration,
    byte Direction,
    byte SelectionMode,
    int RetryCount,
    int ForwardCount);

[EventSource(Name = "Microsoft-Orleans-RpcLatency")]
internal sealed class RpcCallEventSource : EventSource
{
    internal static readonly RpcCallEventSource Log = new();

    private int _sampleRate = ReadEnvironmentSampleRate();

    private RpcCallEventSource()
        : base(EventSourceSettings.EtwSelfDescribingEventFormat)
    {
    }

    internal int SampleRate => Volatile.Read(ref _sampleRate);

    internal static int PendingWorkItemCount
    {
        get
        {
#if NET6_0_OR_GREATER
            return (int)Math.Min(ThreadPool.PendingWorkItemCount, int.MaxValue);
#else
            return -1;
#endif
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsSampled(long correlationId)
    {
        var rate = Volatile.Read(ref _sampleRate);
        return IsSampled(correlationId, rate);
    }

    internal static bool IsSampled(long correlationId, int sampleRate) =>
        correlationId != 0
        && sampleRate > 0
        && (sampleRate & (sampleRate - 1)) == 0
        && (Mix64(unchecked((ulong)correlationId)) & (uint)(sampleRate - 1)) == 0;

    [NonEvent]
    internal void WritePhase(
        in RpcCallTraceContext context,
        RpcCallPhase phase,
        RpcCallResourceKind resourceKind = RpcCallResourceKind.None,
        long resourceId = 0,
        int queueDepth = -1,
        int batchSize = 0,
        int batchIndex = -1,
        int detail = 0,
        long durationTicks = 0)
    {
        if (!IsEnabled())
        {
            return;
        }

        Phase(
            context.TraceIdHigh,
            context.TraceIdLow,
            context.CorrelationId,
            context.OriginSiloPort,
            context.OriginSiloGeneration,
            context.LocalSiloPort,
            context.LocalSiloGeneration,
            context.Direction,
            (byte)phase,
            context.SelectionMode,
            (byte)resourceKind,
            resourceId,
            queueDepth,
            context.RetryCount,
            context.ForwardCount,
            batchSize,
            batchIndex,
            detail,
            durationTicks,
            System.Diagnostics.Stopwatch.Frequency,
            SampleRate);
    }

    [NonEvent]
    internal void WriteBenchmarkPhase(RpcBenchmarkPhase phase, RpcProcessRole processRole) =>
        BenchmarkPhase((byte)phase, (byte)processRole);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        base.OnEventCommand(command);
        if (command.Command is not (EventCommand.Enable or EventCommand.Update) || command.Arguments is null)
        {
            return;
        }

        foreach (var pair in command.Arguments)
        {
            if (string.Equals(pair.Key, "SampleRate", StringComparison.OrdinalIgnoreCase))
            {
                Volatile.Write(ref _sampleRate, ParseSampleRate(pair.Value));
                break;
            }
        }
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.Phases)]
    private unsafe void Phase(
        ulong traceIdHigh,
        ulong traceIdLow,
        long correlationId,
        int originSiloPort,
        int originSiloGeneration,
        int localSiloPort,
        int localSiloGeneration,
        byte direction,
        byte phase,
        byte selectionMode,
        byte resourceKind,
        long resourceId,
        int queueDepth,
        int retryCount,
        int forwardCount,
        int batchSize,
        int batchIndex,
        int detail,
        long durationTicks,
        long stopwatchFrequency,
        int sampleRate)
    {
        EventData* data = stackalloc EventData[21];
        SetData(data + 0, &traceIdHigh, sizeof(ulong));
        SetData(data + 1, &traceIdLow, sizeof(ulong));
        SetData(data + 2, &correlationId, sizeof(long));
        SetData(data + 3, &originSiloPort, sizeof(int));
        SetData(data + 4, &originSiloGeneration, sizeof(int));
        SetData(data + 5, &localSiloPort, sizeof(int));
        SetData(data + 6, &localSiloGeneration, sizeof(int));
        SetData(data + 7, &direction, sizeof(byte));
        SetData(data + 8, &phase, sizeof(byte));
        SetData(data + 9, &selectionMode, sizeof(byte));
        SetData(data + 10, &resourceKind, sizeof(byte));
        SetData(data + 11, &resourceId, sizeof(long));
        SetData(data + 12, &queueDepth, sizeof(int));
        SetData(data + 13, &retryCount, sizeof(int));
        SetData(data + 14, &forwardCount, sizeof(int));
        SetData(data + 15, &batchSize, sizeof(int));
        SetData(data + 16, &batchIndex, sizeof(int));
        SetData(data + 17, &detail, sizeof(int));
        SetData(data + 18, &durationTicks, sizeof(long));
        SetData(data + 19, &stopwatchFrequency, sizeof(long));
        SetData(data + 20, &sampleRate, sizeof(int));
        WriteEventCore(1, 21, data);

        static void SetData(EventData* data, void* value, int size)
        {
            data->DataPointer = (IntPtr)value;
            data->Size = size;
        }
    }

    [Event(2, Level = EventLevel.Informational, Keywords = Keywords.Benchmark)]
    private void BenchmarkPhase(byte phase, byte processRole) => WriteEvent(2, phase, processRole);

    private static int ReadEnvironmentSampleRate() =>
        ParseSampleRate(Environment.GetEnvironmentVariable("ORLEANS_RPC_TRACE_SAMPLE_RATE"));

    private static int ParseSampleRate(string? value)
    {
        if (!int.TryParse(value, out var result) || result < 0 || (result != 0 && (result & (result - 1)) != 0))
        {
            return 0;
        }

        return result;
    }

    private static ulong Mix64(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }

    internal static class Keywords
    {
        internal const EventKeywords Phases = (EventKeywords)0x1;
        internal const EventKeywords Benchmark = (EventKeywords)0x2;
    }
}
#endif
