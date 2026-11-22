namespace Orleans.Durable.Storage.Engine.Archive.Logs.Control;

/// <summary>
/// Paces a node's drain requests to a rate (archive-logs.md §8.6): each call is admitted at the later of now and the end
/// of the requests admitted before it, so admissions keep within the rate over any window, in arrival order. The rate may
/// be reassigned at any time, and applies to the calls admitted after; a <see cref="Source"/>, when set, is read at each
/// admission and charge instead, so a rate with a deadline applies it on the path which uses the rate.
/// </summary>
internal sealed class RequestPacer
{
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();
    private double _rate;
    private long _next;

    /// <summary>Initializes a new instance of the <see cref="RequestPacer"/> class.</summary>
    /// <param name="time">The clock of the waits.</param>
    /// <param name="requestsPerSecond">The rate; <see cref="double.PositiveInfinity"/> admits every call at once.</param>
    public RequestPacer(TimeProvider time, double requestsPerSecond)
    {
        _time = time;
        RequestsPerSecond = requestsPerSecond;
    }

    /// <summary>
    /// Gets or sets the rate, positive; <see cref="double.PositiveInfinity"/> admits every call at once. With a
    /// <see cref="Source"/>, gets the source's rate now.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not positive.</exception>
    public double RequestsPerSecond
    {
        get
        {
            if (Rate() is { } source)
            {
                return source;
            }

            lock (_sync)
            {
                return _rate;
            }
        }

        set
        {
            ArchiveLogOptions.CheckRequestsPerSecond(value);
            lock (_sync)
            {
                _rate = value;
            }
        }
    }

    /// <summary>
    /// Gets the source of the rate, read at each admission and charge in place of <see cref="RequestsPerSecond"/>, or
    /// <see langword="null"/>. It must be cheap, safe from any thread, and answer a positive rate.
    /// </summary>
    public Func<double>? Source { get; set; }

    /// <summary>Waits until <paramref name="requests"/> more requests keep within the rate.</summary>
    /// <exception cref="OperationCanceledException">The wait was cancelled; the requests stay counted.</exception>
    public Task WaitAsync(int requests, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requests);
        var source = Rate();
        TimeSpan delay;
        lock (_sync)
        {
            var rate = source ?? _rate;
            if (double.IsPositiveInfinity(rate))
            {
                return Task.CompletedTask;
            }

            var now = _time.GetTimestamp();
            var start = Math.Max(now, _next);
            _next = start + Span(requests, rate);
            delay = _time.GetElapsedTime(now, start);
        }

        return delay > TimeSpan.Zero ? Task.Delay(delay, _time, cancellationToken) : Task.CompletedTask;
    }

    /// <summary>
    /// Counts <paramref name="requests"/> made beyond those a wait admitted, as a write's retries and properties reads are,
    /// without waiting: the calls admitted after them wait the longer.
    /// </summary>
    public void Charge(int requests)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requests);
        var source = Rate();
        lock (_sync)
        {
            var rate = source ?? _rate;
            if (requests > 0 && !double.IsPositiveInfinity(rate))
            {
                _next = Math.Max(_time.GetTimestamp(), _next) + Span(requests, rate);
            }
        }
    }

    /// <summary>Returns the timestamps <paramref name="requests"/> take at <paramref name="rate"/>.</summary>
    private long Span(int requests, double rate) => (long)Math.Ceiling(requests * _time.TimestampFrequency / rate);

    /// <summary>Reads the <see cref="Source"/>'s rate, checked, outside the lock, or <see langword="null"/> without one.</summary>
    private double? Rate()
    {
        if (Source is not { } source)
        {
            return null;
        }

        var rate = source();
        ArchiveLogOptions.CheckRequestsPerSecond(rate);
        return rate;
    }
}
