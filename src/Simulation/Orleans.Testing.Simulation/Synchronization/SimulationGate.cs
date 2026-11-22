using System.Diagnostics;

namespace Orleans.Testing.Simulation;

/// <summary>
/// <para>
/// A reusable, level-triggered rendezvous gate for simulation code: while closed, callers of
/// <see cref="WaitAsync"/> are suspended until the gate opens; while open, waiters pass through
/// immediately. Unlike <see cref="SimulationLatch"/>, a gate can be closed again after being
/// opened, and reopened any number of times - it models a boolean condition ("is it safe to
/// proceed yet?") rather than a one-shot event.
/// </para>
/// <para>
/// Replaces the common pattern of hand-rolling a <see cref="TaskCompletionSource"/> (or a list of
/// them) to let simulated work wait for a signal. Completions are always dispatched through the
/// supplied <see cref="SimulationSchedulerLane"/> - never executed inline from <see cref="Open"/> and
/// never via a real-time wait or thread-pool callback - so waiter continuations run at a
/// deterministic point in the simulation's schedule, in the order the waiters registered.
/// </para>
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class SimulationGate
{
    private readonly SimulationSchedulerLane _queue;
    private readonly object _sync = new();
    private readonly List<SimulationRendezvousSupport.Waiter> _waiters = [];
    private bool _isOpen;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationGate"/> class.
    /// </summary>
    /// <param name="queue">The queue used to dispatch waiter completions deterministically.</param>
    /// <param name="isOpen">Whether the gate starts open. Defaults to <see langword="false"/> (closed).</param>
    /// <param name="name">An optional name for diagnostics (for example, in a debugger).</param>
    public SimulationGate(SimulationSchedulerLane queue, bool isOpen = false, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
        _isOpen = isOpen;
        Name = name;
    }

    /// <summary>
    /// Gets the optional name of this gate, for diagnostics.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Gets a value indicating whether the gate is currently open.
    /// </summary>
    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return _isOpen;
            }
        }
    }

    /// <summary>
    /// Opens the gate, releasing every waiter currently registered via <see cref="WaitAsync"/>.
    /// Once open, subsequent calls to <see cref="WaitAsync"/> complete immediately, until the gate
    /// is closed again. Calling this while already open has no effect (idempotent).
    /// </summary>
    public void Open()
    {
        SimulationRendezvousSupport.Waiter[] waiters;
        lock (_sync)
        {
            if (_isOpen)
            {
                return;
            }

            _isOpen = true;
            waiters = SimulationRendezvousSupport.TakeAllForRelease(_waiters);
        }

        SimulationRendezvousSupport.ScheduleReleases(_queue, waiters);
    }

    /// <summary>
    /// Closes the gate. Callers of <see cref="WaitAsync"/> after this point suspend again until
    /// the next <see cref="Open"/>. Does not affect waiters already released by a previous
    /// <see cref="Open"/> call. Calling this while already closed has no effect.
    /// </summary>
    public void Close()
    {
        lock (_sync)
        {
            _isOpen = false;
        }
    }

    /// <summary>
    /// Waits for the gate to be open. If the gate is already open, completes immediately
    /// (synchronously, as an already-completed task) without going through the queue.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token whose cancellation removes this wait and transitions the returned task to the
    /// canceled state. Observed synchronously, per Clockwork's determinism requirements: if
    /// already canceled at the time of the call, returns a canceled task without registering a waiter.
    /// </param>
    /// <returns>A task that completes when the gate opens, or is canceled per <paramref name="cancellationToken"/>.</returns>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            if (_isOpen)
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

    private string DebuggerDisplay => $"SimulationGate({Name ?? "unnamed"}, IsOpen={IsOpen})";
}
