using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Orleans.Durable.Storage.Hosting;

namespace Orleans.Durable.Storage.Bench;

[GenerateSerializer]
[Immutable]
public sealed record MeterHistogramSample(
    [property: Id(0)] long Count,
    [property: Id(1)] double Sum,
    [property: Id(2)] double Min,
    [property: Id(3)] double P50,
    [property: Id(4)] double P95,
    [property: Id(5)] double P99,
    [property: Id(6)] double Max,
    [property: Id(7)] string? Unit);

[GenerateSerializer]
[Immutable]
public sealed record SiloTelemetrySample(
    [property: Id(0)] DateTimeOffset UtcTimestamp,
    [property: Id(1)] double CpuPct,
    [property: Id(2)] double CoresUsed,
    [property: Id(3)] int Gen0Delta,
    [property: Id(4)] int Gen1Delta,
    [property: Id(5)] int Gen2Delta,
    [property: Id(6)] double AllocatedMbPerSec,
    [property: Id(7)] long HeapSizeBytes,
    [property: Id(8)] long FragmentedBytes,
    [property: Id(9)] double GcPauseTimePct,
    [property: Id(10)] long TotalCommittedBytes,
    [property: Id(11)] long WorkingSetBytes,
    [property: Id(12)] int ThreadPoolThreadCount,
    [property: Id(13)] long ThreadPoolPendingWorkItems,
    [property: Id(14)] long ThreadPoolCompletedWorkItemsDelta,
    [property: Id(15)] Dictionary<string, double> CounterDeltas,
    [property: Id(16)] Dictionary<string, double> Gauges,
    [property: Id(17)] Dictionary<string, MeterHistogramSample> Histograms,
    [property: Id(18)] BlobCounterSnapshot BlobDelta);

[GenerateSerializer]
[Immutable]
public sealed record SiloTelemetrySummary(
    [property: Id(0)] string SiloAddress,
    [property: Id(1)] string SiloName,
    [property: Id(2)] int SampleCount,
    [property: Id(3)] double MeanCpuPct,
    [property: Id(4)] double MaxCpuPct,
    [property: Id(5)] double MeanCoresUsed,
    [property: Id(6)] double MaxCoresUsed,
    [property: Id(7)] int TotalGen0,
    [property: Id(8)] int TotalGen1,
    [property: Id(9)] int TotalGen2,
    [property: Id(10)] double MeanAllocMbPerSec,
    [property: Id(11)] double MaxAllocMbPerSec,
    [property: Id(12)] double MaxGcPausePct,
    [property: Id(13)] double MaxWorkingSetMb,
    [property: Id(14)] double MaxHeapSizeMb,
    [property: Id(15)] double MaxCommittedMb,
    [property: Id(16)] long MaxThreadPoolPending,
    [property: Id(17)] int MaxThreadPoolThreads,
    [property: Id(18)] Dictionary<string, double> MeterCounterTotals,
    [property: Id(19)] Dictionary<string, MeterHistogramSample> MeterHistograms,
    [property: Id(20)] List<SiloTelemetrySample>? Series);

/// <summary>
/// Samples process CPU, GC, memory, ThreadPool, <c>Orleans.Durable.Storage*</c>/<c>Microsoft.Orleans</c> meters,
/// and <see cref="BlobRequestCounterPolicy"/> deltas once per second into a bounded 30-minute ring buffer.
/// </summary>
public sealed class SiloTelemetrySampler : IHostedService, IDisposable
{
    private const int RingCapacity = 1800; // 30 minutes at 1 Hz
    private const int MaxHistogramReservoirPerTick = 2048;

    private readonly BlobRequestCounterPolicy _blobPolicy;
    private readonly BenchEventLogStore _eventLog;
    private readonly IServiceProvider _services;
    private readonly ILogger<SiloTelemetrySampler> _logger;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Lock _ringLock = new();
    private readonly SiloTelemetrySample?[] _ring = new SiloTelemetrySample?[RingCapacity];
    private int _ringHead;
    private int _ringCount;

    private readonly MeterListener _meterListener = new();
    private readonly ConcurrentDictionary<string, CounterAccumulator> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GaugeHolder> _gauges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HistogramAccumulator> _histograms = new(StringComparer.Ordinal);

