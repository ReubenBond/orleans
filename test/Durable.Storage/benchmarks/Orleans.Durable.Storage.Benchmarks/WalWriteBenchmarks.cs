using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.IoUring;
using Orleans.Durable.Storage.Engine.Wal.Portable;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures durable closed-loop WAL appends, including page packing and completion dispatch.
/// </summary>
[MemoryDiagnoser]
public class WalWriteBenchmarks : IAsyncDisposable
{
    private WalWriteWorkload _workload = null!;

    [Params(128, 4096, 65536)]
    public int PayloadSize { get; set; }

    [Params(1, 16, 64)]
    public int Clients { get; set; }

    [Params(false, true)]
    public bool IoUring { get; set; }

    [IterationSetup]
    public void Setup() => _workload = WalWriteWorkload.CreateAsync(IoUring, PayloadSize).AsTask().GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = 1024)]
    public Task Append() => _workload.RunAsync(Clients, 1024 / Clients, null);

    [IterationCleanup]
    public void Cleanup() => _workload.DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return _workload?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}

internal sealed class WalWriteWorkload : IAsyncDisposable
{
    private readonly WalStorage _storage;
    private readonly WriteAheadLog _wal;
    private readonly string _path;
    private readonly ArcBuffer _payload;
    private bool _disposed;

    private WalWriteWorkload(WalStorage storage, WriteAheadLog wal, string path, ArcBuffer payload)
    {
        _storage = storage;
        _wal = wal;
        _path = path;
        _payload = payload;
    }

    internal static async ValueTask<WalWriteWorkload> CreateAsync(bool ioUring, int payloadSize, WalOptions? options = null)
    {
        WalStorage storage;
        if (ioUring)
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("The io_uring benchmark requires Linux.");
            }

