using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace Orleans.Durable.Storage.Tests;

/// <summary>One run's rates, its allocations per byte moved among them.</summary>
internal sealed record MeasurementSample(
    double MebibytesPerSecond,
    double TotalBytesPerByte,
    double SmallObjectBytesPerByte,
    double LargeObjectBytesPerByte,
    double PinnedObjectBytesPerByte,
    double CpuSecondsPerGibibyte,
    double Gen0PerSecond,
    double Gen1PerSecond,
    double Gen2PerSecond,
    long PeakWorkingSet,
    long PeakBoundedBytes,
    long Bound,
    IReadOnlyDictionary<string, int> Gen2Causes)
{
    public const int Mebibyte = 1 << 20;

    /// <summary>Formats the mean of <paramref name="values"/> and their range.</summary>
    public static string Spread(IEnumerable<double> values, string format = "F0")
    {
        var list = values.ToList();
        return string.Create(CultureInfo.InvariantCulture, $"{list.Average().ToString(format, CultureInfo.InvariantCulture)} ({list.Min().ToString(format, CultureInfo.InvariantCulture)}–{list.Max().ToString(format, CultureInfo.InvariantCulture)})");
    }

    /// <summary>Writes <paramref name="line"/> to the test's output and the console.</summary>
    public static void Report(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        Console.WriteLine(line);
    }
}

/// <summary>
/// Watches one measured run: its time, the process's CPU, working set, and collections of each generation, its
/// allocations in all and as sampled on each heap, and the peak of one bounded count of bytes.
/// </summary>
internal sealed class MeasurementProbe : IDisposable
{
    private readonly HeapAllocations _allocations = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Func<long> _boundedBytes;
    private readonly long _bound;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly long _allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
    private readonly TimeSpan _cpuBefore;
    private readonly int[] _collectionsBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private long _peakWorkingSet;
    private long _peakBoundedBytes;

    public MeasurementProbe(Func<long> boundedBytes, long bound)
    {
        _boundedBytes = boundedBytes;
        _bound = bound;
        _cpuBefore = _process.TotalProcessorTime;
    }

    public async Task WatchAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, CancellationToken.None);
            _process.Refresh();
            _peakWorkingSet = Math.Max(_peakWorkingSet, _process.WorkingSet64);
            _peakBoundedBytes = Math.Max(_peakBoundedBytes, _boundedBytes());
        }
    }

    /// <summary>The run's sample, its rates per byte of <paramref name="moved"/>.</summary>
    public MeasurementSample Sample(long moved)
    {
        var seconds = Stopwatch.GetElapsedTime(_started).TotalSeconds;
        _process.Refresh();
        var cpu = (_process.TotalProcessorTime - _cpuBefore).TotalSeconds;
        double PerByte(long bytes) => bytes / (double)moved;
        double PerSecond(int generation) => (GC.CollectionCount(generation) - _collectionsBefore[generation]) / seconds;
        return new(
            moved / (double)MeasurementSample.Mebibyte / seconds,
            PerByte(GC.GetTotalAllocatedBytes(precise: false) - _allocatedBefore),
            PerByte(_allocations.Bytes(0)),
            PerByte(_allocations.Bytes(1)),
            PerByte(_allocations.Bytes(2)),
            cpu / (moved / (double)(1L << 30)),
            PerSecond(0),
            PerSecond(1),
            PerSecond(2),
            _peakWorkingSet,
            _peakBoundedBytes,
            _bound,
            _allocations.Gen2Causes());
    }

    public void Dispose()
    {
        _allocations.Dispose();
        _process.Dispose();
    }

    /// <summary>
    /// Sums the runtime's sampled allocations (<c>GCAllocationTick</c>, about one event per 100 KB) by heap: kind 0 the
    /// small object heap, 1 the large object heap, and 2 the pinned object heap; and counts each generation-2 collection's
    /// cause (<c>GCStart</c>'s reason at depth 2).
    /// </summary>
    private sealed class HeapAllocations : EventListener
    {
        private static readonly string[] Reasons =
            ["AllocSmall", "Induced", "LowMemory", "Empty", "AllocLarge", "OutOfSpaceSOH", "OutOfSpaceLOH", "InducedNotForced", "Internal", "InducedLowMemory", "InducedCompacting", "LowMemoryHost", "PMFullGC", "LowMemoryHostBlocking"];

        private readonly long[] _bytes = new long[3];
        private readonly ConcurrentDictionary<string, int> _gen2Causes = new(StringComparer.Ordinal);

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
                var depth = names.IndexOf("Depth");
                var reason = names.IndexOf("Reason");
                if (depth >= 0 && reason >= 0 && Convert.ToUInt32(payload[depth], CultureInfo.InvariantCulture) == 2)
                {
                    var code = Convert.ToUInt32(payload[reason], CultureInfo.InvariantCulture);
                    _ = _gen2Causes.AddOrUpdate(code < Reasons.Length ? Reasons[code] : code.ToString(CultureInfo.InvariantCulture), 1, static (_, count) => count + 1);
                }

                return;
            }

            if (eventData.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true)
            {
                return;
            }

            var kind = names.IndexOf("AllocationKind");
            var amount = names.IndexOf("AllocationAmount64");
            if (kind >= 0 && amount >= 0 && Convert.ToUInt32(payload[kind], CultureInfo.InvariantCulture) is var heap and < 3)
            {
                _ = Interlocked.Add(ref _bytes[heap], (long)Convert.ToUInt64(payload[amount], CultureInfo.InvariantCulture));
            }
        }
    }
}
