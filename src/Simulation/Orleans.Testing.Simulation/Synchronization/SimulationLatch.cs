using System.Diagnostics;
using System.Globalization;

namespace Orleans.Testing.Simulation;

/// <summary>
/// <para>
/// A one-shot countdown rendezvous primitive for simulation code, modeled on
/// <see cref="System.Threading.CountdownEvent"/>: created with an initial count, decremented by
/// <see cref="Signal(int)"/>, and every caller of <see cref="WaitAsync"/> is released exactly
/// once, when the count reaches zero. Unlike <see cref="SimulationGate"/>, a latch cannot be
/// reset or re-armed - once signaled to zero, it stays signaled.
/// </para>
/// <para>
/// Completions are dispatched through the supplied <see cref="SimulationSchedulerLane"/>, never
/// inline and never via a real-time wait or thread-pool callback, so release order is
/// deterministic and matches the order in which callers started waiting.
/// </para>
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class SimulationLatch
{
    private readonly SimulationSchedulerLane _queue;
    private readonly object _sync = new();
    private readonly List<SimulationRendezvousSupport.Waiter> _waiters = [];
    private int _remainingCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationLatch"/> class.
    /// </summary>
    /// <param name="queue">The queue used to dispatch waiter completions deterministically.</param>
    /// <param name="initialCount">
    /// The total number of signals (see <see cref="Signal(int)"/>) required before the latch
    /// opens. Must be non-negative; zero creates an already-signaled latch.
    /// </param>
    /// <param name="name">An optional name for diagnostics (for example, in a debugger).</param>
    public SimulationLatch(SimulationSchedulerLane queue, int initialCount, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCount);
        _queue = queue;
        _remainingCount = initialCount;
        Name = name;
    }

    /// <summary>
    /// Gets the optional name of this latch, for diagnostics.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Gets the number of outstanding signals still required before the latch opens. Never negative.
    /// </summary>
    public int RemainingCount
    {
        get
        {
            lock (_sync)
            {
                return _remainingCount;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the latch has counted down to zero and released its waiters.
    /// </summary>
    public bool IsSignaled
    {
        get
        {
            lock (_sync)
            {
                return _remainingCount == 0;
            }
        }
    }

    /// <summary>
    /// Decrements the remaining count by <paramref name="count"/>. When the count reaches zero,
    /// every current waiter of <see cref="WaitAsync"/> is released, and every future call to
    /// <see cref="WaitAsync"/> completes immediately.
    /// </summary>
    /// <param name="count">The number of signals to apply. Must be positive. Defaults to 1.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the latch has already reached zero, or if <paramref name="count"/> exceeds
    /// <see cref="RemainingCount"/> - both indicate a caller miscounted signals, which this
    /// primitive deliberately surfaces instead of silently clamping.
    /// </exception>
    public void Signal(int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        SimulationRendezvousSupport.Waiter[] waiters = [];
        lock (_sync)
        {
            if (_remainingCount == 0)
            {
                throw new InvalidOperationException("The latch has already been fully signaled and cannot be signaled again.");
            }

            if (count > _remainingCount)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Signal count {count} exceeds the remaining count {_remainingCount}."));
            }

            _remainingCount -= count;
            if (_remainingCount == 0)
            {
                waiters = SimulationRendezvousSupport.TakeAllForRelease(_waiters);
            }
        }

        SimulationRendezvousSupport.ScheduleReleases(_queue, waiters);
    }

    /// <summary>
    /// Waits for the latch to reach zero. If it has already reached zero, completes immediately
    /// (synchronously, as an already-completed task) without going through the queue.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token whose cancellation removes this wait and transitions the returned task to the
    /// canceled state. If already canceled at the time of the call, returns a canceled task
    /// without registering a waiter.
    /// </param>
    /// <returns>A task that completes when the latch reaches zero, or is canceled per <paramref name="cancellationToken"/>.</returns>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            if (_remainingCount == 0)
            {
                return Task.CompletedTask;
            }

            return SimulationRendezvousSupport.AddWaiter(_waiters, CancelWaiter, cancellationToken).Task;
        }
    }

    private void CancelWaiter(SimulationRendezvousSupport.Waiter waiter)
    {
        var canceled = false;
        lock (_sync)
        {
            if (waiter.TryCancel())
            {
                canceled = _waiters.Remove(waiter);
            }
        }

        if (canceled)
        {
            waiter.CompleteCancellation();
        }
    }

    private string DebuggerDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"SimulationLatch({Name ?? "unnamed"}, RemainingCount={RemainingCount})");
}
