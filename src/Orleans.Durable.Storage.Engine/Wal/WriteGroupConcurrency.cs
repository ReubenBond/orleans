namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// Chooses how many write groups may be in flight, between <see cref="WalOptions.MinWriteGroupsInFlight"/> and
/// <see cref="WalOptions.MaxWriteGroupsInFlight"/>, from how long the device takes to make them durable.
/// </summary>
/// <remarks>
/// <para>
/// On a device which runs durable writes in parallel, a group submitted while others are in flight is durable about as
/// soon as a lone group would be, so submitting it at once cuts its records' latency. On a device which runs them one
/// at a time, it waits behind the groups ahead of it, finishing no sooner than if it had waited for a free slot, and
/// spends a whole durable write on the records it holds; records accepted while the device is busy would instead share
/// the next write. The limit is therefore the maximum while groups submitted alongside others take at most
/// <see cref="ParallelRatio"/> times as long as lone groups, and the minimum otherwise.
/// </para>
/// <para>
/// Below the maximum, every <see cref="ProbeInterval"/>th group may be submitted past the limit, so the concurrent
/// timing stays current and the limit rises when the device begins to run writes in parallel. The caller serializes
/// every call.
/// </para>
/// </remarks>
internal sealed class WriteGroupConcurrency
{
    /// <summary>
    /// The groups submitted within the limit between two submitted past it.
    /// </summary>
    internal const int ProbeInterval = 64;

    /// <summary>
    /// The largest ratio of concurrent to lone durations at which the device counts as running writes in parallel. One
    /// group submitted while another is midway through its write takes about 1.5 times as long on a device which
    /// runs writes one at a time.
    /// </summary>
    internal const double ParallelRatio = 1.25;

    // Weights each new duration by an eighth, so the averages follow the device within tens of groups.
    private const double Weight = 0.125;

    private readonly int _minimum;
    private readonly int _maximum;
    private double _lone = double.NaN;
    private double _concurrent = double.NaN;
    private int _sinceProbe;

    internal WriteGroupConcurrency(int minimum, int maximum)
    {
        _minimum = minimum;
        _maximum = maximum;
        Limit = minimum;
    }

    /// <summary>
    /// Gets the number of groups which may be in flight.
    /// </summary>
    internal int Limit { get; private set; }

    /// <summary>
    /// Gets how many more groups may be submitted while <paramref name="inFlight"/> are in flight.
    /// </summary>
    internal int Slots(int inFlight)
    {
        if (inFlight < Limit)
        {
            return Limit - inFlight;
        }

        return inFlight < _maximum && _sinceProbe >= ProbeInterval ? 1 : 0;
    }

    /// <summary>
    /// Records that a group was submitted, making <paramref name="inFlight"/> groups in flight.
    /// </summary>
    internal void Submitted(int inFlight) => _sinceProbe = inFlight > Limit ? 0 : _sinceProbe + 1;

    /// <summary>
    /// Records how long a group submitted with <paramref name="concurrency"/> groups in flight, itself included, took
    /// to become durable, and updates the limit.
    /// </summary>
    internal void Completed(int concurrency, TimeSpan duration)
    {
        var milliseconds = duration.TotalMilliseconds;
        if (concurrency == 1)
        {
            _lone = Average(_lone, milliseconds);
        }
        else
        {
            _concurrent = Average(_concurrent, milliseconds);
        }

        if (!double.IsNaN(_lone) && !double.IsNaN(_concurrent))
        {
            Limit = _concurrent <= ParallelRatio * _lone ? _maximum : _minimum;
        }
    }

    private static double Average(double average, double value) =>
        double.IsNaN(average) ? value : average + Weight * (value - average);
}
