using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime;

internal static class ApplicationRequestInstruments
{
    internal static Counter<long> TimedOutRequestsCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.APP_REQUESTS_TIMED_OUT);

    private static readonly long[] AppRequestsLatencyHistogramBuckets = new long[] { 1, 2, 4, 6, 8, 10, 50, 100, 200, 400, 800, 1_000, 1_500, 2_000, 5_000, 10_000, 15_000 };
    private static readonly HistogramAggregator AppRequestsLatencyHistogramAggregator = new(AppRequestsLatencyHistogramBuckets, Array.Empty<KeyValuePair<string, object>>(), value => new ("duration", $"{value}ms"));
    private static readonly ObservableCounter<long> AppRequestsLatencyHistogramBucket = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM + "-bucket", AppRequestsLatencyHistogramAggregator.CollectBuckets);
    private static readonly ObservableCounter<long> AppRequestsLatencyHistogramCount = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM + "-count", AppRequestsLatencyHistogramAggregator.CollectCount);
    private static readonly ObservableCounter<long> AppRequestsLatencyHistogramSum = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM + "-sum", AppRequestsLatencyHistogramAggregator.CollectSum);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void OnAppRequestsEnd(ref CoarseStopwatch stopwatch)
    {
        if (AppRequestsLatencyHistogramSum.Enabled)
        {
            stopwatch.Stop();
            AppRequestsLatencyHistogramAggregator.Record((long)stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    internal static void OnAppRequestsTimedOut()
    {
        TimedOutRequestsCounter.Add(1);
    }
}
