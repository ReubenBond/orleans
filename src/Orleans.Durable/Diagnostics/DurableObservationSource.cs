using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Internal;

namespace Orleans.Durable.Diagnostics;

/// <summary>Provides bounded, asynchronous operational observation of Orleans Durable.</summary>
/// <remarks>
/// Resolve the silo singleton from dependency injection. Each subscription has an independent bounded FIFO and
/// worker. Subscriber exceptions are logged and delivery continues. A full buffer drops the incoming observation.
/// Subscribers run away from activation schedulers and can perform synchronous export without holding an activation.
/// Dispose subscriptions to stop delivery; a callback already running may finish after disposal.
/// </remarks>
public sealed partial class DurableObservationSource : IObservable<DurableObservation>, IDisposable
{
    private readonly ILogger _logger;
    private readonly Meter _meter;
    private readonly object _lock = new();
    private Subscription[] _subscriptions = [];
    private long _droppedCount;
    private bool _disposed;

    /// <summary>Gets the default maximum queued observations per subscriber.</summary>
    public const int DefaultCapacity = 256;

    /// <summary>Gets the meter name for operational observation delivery metrics.</summary>
    public const string MeterName = "Orleans.Durable.Observations";

    /// <summary>Creates the silo's operational observation source.</summary>
    /// <param name="logger">The logger for subscriber failures and delivery overflow.</param>
    public DurableObservationSource(ILogger<DurableObservationSource> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _meter = new(MeterName);
        _meter.CreateObservableCounter("orleans.durable.observations.dropped", () => DroppedCount,
            unit: "{observation}", description: "Observations lost to producer or subscriber buffer overflow.");
    }

    /// <summary>Gets the total observations dropped by producer and subscriber buffers on this silo.</summary>
    /// <remarks>Each subscriber's dropped delivery counts separately. Read this counter when reconciling with durable state.</remarks>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    internal bool IsEnabled => Volatile.Read(ref _subscriptions).Length > 0;

    /// <summary>Subscribes using <see cref="DefaultCapacity"/> queued observations.</summary>
    public IDisposable Subscribe(IObserver<DurableObservation> observer) => Subscribe(observer, DefaultCapacity);

    /// <summary>Subscribes with an independent, bounded delivery buffer.</summary>
    /// <param name="observer">The operational exporter.</param>
    /// <param name="capacity">The maximum number of queued observations, from 1 through 65,536.</param>
    /// <returns>The subscription lifetime.</returns>
    public IDisposable Subscribe(IObserver<DurableObservation> observer, int capacity)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 65_536);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = new Subscription(this, observer, capacity);
            Volatile.Write(ref _subscriptions, [.. _subscriptions, subscription]);
            subscription.Start();
            return subscription;
        }
    }

    internal void Publish(DurableObservation observation)
    {
        foreach (var subscription in Volatile.Read(ref _subscriptions))
        {
            subscription.Enqueue(observation);
        }
    }

    internal void ReportDropped(long count) => Interlocked.Add(ref _droppedCount, count);

    private void Remove(Subscription subscription)
    {
        lock (_lock)
        {
            var index = Array.IndexOf(_subscriptions, subscription);
            if (index < 0) return;
            var remaining = new Subscription[_subscriptions.Length - 1];
            Array.Copy(_subscriptions, 0, remaining, 0, index);
            Array.Copy(_subscriptions, index + 1, remaining, index, remaining.Length - index);
            Volatile.Write(ref _subscriptions, remaining);
        }
    }

    /// <summary>Stops all subscriptions. Workers release their queues when their current callback returns.</summary>
    public void Dispose()
    {
        Subscription[] subscriptions;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            subscriptions = _subscriptions;
            Volatile.Write(ref _subscriptions, []);
        }

        foreach (var subscription in subscriptions) subscription.Dispose();
        _meter.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Durable operational subscriber failed. Delivery continues.")]
    private static partial void LogSubscriberFailure(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Durable operational subscriber buffer overflow dropped {Count} observations. Reconcile with durable state.")]
    private static partial void LogOverflow(ILogger logger, long count);

    private sealed class Subscription(DurableObservationSource source, IObserver<DurableObservation> observer, int capacity) : IDisposable
    {
        private readonly Channel<DurableObservation> _channel = Channel.CreateBounded<DurableObservation>(
            new BoundedChannelOptions(capacity) { SingleReader = true, AllowSynchronousContinuations = false });
        private long _dropped;
        private int _disposed;

        public void Start()
        {
            using var suppress = new ExecutionContextSuppressor();
            _ = Task.Run(DeliverAsync);
        }

        public void Enqueue(DurableObservation observation)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!_channel.Writer.TryWrite(observation) && Volatile.Read(ref _disposed) == 0)
            {
                source.ReportDropped(1);
                Interlocked.Increment(ref _dropped);
            }
        }

        private async Task DeliverAsync()
        {
            await foreach (var observation in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref _disposed) != 0) break;
                try
                {
                    var dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0) LogOverflow(source._logger, dropped);
                    observer.OnNext(observation);
                }
                catch (Exception exception)
                {
                    // This is the application callback boundary, isolated separately for each subscriber.
                    LogSubscriberFailure(source._logger, exception);
                }
            }

            while (_channel.Reader.TryRead(out _)) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            source.Remove(this);
            _channel.Writer.TryComplete();
        }
    }
}
