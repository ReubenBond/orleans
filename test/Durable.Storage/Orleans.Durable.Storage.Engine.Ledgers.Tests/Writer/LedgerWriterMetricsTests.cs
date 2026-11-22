using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

#pragma warning disable CA2000 // Each replica double passes to the primary's Ledger, which owns and disposes it.

[CollectionDefinition(LedgerWriterMetricsTestGroup.Name, DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit collection definitions must be public.
public sealed class LedgerWriterMetricsTestGroup
{
    public const string Name = "Ledger writer metrics tests";

    private LedgerWriterMetricsTestGroup()
    {
    }
}
#pragma warning restore CA1515

[Collection(LedgerWriterMetricsTestGroup.Name)]
public sealed class LedgerWriterMetricsTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const string AcquireDurationName = "orleans.durable.storage.ledger.append.sequencer.acquire.duration";
    private const string CompletionDurationName = "orleans.durable.storage.ledger.append.sequencer.complete.duration";
    private const string OccupancyName = "orleans.durable.storage.ledger.append.sequencer.occupancy";
    private const string WriteDurationName = "orleans.durable.storage.ledger.append.payload.write.duration";
    private const string HedgesName = "orleans.durable.storage.ledger.read.hedges";
    private const string HedgeBudgetExhaustedName = "orleans.durable.storage.ledger.read.hedge_budget_exhausted";

    /// <summary>
    /// The <c>Orleans.Durable.Storage.Engine.Ledgers</c> meter publishes exactly the append path's four instruments and the read path's two, each
    /// with its name, unit, description, and kind (ledger-protocol.md §8.5).
    /// </summary>
    [Fact]
    public void TheLedgersMeterPublishesItsAppendAndReadInstruments()
    {
        // A writer's first append and a decided read's first hedge create the instruments. Both are created before the
        // listener starts, which then sees every instrument of the meter whichever tests ran first in the process.
        RuntimeHelpers.RunClassConstructor(typeof(LedgerWriterMetrics).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(LedgerReadMetrics).TypeHandle);
        using var metrics = new LedgerWriterMetricListener();

        var instruments = metrics.Instruments.OrderBy(static instrument => instrument.Name, StringComparer.Ordinal).ToArray();
        Assert.Collection(
            instruments,
            instrument => AssertInstrument(
                instrument,
                WriteDurationName,
                "ms",
                "Time spent writing one batch to a ledger's replicas.",
                typeof(Histogram<double>)),
            instrument => AssertInstrument(
                instrument,
                AcquireDurationName,
                "ms",
                "Time spent acquiring a ledger writer's sequencer slot.",
                typeof(Histogram<double>)),
            instrument => AssertInstrument(
                instrument,
                CompletionDurationName,
                "ms",
                "Time spent completing a ledger writer's sequencer slot after its batch is durable.",
                typeof(Histogram<double>)),
            instrument => AssertInstrument(
                instrument,
                OccupancyName,
                "{slot}",
                "The number of acquired ledger writer sequencer slots which have not completed.",
                typeof(UpDownCounter<long>)),
            instrument => AssertInstrument(
                instrument,
                HedgeBudgetExhaustedName,
                "{read}",
                "Reads below a ledger's known contiguous tail which waited on a slow replica because the hedge budget was empty.",
                typeof(Counter<long>)),
            instrument => AssertInstrument(
                instrument,
                HedgesName,
                "{read}",
                "Replica reads a read below a ledger's known contiguous tail sent to hedge a slow replica.",
                typeof(Counter<long>)));
    }

    [Fact]
    public async Task TimingsResetPerAppendAndRemainIndependentAcrossInstances()
    {
        using var metrics = new LedgerWriterMetricListener();
        var firstTimeProvider = new FakeTimeProvider();
        var acquireDelay = TimeSpan.FromMilliseconds(2);
        var writeDelay = TimeSpan.FromMilliseconds(3);
        var completionDelay = TimeSpan.FromMilliseconds(5);
        await using var firstLog = CreateTimedLog(
            firstTimeProvider,
            () => acquireDelay,
            () => writeDelay,
            () => completionDelay);
        using var payload = WriterTestData.CreatePayload("value"u8);

        _ = await firstLog.AppendAsync(payload, TestContext.Current.CancellationToken);
        acquireDelay = TimeSpan.FromMilliseconds(7);
        writeDelay = TimeSpan.FromMilliseconds(11);
        completionDelay = TimeSpan.FromMilliseconds(13);
        _ = await firstLog.AppendAsync(payload, TestContext.Current.CancellationToken);

        var secondTimeProvider = new FakeTimeProvider();
        await using var secondLog = CreateTimedLog(
            secondTimeProvider,
            static () => TimeSpan.FromMilliseconds(17),
            static () => TimeSpan.FromMilliseconds(19),
            static () => TimeSpan.FromMilliseconds(23));
        _ = await secondLog.AppendAsync(payload, TestContext.Current.CancellationToken);

        Assert.Equal([2D, 7D, 17D], metrics.Values(AcquireDurationName));
        Assert.Equal([3D, 11D, 19D], metrics.Values(WriteDurationName));
        Assert.Equal([5D, 13D, 23D], metrics.Values(CompletionDurationName));
        Assert.Equal([1D, -1D, 1D, -1D, 1D, -1D], metrics.Values(OccupancyName));
    }

    [Fact]
    public async Task SealedAppendBalancesOccupancyAndSkipsCompletionTiming()
    {
        using var metrics = new LedgerWriterMetricListener();
        await using var log = WriterTestData.CreateLog(
            new DelegatingLedgerReplica
            {
                Write = static (_, _, _, _, _) => ValueTask.FromResult(ReplicaAnswers.Sealed),
            },
            windowSize: 1);
        using var payload = WriterTestData.CreatePayload("value"u8);

        var result = await log.AppendAsync(payload, TestContext.Current.CancellationToken);

        Assert.Equal(WriterTestData.Sealed(0), result);
        Assert.Equal([1D, -1D], metrics.Values(OccupancyName));
        Assert.Single(metrics.Values(AcquireDurationName));
        Assert.Single(metrics.Values(WriteDurationName));
        Assert.Empty(metrics.Values(CompletionDurationName));
    }

    /// <summary>
    /// Cancellation stops the caller waiting, never the write (ledger-protocol.md §2.4): the slot stays occupied until its
    /// write lands and completes.
    /// </summary>
    [Fact]
    public async Task CancelledWaitDoesNotLeakOccupancy()
    {
        using var metrics = new LedgerWriterMetricListener();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var writeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writeRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var log = WriterTestData.CreateLog(
            new DelegatingLedgerReplica
            {
                Write = async (_, _, _, _, cancellationToken) =>
                {
                    _ = writeStarted.TrySetResult();
                    await writeRelease.Task.WaitAsync(cancellationToken);
                    return ReplicaAnswers.Written;
                },
            },
            windowSize: 1);
        using var payload = WriterTestData.CreatePayload("value"u8);

        var append = log.AppendAsync(payload, cancellation.Token).AsTask();
        await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await append);

        Assert.Equal([1D], metrics.Values(OccupancyName));
        _ = writeRelease.TrySetResult();
        await WriterTestData.WaitUntilAsync(() => metrics.Values(OccupancyName).Length == 2);
        Assert.Equal([1D, -1D], metrics.Values(OccupancyName));
        Assert.Single(metrics.Values(AcquireDurationName));
        Assert.Single(metrics.Values(WriteDurationName));
        Assert.Single(metrics.Values(CompletionDurationName));
    }

    [Fact]
    public async Task FaultedWriteDoesNotLeakOccupancy()
    {
        using var metrics = new LedgerWriterMetricListener();
        await using var log = WriterTestData.CreateLog(
            new DelegatingLedgerReplica
            {
                Write = static (address, _, _, _, _) =>
                    ValueTask.FromException<ReplicaWriteOutcome>(new LedgerReplicaCorruptException(address, "corrupt")),
            },
            windowSize: 1);
        using var payload = WriterTestData.CreatePayload("value"u8);

        Assert.Equal(WriterTestData.Unresolved(0), await log.AppendAsync(payload, TestContext.Current.CancellationToken));

        Assert.Equal([1D, -1D], metrics.Values(OccupancyName));
        Assert.Single(metrics.Values(AcquireDurationName));
        Assert.Single(metrics.Values(WriteDurationName));
        Assert.Empty(metrics.Values(CompletionDurationName));
    }

    [Fact]
    public async Task FaultedCompletionDoesNotLeakOccupancy()
    {
        using var metrics = new LedgerWriterMetricListener();
        await using var log = WriterTestData.CreateLog(new DelegatingLedgerReplica(), windowSize: 1);
        log.Sequencer.BeforeComplete = static (_, _) => ValueTask.FromException(new IOException("completion failed"));
        using var payload = WriterTestData.CreatePayload("value"u8);

        Assert.Equal(
            WriterTestData.Unresolved(0),
            await log.AppendAsync(payload, TestContext.Current.CancellationToken));

        Assert.Equal([1D, -1D], metrics.Values(OccupancyName));
        Assert.Single(metrics.Values(AcquireDurationName));
        Assert.Single(metrics.Values(WriteDurationName));
        Assert.Single(metrics.Values(CompletionDurationName));
    }

    private static void AssertInstrument(
        Instrument instrument,
        string name,
        string unit,
        string description,
        Type instrumentType)
    {
        Assert.Equal(LedgerWriterMetrics.MeterName, instrument.Meter.Name);
        Assert.Equal(name, instrument.Name);
        Assert.Equal(unit, instrument.Unit);
        Assert.Equal(description, instrument.Description);
        Assert.Equal(instrumentType, instrument.GetType());
    }

    private static PrimaryLedger CreateTimedLog(
        FakeTimeProvider timeProvider,
        Func<TimeSpan> acquireDelay,
        Func<TimeSpan> writeDelay,
        Func<TimeSpan> completionDelay)
    {
        var ledgerReplica = new DelegatingLedgerReplica
        {
            Write = (_, _, _, _, _) =>
            {
                timeProvider.Advance(writeDelay());
                return ValueTask.FromResult(ReplicaAnswers.Written);
            },
        };
        var log = WriterTestData.CreateLog(ledgerReplica, windowSize: 1, timeProvider);
        log.Sequencer.BeforeAcquire = _ =>
        {
            timeProvider.Advance(acquireDelay());
            return ValueTask.CompletedTask;
        };
        log.Sequencer.BeforeComplete = (_, _) =>
        {
            timeProvider.Advance(completionDelay());
            return ValueTask.CompletedTask;
        };
        return log;
    }

    /// <summary>
    /// Observes the ledger writer meter. A meter listener sees every writer in the process, so the listener keeps only
    /// the measurements recorded in the execution context of the test which created it: work another test, or an
    /// earlier run in the same test host, left behind records nothing here.
    /// </summary>
    private sealed class LedgerWriterMetricListener : IDisposable
    {
        private static readonly AsyncLocal<LedgerWriterMetricListener?> Owner = new();
        private readonly ConcurrentQueue<Instrument> _instruments = [];
        private readonly ConcurrentQueue<MetricMeasurement> _measurements = [];
        private readonly MeterListener _listener;

        internal LedgerWriterMetricListener()
        {
            Owner.Value = this;
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (string.Equals(
                            instrument.Meter.Name,
                            LedgerWriterMetrics.MeterName,
                            StringComparison.Ordinal))
                    {
                        _instruments.Enqueue(instrument);
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<double>(
                (instrument, value, _, _) => Record(instrument, value));
            _listener.SetMeasurementEventCallback<long>(
                (instrument, value, _, _) => Record(instrument, value));
            _listener.Start();
        }

        internal IReadOnlyCollection<Instrument> Instruments => _instruments;

        internal double[] Values(string instrumentName) =>
            [
                .. _measurements
                    .Where(measurement => string.Equals(
                        measurement.InstrumentName,
                        instrumentName,
                        StringComparison.Ordinal))
                    .Select(static measurement => measurement.Value),
            ];

        public void Dispose()
        {
            _listener.Dispose();
            Owner.Value = null;
        }

        private void Record(Instrument instrument, double value)
        {
            if (ReferenceEquals(Owner.Value, this))
            {
                _measurements.Enqueue(new MetricMeasurement(instrument.Name, value));
            }
        }
    }

    private sealed record MetricMeasurement(string InstrumentName, double Value);
}
