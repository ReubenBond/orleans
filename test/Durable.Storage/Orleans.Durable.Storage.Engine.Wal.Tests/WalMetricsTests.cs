using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

[CollectionDefinition(WalMetricsTestGroup.Name, DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit collection definitions must be public.
public sealed class WalMetricsTestGroup
{
    public const string Name = "WriteAheadLog metrics tests";

    private WalMetricsTestGroup()
    {
    }
}
#pragma warning restore CA1515

[Collection(WalMetricsTestGroup.Name)]
public sealed class WalMetricsTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const string GroupDurationName = "orleans.durable.storage.wal.write_group.duration";
    private const string GroupRecordsName = "orleans.durable.storage.wal.write_group.records";
    private const string GroupPagesName = "orleans.durable.storage.wal.write_group.pages";
    private const string GroupConcurrencyName = "orleans.durable.storage.wal.write_group.concurrency";
    private const string RecordDurationName = "orleans.durable.storage.wal.record.duration";

    /// <summary>
    /// With one write slot, records accepted while the slot is busy share the next group: each group's duration runs
    /// from its submission, and each record's from its acceptance, on the log's clock.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task GroupsAndRecordsAreTimedFromSubmissionAndAcceptance()
    {
        using var metrics = new WalMetricListener();
        var time = new FakeTimeProvider();
        var storage = new SimulatedWalStorage { HoldWrites = true };
        var options = Options(pageSize: 512, window: 64, groups: 1) with { TimeProvider = time, PreallocateFiles = false };
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken);

        var blocker = AppendAsync(wal, Bytes(100, 1)).AsTask();
        await WalFormatTests.WaitForPendingAsync(storage, 1);
        time.Advance(TimeSpan.FromMilliseconds(3));
        var first = AppendAsync(wal, Bytes(50, 2)).AsTask();
        var second = AppendAsync(wal, Bytes(50, 3)).AsTask();
        time.Advance(TimeSpan.FromMilliseconds(4));

        Assert.True(storage.CompleteWrite());
        _ = await blocker;
        while (storage.PendingWriteCount == 0)
        {
            await PauseAsync();
        }

        time.Advance(TimeSpan.FromMilliseconds(5));
        Assert.True(storage.CompleteWrite());
        _ = await Task.WhenAll(first, second);

        Assert.Equal([7D, 5D], metrics.Values(GroupDurationName));
        Assert.Equal([1D, 2D], metrics.Values(GroupRecordsName));
        Assert.Equal([1D, 1D], metrics.Values(GroupPagesName));
        Assert.Equal([1D, 1D], metrics.Values(GroupConcurrencyName));
        Assert.Equal([7D, 9D, 9D], metrics.Values(RecordDurationName));
    }

    /// <summary>
    /// A record spanning pages is counted once in its group, which counts each page it writes.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ARecordSpanningPagesIsOneRecordOfAMultiPageGroup()
    {
        using var metrics = new WalMetricListener();
        var storage = new SimulatedWalStorage();
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);

        // 9,000 bytes span three 4 KiB pages.
        _ = await AppendAsync(wal, Bytes(9000, 7));

        Assert.Equal([1D], metrics.Values(GroupRecordsName));
        Assert.Equal([3D], metrics.Values(GroupPagesName));
        Assert.Single(metrics.Values(RecordDurationName));
    }

    private sealed class WalMetricListener : IDisposable
    {
        private readonly ConcurrentQueue<(string Name, double Value)> _measurements = [];
        private readonly MeterListener _listener;

        internal WalMetricListener()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = static (instrument, listener) =>
                {
                    if (string.Equals(instrument.Meter.Name, WalMetrics.MeterName, StringComparison.Ordinal))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => _measurements.Enqueue((instrument.Name, value)));
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => _measurements.Enqueue((instrument.Name, value)));
            _listener.Start();
        }

        internal double[] Values(string name) =>
            [.. _measurements.Where(measurement => string.Equals(measurement.Name, name, StringComparison.Ordinal)).Select(static measurement => measurement.Value)];

        public void Dispose() => _listener.Dispose();
    }
}
