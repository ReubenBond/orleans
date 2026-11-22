using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Shared formatting, percentile, and concurrency helpers across the benchmark runners.
/// </summary>
internal static class BenchmarkHelpers
{
    public const int Mebibyte = 1 << 20;
    public const long Gibibyte = 1L << 30;

    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    public static string FormatSize(int bytes) =>
        bytes switch
        {
            >= 1024 * 1024 when bytes % (1024 * 1024) == 0 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024 * 1024)} MiB"),
            >= 1024 when bytes % 1024 == 0 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024} KiB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        };

    public static string FormatMeanRange(IEnumerable<double> values)
    {
        var arr = values.ToArray();
        if (arr.Length == 0)
        {
            return "0.0";
        }

        if (arr.Length == 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{arr[0]:F2}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{arr.Average():F2} ({arr.Min():F2}-{arr.Max():F2})");
    }

    public static void UpdateMax(ref long location, long value)
    {
        long current;
        while ((current = Volatile.Read(ref location)) < value)
        {
            if (Interlocked.CompareExchange(ref location, value, current) == current)
            {
                return;
            }
        }
    }

    public static string MergeGen2Causes(IEnumerable<string> formattedCauses)
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in formattedCauses)
        {
            if (string.IsNullOrWhiteSpace(entry) || string.Equals(entry, "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var part in entry.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(part, "none", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var colon = part.LastIndexOf(':');
                if (colon > 0 && int.TryParse(part.AsSpan(colon + 1), CultureInfo.InvariantCulture, out var count))
                {
                    var key = part[..colon].Trim();
                    totals[key] = totals.GetValueOrDefault(key) + count;
                }
                else
                {
                    totals[part] = totals.GetValueOrDefault(part) + 1;
                }
            }
        }

        return totals.Count == 0
            ? "none"
            : string.Join(", ", totals.OrderByDescending(static kv => kv.Value).ThenBy(static kv => kv.Key, StringComparer.Ordinal).Select(static kv => $"{kv.Key}:{kv.Value}"));
    }
}

/// <summary>
/// Watches one measured run: wall time, CPU, working set, GC collections, GC pause duration, total and per-heap
/// sampled allocations (<c>GCAllocationTick</c>), and Gen2 GC causes (<c>GCStart</c>).
/// </summary>
internal sealed record BenchmarkProbeSummary(
    TimeSpan Elapsed,
    long AllocatedBytes,
    long SampledSohBytes,
    long SampledLohBytes,
    long SampledPohBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    TimeSpan GcPauseDuration,
    double GcPausePercent,
    TimeSpan CpuTime,
    double CpuSecondsPerGiB,
    double PeakWorkingSetMiB,
    IReadOnlyDictionary<string, int> Gen2Causes)
{
    public string FormatGen2Causes() =>
        Gen2Causes.Count == 0
            ? "none"
            : string.Join(", ", Gen2Causes.OrderByDescending(static kv => kv.Value).ThenBy(static kv => kv.Key, StringComparer.Ordinal).Select(static kv => $"{kv.Key}:{kv.Value}"));
}

internal sealed class BenchmarkProbe : IDisposable
{
    private readonly HeapAllocations _allocations = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly long _allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    private readonly TimeSpan _cpuBefore;
    private readonly TimeSpan _pauseBefore = GC.GetTotalPauseDuration();
    private readonly int[] _collectionsBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private long _peakWorkingSet;

    private BenchmarkProbe()
    {
        _cpuBefore = _process.TotalProcessorTime;
        _peakWorkingSet = _process.WorkingSet64;
    }

    public static BenchmarkProbe Start() => new();