            storage = new IoUringWalStorage();
        }
        else
        {
            storage = new PortableWalStorage();
        }

        var path = Path.Combine(Path.GetTempPath(), "clog-wal-benchmark-" + Guid.NewGuid().ToString("N"));
        var wal = await WriteAheadLog.CreateAsync(storage, path, options ?? new WalOptions()).ConfigureAwait(false);
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[payloadSize]);
        return new WalWriteWorkload(storage, wal, path, writer.PeekSlice(writer.Length));
    }

    internal Task RunAsync(int clients, int perClient, long[][]? samples, ArcBuffer? payload = null) =>
        Task.WhenAll(Enumerable.Range(0, clients).Select(client => Task.Run(async () =>
        {
            for (var index = 0; index < perClient; index++)
            {
                var start = samples is null ? 0 : Stopwatch.GetTimestamp();
                _ = await _wal.AppendAsync(payload ?? _payload).ConfigureAwait(false);
                if (samples is not null)
                {
                    samples[client][index] = Stopwatch.GetTimestamp() - start;
                }
            }
        })));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _wal.DisposeAsync().ConfigureAwait(false);
        (_storage as IDisposable)?.Dispose();
        _payload.Dispose();
        Directory.Delete(_path, recursive: true);
    }

    /// <summary>
    /// A fixed-work profile run; setup, warmup and disposal are outside the measured interval.
    /// </summary>
    internal static async Task ProfileAsync(string[] args)
    {
        if (args.Length < 5)
        {
            throw new ArgumentException("Usage: --wal-perf <io-uring|portable> <payload-bytes> <clients> <appends-per-client> [fixed-write-groups] [prealloc=false] [window=<pages>] [budgetMiB=<n>] [fileMiB=<n>] [maxGroups=<n>] [prewarm=<bytes>x<clients>x<per-client>]");
        }

        var ioUring = args[1] switch
        {
            "io-uring" => true,
            "portable" => false,
            _ => throw new ArgumentException("Storage must be io-uring or portable."),
        };
        var payloadSize = int.Parse(args[2], CultureInfo.InvariantCulture);
        var clients = int.Parse(args[3], CultureInfo.InvariantCulture);
        var perClient = int.Parse(args[4], CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clients);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perClient);
        var options = new WalOptions();
        var groups = 0;
        (int Bytes, int Clients, int PerClient)? prewarm = null;
        foreach (var argument in args.Skip(5))
        {
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                groups = int.Parse(argument, CultureInfo.InvariantCulture);
                ArgumentOutOfRangeException.ThrowIfNegative(groups);
                if (groups > 0)
                {
                    options = options with { MinWriteGroupsInFlight = groups, MaxWriteGroupsInFlight = groups };
                }

                continue;
            }

            var value = argument[(separator + 1)..];
            options = argument[..separator] switch
            {
                "prealloc" => options with { PreallocateFiles = bool.Parse(value) },
                "window" => options with { WriteWindowPages = int.Parse(value, CultureInfo.InvariantCulture) },
                "budgetMiB" => options with { PageBufferBudgetBytes = long.Parse(value, CultureInfo.InvariantCulture) << 20 },
                "fileMiB" => options with { FileSize = long.Parse(value, CultureInfo.InvariantCulture) << 20 },
                "maxGroups" => options with { MaxWriteGroupsInFlight = int.Parse(value, CultureInfo.InvariantCulture) },
                "prewarm" => SetPrewarm(value),
                _ => throw new ArgumentException($"Unknown option '{argument}'."),
            };
        }

        options.Validate();

        // A heavy phase before the measured one, so the measurement shows how the log behaves once the load drops.
        WalOptions SetPrewarm(string value)
        {
            var parts = value.Split('x');
            prewarm = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
            return options;
        }

        await using var workload = await CreateAsync(ioUring, payloadSize, options).ConfigureAwait(false);
        if (prewarm is { } heavy)
        {
            using var writer = new ArcBufferWriter();
            writer.Write(new byte[heavy.Bytes]);
            using var heavyPayload = writer.PeekSlice(writer.Length);
            await workload.RunAsync(heavy.Clients, heavy.PerClient, null, heavyPayload).ConfigureAwait(false);
        }
        else
        {
            await workload.RunAsync(clients, Math.Min(1024, perClient), null).ConfigureAwait(false);
        }
        var samples = Enumerable.Range(0, clients).Select(_ => new long[perClient]).ToArray();
        using var metrics = new WalGroupMetrics();
        using var process = Process.GetCurrentProcess();
        var user = process.UserProcessorTime;
        var kernel = process.PrivilegedProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var start = Stopwatch.GetTimestamp();
        metrics.Start();
        await workload.RunAsync(clients, perClient, samples).ConfigureAwait(false);
        metrics.Stop();
        var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
        process.Refresh();
        var userSeconds = (process.UserProcessorTime - user).TotalSeconds;
        var kernelSeconds = (process.PrivilegedProcessorTime - kernel).TotalSeconds;
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var ordered = samples.SelectMany(static values => values).Order().ToArray();
        double Percentile(double fraction) => ordered[(int)((ordered.Length - 1) * fraction)] * 1_000_000.0 / Stopwatch.Frequency;
        Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, double>
        {
            ["payloadBytes"] = payloadSize,
            ["clients"] = clients,
            ["fixedWriteGroups"] = groups,
            ["preallocate"] = options.PreallocateFiles ? 1 : 0,
            ["writeWindowPages"] = options.WriteWindowPages,
            ["pageBufferBudgetMiB"] = options.PageBufferBudgetBytes / (1024.0 * 1024),
            ["fileMiB"] = options.FileSize / (1024.0 * 1024),
            ["maxWriteGroups"] = options.MaxWriteGroupsInFlight,
            ["appends"] = ordered.Length,
            ["seconds"] = elapsed,
            ["appendsPerSecond"] = ordered.Length / elapsed,
            ["payloadMiBPerSecond"] = ordered.Length * (double)payloadSize / elapsed / (1024 * 1024),
            ["p50Microseconds"] = Percentile(0.50),
            ["p95Microseconds"] = Percentile(0.95),
            ["p99Microseconds"] = Percentile(0.99),
            ["cpuSeconds"] = userSeconds + kernelSeconds,
            ["userCpuSeconds"] = userSeconds,
            ["kernelCpuSeconds"] = kernelSeconds,
            ["cpuMicrosecondsPerAppend"] = (userSeconds + kernelSeconds) * 1_000_000 / ordered.Length,
            ["allocatedBytesPerAppend"] = (double)allocated / ordered.Length,
            ["groupsPerSecond"] = metrics.Groups / elapsed,
            ["meanGroupPages"] = metrics.Mean("orleans.durable.storage.wal.write_group.pages"),
            ["meanGroupRecords"] = metrics.Mean("orleans.durable.storage.wal.write_group.records"),
            ["meanGroupMilliseconds"] = metrics.Mean("orleans.durable.storage.wal.write_group.duration"),
            ["maxGroupMilliseconds"] = metrics.Max("orleans.durable.storage.wal.write_group.duration"),
            ["meanGroupConcurrency"] = metrics.Mean("orleans.durable.storage.wal.write_group.concurrency"),
        }));
    }
}

/// <summary>
/// Aggregates the WAL's write-group histograms during a profile's measured interval.
/// </summary>
internal sealed class WalGroupMetrics : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Lock _sync = new();
    private readonly Dictionary<string, (long Count, double Sum, double Max)> _values = [];
    private volatile bool _recording;

    public WalGroupMetrics()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Orleans.Durable.Storage.Wal" && instrument.Name.StartsWith("orleans.durable.storage.wal.write_group.", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Record(instrument.Name, value));
        _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => Record(instrument.Name, value));
        _listener.Start();
    }

    public double Groups => Count("orleans.durable.storage.wal.write_group.duration");

    public void Start() => _recording = true;

    public void Stop() => _recording = false;

    public double Mean(string name)
    {
        lock (_sync)
        {
            return _values.TryGetValue(name, out var value) && value.Count > 0 ? value.Sum / value.Count : 0;
        }
    }

    public double Max(string name)
    {
        lock (_sync)
        {
            return _values.TryGetValue(name, out var value) ? value.Max : 0;
        }
    }

    public void Dispose() => _listener.Dispose();

    private double Count(string name)
    {
        lock (_sync)
        {
            return _values.TryGetValue(name, out var value) ? value.Count : 0;
        }
    }

    private void Record(string name, double value)
    {
        if (!_recording)
        {
            return;
        }

        lock (_sync)
        {
            var current = _values.GetValueOrDefault(name);
            _values[name] = (current.Count + 1, current.Sum + value, Math.Max(current.Max, value));
        }
    }
}