    private readonly Dictionary<ulong, (bool IsOpen, string Primary)> _knownLedgers = [];
    private readonly Dictionary<ulong, (long Watermark, long UndrainedBytes)> _knownPrimaries = [];
    private ulong _lastMapEpoch;
    private ulong? _lastRetainedWalFiles;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private TimeSpan _prevCpu;
    private long _prevTimestamp;
    private int _prevGen0;
    private int _prevGen1;
    private int _prevGen2;
    private long _prevAllocatedBytes;
    private long _prevCompletedWorkItems;
    private BlobCounterSnapshot _prevBlobSnapshot = BlobCounterSnapshot.Empty;

    public SiloTelemetrySampler(
        BlobRequestCounterPolicy blobPolicy,
        BenchEventLogStore eventLog,
        IServiceProvider services,
        ILogger<SiloTelemetrySampler> logger)
    {
        _blobPolicy = blobPolicy;
        _eventLog = eventLog;
        _services = services;
        _logger = logger;
        ConfigureMeterListener();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _meterListener.Start();
        _process.Refresh();
        _prevCpu = _process.TotalProcessorTime;
        _prevTimestamp = Stopwatch.GetTimestamp();
        _prevGen0 = GC.CollectionCount(0);
        _prevGen1 = GC.CollectionCount(1);
        _prevGen2 = GC.CollectionCount(2);
        _prevAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        _prevCompletedWorkItems = ThreadPool.CompletedWorkItemCount;
        _prevBlobSnapshot = _blobPolicy.Snapshot();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => SampleLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Ignore shutdown cancellation/timeout.
            }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _meterListener.Dispose();
        _process.Dispose();
    }

    public List<SiloTelemetrySample> GetSamples(DateTimeOffset? sinceUtc = null)
    {
        lock (_ringLock)
        {
            var result = new List<SiloTelemetrySample>(_ringCount);
            var start = (_ringHead - _ringCount + RingCapacity) % RingCapacity;
            for (var i = 0; i < _ringCount; i++)
            {
                var sample = _ring[(start + i) % RingCapacity];
                if (sample is null)
                {
                    continue;
                }

                if (sinceUtc is null || sample.UtcTimestamp >= sinceUtc.Value)
                {
                    result.Add(sample);
                }
            }

            return result;
        }
    }

    public static SiloTelemetrySummary Summarize(
        string siloAddress,
        string siloName,
        List<SiloTelemetrySample> samples,
        bool includeSeries)
    {
        if (samples.Count == 0)
        {
            return new SiloTelemetrySummary(
                siloAddress,
                siloName,
                SampleCount: 0,
                MeanCpuPct: 0,
                MaxCpuPct: 0,
                MeanCoresUsed: 0,
                MaxCoresUsed: 0,
                TotalGen0: 0,
                TotalGen1: 0,
                TotalGen2: 0,
                MeanAllocMbPerSec: 0,
                MaxAllocMbPerSec: 0,
                MaxGcPausePct: 0,
                MaxWorkingSetMb: 0,
                MaxHeapSizeMb: 0,
                MaxCommittedMb: 0,
                MaxThreadPoolPending: 0,
                MaxThreadPoolThreads: 0,
                MeterCounterTotals: [],
                MeterHistograms: [],
                Series: includeSeries ? samples : null);
        }

        double cpuSum = 0, maxCpu = 0;
        double coresSum = 0, maxCores = 0;
        int gen0 = 0, gen1 = 0, gen2 = 0;
        double allocSum = 0, maxAlloc = 0;
        double maxPause = 0;
        long maxWs = 0, maxHeap = 0, maxCommitted = 0, maxPending = 0;
        int maxThreads = 0;

        var counterTotals = new Dictionary<string, double>(StringComparer.Ordinal);
        var histMerge = new Dictionary<string, (long Count, double Sum, double Min, double Max, List<double> P50s, List<double> P95s, List<double> P99s, string? Unit)>(StringComparer.Ordinal);

        foreach (var s in samples)
        {
            cpuSum += s.CpuPct;
            if (s.CpuPct > maxCpu)
            {
                maxCpu = s.CpuPct;
            }

            coresSum += s.CoresUsed;
            if (s.CoresUsed > maxCores)
            {
                maxCores = s.CoresUsed;
            }

            gen0 += s.Gen0Delta;
            gen1 += s.Gen1Delta;
            gen2 += s.Gen2Delta;

            allocSum += s.AllocatedMbPerSec;
            if (s.AllocatedMbPerSec > maxAlloc)
            {
                maxAlloc = s.AllocatedMbPerSec;
            }

            if (s.GcPauseTimePct > maxPause)
            {
                maxPause = s.GcPauseTimePct;
            }

            if (s.WorkingSetBytes > maxWs)
            {
                maxWs = s.WorkingSetBytes;
            }

            if (s.HeapSizeBytes > maxHeap)
            {
                maxHeap = s.HeapSizeBytes;
            }

            if (s.TotalCommittedBytes > maxCommitted)
            {
                maxCommitted = s.TotalCommittedBytes;
            }

            if (s.ThreadPoolPendingWorkItems > maxPending)
            {
                maxPending = s.ThreadPoolPendingWorkItems;
            }

            if (s.ThreadPoolThreadCount > maxThreads)
            {
                maxThreads = s.ThreadPoolThreadCount;
            }

            foreach (var (name, val) in s.CounterDeltas)
            {
                counterTotals[name] = counterTotals.GetValueOrDefault(name) + val;
            }

            foreach (var (name, h) in s.Histograms)
            {
                if (!histMerge.TryGetValue(name, out var agg))
                {
                    agg = (0, 0, double.MaxValue, double.MinValue, [], [], [], h.Unit);
                }

                agg.Count += h.Count;
                agg.Sum += h.Sum;
                if (h.Min < agg.Min)
                {
                    agg.Min = h.Min;
                }

                if (h.Max > agg.Max)
                {
                    agg.Max = h.Max;
                }

                agg.P50s.Add(h.P50);
                agg.P95s.Add(h.P95);
                agg.P99s.Add(h.P99);
                histMerge[name] = agg;
            }
        }

        var mergedHistograms = new Dictionary<string, MeterHistogramSample>(histMerge.Count, StringComparer.Ordinal);
        foreach (var (name, agg) in histMerge)
        {
            if (agg.Count == 0)
            {
                continue;
            }

            mergedHistograms[name] = new MeterHistogramSample(
                Count: agg.Count,
                Sum: Math.Round(agg.Sum, 4),
                Min: Math.Round(agg.Min, 4),
                P50: Math.Round(Percentile(agg.P50s, 0.50), 4),
                P95: Math.Round(Percentile(agg.P95s, 0.95), 4),
                P99: Math.Round(Percentile(agg.P99s, 0.99), 4),
                Max: Math.Round(agg.Max, 4),
                Unit: agg.Unit);
        }

        const double bytesPerMb = 1024.0 * 1024.0;
        return new SiloTelemetrySummary(
            siloAddress,
            siloName,
            SampleCount: samples.Count,
            MeanCpuPct: Math.Round(cpuSum / samples.Count, 2),
            MaxCpuPct: Math.Round(maxCpu, 2),
            MeanCoresUsed: Math.Round(coresSum / samples.Count, 3),
            MaxCoresUsed: Math.Round(maxCores, 3),
            TotalGen0: gen0,
            TotalGen1: gen1,
            TotalGen2: gen2,
            MeanAllocMbPerSec: Math.Round(allocSum / samples.Count, 2),
            MaxAllocMbPerSec: Math.Round(maxAlloc, 2),
            MaxGcPausePct: Math.Round(maxPause, 2),
            MaxWorkingSetMb: Math.Round(maxWs / bytesPerMb, 2),
            MaxHeapSizeMb: Math.Round(maxHeap / bytesPerMb, 2),
            MaxCommittedMb: Math.Round(maxCommitted / bytesPerMb, 2),
            MaxThreadPoolPending: maxPending,
            MaxThreadPoolThreads: maxThreads,
            MeterCounterTotals: counterTotals,
            MeterHistograms: mergedHistograms,
            Series: includeSeries ? samples : null);
    }

    private async Task SampleLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                CaptureSample();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "SiloTelemetrySampler tick failed.");
            }
        }
    }

    private void CaptureSample()
    {
        _meterListener.RecordObservableInstruments();
        _process.Refresh();

        var now = DateTimeOffset.UtcNow;
        var nowTs = Stopwatch.GetTimestamp();
        var wallSec = Math.Max(0.001, Stopwatch.GetElapsedTime(_prevTimestamp, nowTs).TotalSeconds);
        _prevTimestamp = nowTs;

        var cpu = _process.TotalProcessorTime;
        var cpuDeltaSec = Math.Max(0.0, (cpu - _prevCpu).TotalSeconds);
        _prevCpu = cpu;

        var coresUsed = cpuDeltaSec / wallSec;
        var cpuPct = (coresUsed / Math.Max(1, Environment.ProcessorCount)) * 100.0;

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var gen0Delta = Math.Max(0, gen0 - _prevGen0);
        var gen1Delta = Math.Max(0, gen1 - _prevGen1);
        var gen2Delta = Math.Max(0, gen2 - _prevGen2);
        _prevGen0 = gen0;
        _prevGen1 = gen1;
        _prevGen2 = gen2;

        var allocBytes = GC.GetTotalAllocatedBytes(precise: false);
        var allocDelta = Math.Max(0L, allocBytes - _prevAllocatedBytes);
        _prevAllocatedBytes = allocBytes;
        var allocMbPerSec = (allocDelta / (1024.0 * 1024.0)) / wallSec;

        var gcInfo = GC.GetGCMemoryInfo();
        var workingSet = _process.WorkingSet64;

        var tpThreads = ThreadPool.ThreadCount;
        var tpPending = ThreadPool.PendingWorkItemCount;
        var tpCompleted = ThreadPool.CompletedWorkItemCount;
        var tpCompletedDelta = Math.Max(0L, tpCompleted - _prevCompletedWorkItems);
        _prevCompletedWorkItems = tpCompleted;

        var blobNow = _blobPolicy.Snapshot();
        var blobDelta = BlobCounterSnapshot.Diff(_prevBlobSnapshot, blobNow);
        _prevBlobSnapshot = blobNow;

        var counterDeltas = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, acc) in _counters)
        {
            var delta = acc.ExchangeZero();
            if (delta != 0)
            {
                counterDeltas[name] = Math.Round(delta, 4);
            }
        }

        var gauges = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, holder) in _gauges)
        {
            var value = holder.Read();
            if (!double.IsNaN(value))
            {
                gauges[name] = Math.Round(value, 4);
            }
        }

        var histograms = new Dictionary<string, MeterHistogramSample>(StringComparer.Ordinal);
        foreach (var (name, acc) in _histograms)
        {
            var snapshot = acc.Drain();
            if (snapshot is not null)
            {
                histograms[name] = snapshot;
            }
        }

        var sample = new SiloTelemetrySample(
            UtcTimestamp: now,
            CpuPct: Math.Round(cpuPct, 2),
            CoresUsed: Math.Round(coresUsed, 3),
            Gen0Delta: gen0Delta,
            Gen1Delta: gen1Delta,
            Gen2Delta: gen2Delta,
            AllocatedMbPerSec: Math.Round(allocMbPerSec, 2),
            HeapSizeBytes: gcInfo.HeapSizeBytes,
            FragmentedBytes: gcInfo.FragmentedBytes,
            GcPauseTimePct: Math.Round(gcInfo.PauseTimePercentage, 2),
            TotalCommittedBytes: gcInfo.TotalCommittedBytes,
            WorkingSetBytes: workingSet,
            ThreadPoolThreadCount: tpThreads,
            ThreadPoolPendingWorkItems: tpPending,
            ThreadPoolCompletedWorkItemsDelta: tpCompletedDelta,
            CounterDeltas: counterDeltas,
            Gauges: gauges,
            Histograms: histograms,
            BlobDelta: blobDelta);

        lock (_ringLock)
        {
            _ring[_ringHead] = sample;
            _ringHead = (_ringHead + 1) % RingCapacity;
            if (_ringCount < RingCapacity)
            {
                _ringCount++;
            }
        }

        ObserveStorageLifecycle();
    }

    private void ObserveStorageLifecycle()
    {
        // Track ledger map transitions (ledger open / seal / drop / primary handoff).
        var routing = _services.GetService<LocalRouting>();
        if (routing?.Current is { } current)
        {
            var map = current.View.Current;
            if (map.Epoch != _lastMapEpoch)
            {
                _lastMapEpoch = map.Epoch;
                var seen = new HashSet<ulong>(map.Ledgers.Length);
                foreach (var ledger in map.Ledgers)
                {
                    var id = ledger.Id.Value;
                    var primary = ledger.Primary.Node.ToString();
                    _ = seen.Add(id);

                    if (!_knownLedgers.TryGetValue(id, out var prev))
                    {
                        _knownLedgers[id] = (ledger.IsOpen, primary);
                        _eventLog.Record(
                            "Bench.Ledger",
                            eventId: 7201,
                            level: "Information",
                            message: string.Create(
                                CultureInfo.InvariantCulture,
                                $"LedgerOpened id={id} isOpen={ledger.IsOpen} primary={primary} epoch={map.Epoch}"),
                            writeConsole: true);
                    }
                    else
                    {
                        if (prev.IsOpen && !ledger.IsOpen)
                        {
                            _eventLog.Record(
                                "Bench.Ledger",
                                eventId: 7202,
                                level: "Information",
                                message: string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"LedgerSealed id={id} primary={primary} epoch={map.Epoch}"),
                                writeConsole: true);
                        }

                        if (!string.Equals(prev.Primary, primary, StringComparison.Ordinal))
                        {
                            _eventLog.Record(
                                "Bench.Ledger",
                                eventId: 7203,
                                level: "Information",
                                message: string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"LedgerPrimaryChanged id={id} from={prev.Primary} to={primary} epoch={map.Epoch}"),
                                writeConsole: true);
                        }

                        _knownLedgers[id] = (ledger.IsOpen, primary);
                    }
                }

                List<ulong>? dropped = null;
                foreach (var id in _knownLedgers.Keys)
                {
                    if (!seen.Contains(id))
                    {
                        dropped ??= [];
                        dropped.Add(id);
                    }
                }

                if (dropped is not null)
                {
                    foreach (var id in dropped)
                    {
                        _ = _knownLedgers.Remove(id);
                        _ = _knownPrimaries.Remove(id);
                        _eventLog.Record(
                            "Bench.Ledger",
                            eventId: 7204,
                            level: "Information",
                            message: string.Create(CultureInfo.InvariantCulture, $"LedgerDropped id={id} epoch={map.Epoch}"),
                            writeConsole: true);
                    }
                }
            }
        }

        // Track segment drains (watermark advances) and retained WAL file count changes.
        var dsStatus = _services.GetService<DurableStorageStatus>();
        if (dsStatus?.NodeStatus is { } nodeStatus)
        {
            if (nodeStatus.Resources is { } res)
            {
                if (_lastRetainedWalFiles is null || _lastRetainedWalFiles.Value != res.RetainedWalFiles)
                {
                    var prevFiles = _lastRetainedWalFiles;
                    _lastRetainedWalFiles = res.RetainedWalFiles;
                    _eventLog.Record(
                        "Bench.Wal",
                        eventId: 7210,
                        level: "Information",
                        message: string.Create(
                            CultureInfo.InvariantCulture,
                            $"WalRetainedFilesChanged prev={prevFiles?.ToString(CultureInfo.InvariantCulture) ?? "none"} current={res.RetainedWalFiles} diskUsedMb={res.DiskUsedBytes / (1024.0 * 1024.0):F1} underPressure={res.UnderPressure}"),
                        writeConsole: true);
                }
            }

            foreach (var p in nodeStatus.Primaries)
            {
                var id = p.Ledger.Value;
                var watermark = p.Watermark.Value;
                var undrained = p.Undrained.Bytes;
                if (_knownPrimaries.TryGetValue(id, out var prev))
                {
                    if (watermark > prev.Watermark)
                    {
                        _eventLog.Record(
                            "Bench.Segment",
                            eventId: 7220,
                            level: "Information",
                            message: string.Create(
                                CultureInfo.InvariantCulture,
                                $"SegmentDrained ledger={id} watermark={prev.Watermark}->{watermark} tail={p.ContiguousTail.Value} undrainedMb={undrained / (1024.0 * 1024.0):F2}"),
                            writeConsole: true);
                    }
                }

                _knownPrimaries[id] = (watermark, undrained);
            }
        }
    }

    private void ConfigureMeterListener()
    {
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            var meterName = instrument.Meter.Name;
            if (meterName.StartsWith("Orleans.Durable.Storage", StringComparison.Ordinal)
                || string.Equals(meterName, "Microsoft.Orleans", StringComparison.Ordinal)
                || meterName.StartsWith("Microsoft.Orleans.", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _meterListener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        _meterListener.SetMeasurementEventCallback<int>(OnIntMeasurement);
        _meterListener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
        _meterListener.SetMeasurementEventCallback<float>(OnFloatMeasurement);
    }

    private void OnLongMeasurement(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        RecordMeasurement(instrument, value);

    private void OnIntMeasurement(Instrument instrument, int value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        RecordMeasurement(instrument, value);

    private void OnDoubleMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        RecordMeasurement(instrument, value);

    private void OnFloatMeasurement(Instrument instrument, float value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        RecordMeasurement(instrument, value);

    private void RecordMeasurement(Instrument instrument, double value)
    {
        var key = instrument.Name;
        var typeName = instrument.GetType().Name;
        if (typeName.StartsWith("Histogram", StringComparison.Ordinal))
        {
            var acc = _histograms.GetOrAdd(key, _ => new HistogramAccumulator(instrument.Unit));
            acc.Record(value);
        }
        else if (typeName.StartsWith("ObservableGauge", StringComparison.Ordinal)
            || typeName.StartsWith("Gauge", StringComparison.Ordinal)
            || typeName.StartsWith("ObservableUpDownCounter", StringComparison.Ordinal)
            || typeName.StartsWith("ObservableCounter", StringComparison.Ordinal))
        {
            var holder = _gauges.GetOrAdd(key, static _ => new GaugeHolder());
            holder.Write(value);
        }
        else
        {
            var acc = _counters.GetOrAdd(key, static _ => new CounterAccumulator());
            acc.Add(value);
        }
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        values.Sort();
        var index = (int)Math.Ceiling(percentile * values.Count) - 1;
        index = Math.Clamp(index, 0, values.Count - 1);
        return values[index];
    }

    private sealed class CounterAccumulator
    {
        private double _value;
        private readonly Lock _lock = new();

        public void Add(double delta)
        {
            lock (_lock)
            {
                _value += delta;
            }
        }

        public double ExchangeZero()
        {
            lock (_lock)
            {
                var v = _value;
                _value = 0;
                return v;
            }
        }
    }

    private sealed class GaugeHolder
    {
        private double _value = double.NaN;

        public void Write(double value) => Volatile.Write(ref _value, value);

        public double Read() => Volatile.Read(ref _value);
    }

    private sealed class HistogramAccumulator(string? unit)
    {
        private readonly Lock _lock = new();
        private long _count;
        private double _sum;
        private double _min = double.MaxValue;
        private double _max = double.MinValue;
        private List<double> _samples = new(64);

        public void Record(double value)
        {
            lock (_lock)
            {
                _count++;
                _sum += value;
                if (value < _min)
                {
                    _min = value;
                }

                if (value > _max)
                {
                    _max = value;
                }

                if (_samples.Count < MaxHistogramReservoirPerTick)
                {
                    _samples.Add(value);
                }
                else
                {
                    var replaceIdx = Random.Shared.NextInt64(_count);
                    if (replaceIdx < MaxHistogramReservoirPerTick)
                    {
                        _samples[(int)replaceIdx] = value;
                    }
                }
            }
        }

        public MeterHistogramSample? Drain()
        {
            long count;
            double sum, min, max;
            List<double> samples;
            lock (_lock)
            {
                if (_count == 0)
                {
                    return null;
                }

                count = _count;
                sum = _sum;
                min = _min;
                max = _max;
                samples = _samples;
                _count = 0;
                _sum = 0;
                _min = double.MaxValue;
                _max = double.MinValue;
                _samples = new List<double>(64);
            }

            samples.Sort();
            return new MeterHistogramSample(
                Count: count,
                Sum: Math.Round(sum, 4),
                Min: Math.Round(min, 4),
                P50: Math.Round(Pick(samples, 0.50), 4),
                P95: Math.Round(Pick(samples, 0.95), 4),
                P99: Math.Round(Pick(samples, 0.99), 4),
                Max: Math.Round(max, 4),
                Unit: unit);
        }

        private static double Pick(List<double> sorted, double p)
        {
            if (sorted.Count == 0)
            {
                return 0;
            }

            var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
            return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
        }
    }
}