    public BenchmarkProbeSummary Stop(long bytesMoved)
    {
        SampleInstant();
        var elapsed = Stopwatch.GetElapsedTime(_started);
        var seconds = Math.Max(elapsed.TotalSeconds, 1e-9);
        var cpu = _process.TotalProcessorTime - _cpuBefore;
        var pause = GC.GetTotalPauseDuration() - _pauseBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - _allocatedBefore;
        var gen0 = GC.CollectionCount(0) - _collectionsBefore[0];
        var gen1 = GC.CollectionCount(1) - _collectionsBefore[1];
        var gen2 = GC.CollectionCount(2) - _collectionsBefore[2];
        var gib = bytesMoved / (double)BenchmarkHelpers.Gibibyte;
        return new BenchmarkProbeSummary(
            elapsed,
            allocated,
            _allocations.Bytes(0),
            _allocations.Bytes(1),
            _allocations.Bytes(2),
            gen0,
            gen1,
            gen2,
            pause,
            pause.TotalSeconds / seconds * 100.0,
            cpu,
            gib > 0 ? cpu.TotalSeconds / gib : 0,
            Interlocked.Read(ref _peakWorkingSet) / (double)BenchmarkHelpers.Mebibyte,
            _allocations.Gen2Causes());
    }

    public async Task WatchAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            SampleInstant();
        }

        SampleInstant();
    }

    public void SampleInstant()
    {
        _process.Refresh();
        BenchmarkHelpers.UpdateMax(ref _peakWorkingSet, _process.WorkingSet64);
    }

    public void Dispose()
    {
        _allocations.Dispose();
        _process.Dispose();
    }

    private sealed class HeapAllocations : EventListener
    {
        private static readonly string[] Reasons =
            ["AllocSmall", "Induced", "LowMemory", "Empty", "AllocLarge", "OutOfSpaceSOH", "OutOfSpaceLOH", "InducedNotForced", "Internal", "InducedLowMemory", "InducedCompacting", "LowMemoryHost", "PMFullGC", "LowMemoryHostBlocking"];

        private readonly long[] _bytes = new long[3];
        private readonly ConcurrentDictionary<string, int> _gen2Causes = new(StringComparer.Ordinal);
        private int _gcStartDepthIndex = -2;
        private int _gcStartReasonIndex = -2;
        private int _allocTickKindIndex = -2;
        private int _allocTickAmountIndex = -2;

        public long Bytes(int kind) => Interlocked.Read(ref _bytes[kind]);

        public IReadOnlyDictionary<string, int> Gen2Causes() => new Dictionary<string, int>(_gen2Causes, StringComparer.Ordinal);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.PayloadNames is not { } names || eventData.Payload is not { } payload)
            {
                return;
            }

            if (eventData.EventName?.StartsWith("GCStart", StringComparison.Ordinal) == true)
            {
                var depth = Volatile.Read(ref _gcStartDepthIndex);
                var reason = Volatile.Read(ref _gcStartReasonIndex);
                if (depth == -2)
                {
                    depth = names.IndexOf("Depth");
                    reason = names.IndexOf("Reason");
                    Volatile.Write(ref _gcStartReasonIndex, reason);
                    Volatile.Write(ref _gcStartDepthIndex, depth);
                }

                if (depth >= 0 && reason >= 0 && Convert.ToUInt32(payload[depth], CultureInfo.InvariantCulture) == 2)
                {
                    var code = Convert.ToUInt32(payload[reason], CultureInfo.InvariantCulture);
                    var name = code < Reasons.Length ? Reasons[code] : code.ToString(CultureInfo.InvariantCulture);
                    _ = _gen2Causes.AddOrUpdate(name, 1, static (_, count) => count + 1);
                }

                return;
            }

            if (eventData.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true)
            {
                return;
            }

            var kind = Volatile.Read(ref _allocTickKindIndex);
            var amount = Volatile.Read(ref _allocTickAmountIndex);
            if (kind == -2)
            {
                kind = names.IndexOf("AllocationKind");
                amount = names.IndexOf("AllocationAmount64");
                Volatile.Write(ref _allocTickAmountIndex, amount);
                Volatile.Write(ref _allocTickKindIndex, kind);
            }

            if (kind >= 0 && amount >= 0 && Convert.ToUInt32(payload[kind], CultureInfo.InvariantCulture) is var heap and < 3)
            {
                _ = Interlocked.Add(ref _bytes[heap], (long)Convert.ToUInt64(payload[amount], CultureInfo.InvariantCulture));
            }
        }
    }
}
