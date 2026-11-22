using System.Globalization;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

internal interface ISimulationTimerEntry
{
    TimeSpan DueTime { get; }

    long Sequence { get; }

    bool IsCanceled { get; }
}

/// <summary>
/// A single pending virtual-time timeout owned by the scheduler's controlled virtual clock:
/// the due instant (in virtual time, as an offset from simulation start), a deterministic sequence
/// for tie-breaking coincident timeouts, and the waiter it will time out. Firing is driven purely by
/// virtual-time advancement - never by a real-time timer - so a timeout is a modeled, replayable
/// event, not wall-clock behavior.
/// </summary>
internal sealed class SimulationResourceTimeoutRegistration : ISimulationTimerEntry
{
    internal SimulationResourceTimeoutRegistration(TimeSpan dueTime, long sequence, SimulationResourceWaiter waiter)
    {
        DueTime = dueTime;
        Sequence = sequence;
        Waiter = waiter;
    }

    /// <summary>Gets the virtual-time instant (offset from start) at which this timeout is due.</summary>
    public TimeSpan DueTime { get; }

    /// <summary>
    /// Gets the deterministic, strictly increasing sequence used to break ties between timeouts due
    /// at the same virtual instant, so timeout firing order is a stable function of registration
    /// order.
    /// </summary>
    public long Sequence { get; }

    /// <summary>Gets the waiter this timeout resolves when it fires.</summary>
    public SimulationResourceWaiter Waiter { get; }

    /// <summary>
    /// Gets or sets a value indicating whether this registration has been canceled (its waiter was
    /// resolved by a signal or cancellation first). A canceled registration is skipped when the
    /// clock fires due timeouts, and eventually removed from the pending set.
    /// </summary>
    public bool IsCanceled { get; set; }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"timeout@{DueTime} seq={Sequence} -> {Waiter.Operation.Id}{(IsCanceled ? " (canceled)" : string.Empty)}");
}

internal sealed class SimulationTimerRegistration(
    TimeSpan dueTime,
    long sequence,
    Action? onElapsed,
    string? diagnosticKind,
    SimulationNodeIdentity? node) : ISimulationTimerEntry, ISimulationTimer
{
    private Action? _onElapsed = onElapsed;
    private int _state;

    public TimeSpan DueTime { get; } = dueTime;

    public long Sequence { get; } = sequence;

    public string? DiagnosticKind { get; } = diagnosticKind;

    public SimulationNodeIdentity? Node { get; } = node;

    public bool IsCanceled => Volatile.Read(ref _state) == 2;

    public bool IsElapsed => Volatile.Read(ref _state) == 1;

    public void Cancel()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
        {
            Interlocked.Exchange(ref _onElapsed, null);
        }
    }

    internal bool TryClaimElapsed(out Action? callback)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            callback = null;
            return false;
        }

        callback = Interlocked.Exchange(ref _onElapsed, null);
        return true;
    }
}
