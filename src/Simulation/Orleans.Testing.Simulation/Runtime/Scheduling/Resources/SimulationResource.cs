using System.Diagnostics;
using System.Globalization;

namespace Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

/// <summary>
/// <para>
/// The reusable, scheduler-owned model of one waitable resource - the substrate every
/// controlled <c>Monitor</c>, <c>SemaphoreSlim</c>, event, wait handle, synchronous <c>Task</c>
/// wait, and timer (controlled synchronization shims) builds on. A resource carries a stable identity
/// (<see cref="Id"/>), a diagnostic <see cref="Kind"/> and <see cref="Name"/>, an optional
/// <see cref="Owner"/> with reentrancy metadata (<see cref="RecursionCount"/>), counting/capacity
/// state (<see cref="CurrentCount"/>/<see cref="MaximumCount"/>), an event-style
/// <see cref="IsSignaled"/> latch, an extensibility <see cref="SpecializationTag"/>, and a
/// deterministic FIFO wait queue.
/// </para>
/// <para>
/// <b>What it is and is not.</b> The resource provides the <em>mechanism</em> shared by all those
/// primitives - a stable identity, ownership/count bookkeeping fields, and an ordered, replayable
/// wait queue - but it deliberately does <em>not</em> hard-code any single primitive's acquire/release
/// policy. A monitor's mutual exclusion, a semaphore's permit counting, an event's manual/auto reset,
/// and a task wait's external completion are all composed on top of these fields by the scheduler and
/// its callers; a primitive that needs behaviour these fields cannot express attaches it via
/// <see cref="SpecializationTag"/> rather than forcing an incorrect one-size-fits-all rule into the
/// core. This is what "keep the abstraction general without pretending all resources share identical
/// semantics" means in practice.
/// </para>
/// <para>
/// <b>Mutation contract.</b> Only the owning <see cref="SimulationScheduler"/> mutates a
/// resource, and only under its lock. Every property here is publicly readable (so diagnostics and
/// deadlock reports can inspect a resource) but only internally settable, exactly mirroring the
/// scheduler-owns-all-transitions discipline of <see cref="SimulationOperation"/>.
/// </para>
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class SimulationResource
{
    // FIFO by construction: waiters are appended in strictly increasing enqueue sequence and removed
    // by reference, so iteration order is always deterministic enqueue order.
    private readonly List<SimulationResourceWaiter> _waiters = [];

    internal SimulationResource(SimulationScheduler scheduler, SimulationResourceId id, SimulationResourceKind kind, string name)
    {
        Scheduler = scheduler;
        Id = id;
        Kind = kind;
        Name = name;
    }

    /// <summary>Gets the scheduler that owns this resource.</summary>
    internal SimulationScheduler Scheduler { get; }

    /// <summary>Gets this resource's stable, scheduler-assigned identity.</summary>
    public SimulationResourceId Id { get; }

    /// <summary>Gets the diagnostic classification of the primitive this resource models.</summary>
    public SimulationResourceKind Kind { get; }

    /// <summary>
    /// Gets a short, stable, human-readable name for this resource, used in deterministic
    /// diagnostics and deadlock reports. Never embeds non-deterministic data.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the operation that currently owns this resource (holds the monitor, or the operation
    /// expected to complete a task wait), or <see langword="null"/> for an unowned resource
    /// (available monitor, semaphore, or an ownerless event). Used both by acquire/release policy and
    /// by wait-for-graph construction to draw the "waiter -&gt; owner" edge.
    /// </summary>
    public SimulationOperation? Owner { get; internal set; }

    /// <summary>
    /// Gets the reentrancy depth for an owned mutual-exclusion resource: the number of times
    /// <see cref="Owner"/> has acquired it without releasing. <c>0</c> when unowned. This is the
    /// metadata a reentrant <c>Monitor</c> needs; primitives without reentrancy simply never raise it
    /// above <c>1</c>.
    /// </summary>
    public int RecursionCount { get; internal set; }

    /// <summary>
    /// Gets the current number of available permits for a counting resource (a semaphore), or
    /// <c>0</c> for primitives that do not count. Acquire policy decrements it; release policy
    /// increments it toward <see cref="MaximumCount"/>.
    /// </summary>
    public int CurrentCount { get; internal set; }

    /// <summary>
    /// Gets the maximum number of permits a counting resource may hold, or <see cref="int.MaxValue"/>
    /// for resources without a meaningful cap. Only meaningful when <see cref="Kind"/> is a counting
    /// primitive.
    /// </summary>
    public int MaximumCount { get; internal set; } = int.MaxValue;

    /// <summary>
    /// Gets a value indicating whether an event-style resource is currently signaled/set. For a
    /// manual-reset event this stays set until explicitly reset; for an auto-reset event the scheduler
    /// clears it after waking one waiter. Not meaningful for non-event kinds.
    /// </summary>
    public bool IsSignaled { get; internal set; }

    /// <summary>
    /// Gets or sets an optional, opaque specialization hook a primitive can attach when it
    /// needs state or behaviour the core fields do not model (e.g. a fairness sub-policy, a condition
    /// predicate, or a task reference). The core never interprets it; it exists so specialized
    /// semantics can be layered on without changing this type. Kept deterministic is the caller's
    /// responsibility.
    /// </summary>
    public object? SpecializationTag { get; set; }

    /// <summary>Gets the number of waiters currently parked on this resource (resolved or not).</summary>
    public int WaiterCount => _waiters.Count;

    /// <summary>
    /// Gets a value indicating whether any <em>unresolved</em> waiter is parked on this resource.
    /// </summary>
    public bool HasPendingWaiters
    {
        get
        {
            foreach (var waiter in _waiters)
            {
                if (!waiter.IsResolved)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Captures a deterministic, immutable snapshot of this resource's current waiters in wait-queue
    /// order, for diagnostics and deadlock reporting.
    /// </summary>
    /// <returns>An ordered, immutable list of waiter snapshots.</returns>
    public IReadOnlyList<SimulationResourceWaiterInfo> SnapshotWaiters()
    {
        var result = new List<SimulationResourceWaiterInfo>(_waiters.Count);
        foreach (var waiter in _waiters)
        {
            result.Add(waiter.ToInfo());
        }

        return result;
    }

    // --- Internal wait-queue mechanics (scheduler-only, under the scheduler lock) ---

    /// <summary>Appends a waiter to the tail of the deterministic FIFO wait queue.</summary>
    internal void EnqueueWaiter(SimulationResourceWaiter waiter) => _waiters.Add(waiter);

    /// <summary>Removes a specific waiter from the queue (on resolution/teardown). Idempotent.</summary>
    internal void RemoveWaiter(SimulationResourceWaiter waiter) => _waiters.Remove(waiter);

    /// <summary>
    /// Returns the earliest-enqueued unresolved waiter without removing it, or <see langword="null"/>
    /// if none is pending. This is the deterministic "next to wake" under FIFO waiter ordering.
    /// </summary>
    internal SimulationResourceWaiter? PeekNextPending()
    {
        foreach (var waiter in _waiters)
        {
            if (!waiter.IsResolved)
            {
                return waiter;
            }
        }

        return null;
    }

    /// <summary>
    /// Enumerates the current unresolved waiters in deterministic FIFO order. Returns a copied array
    /// so callers may resolve/remove waiters while iterating.
    /// </summary>
    internal SimulationResourceWaiter[] SnapshotPendingWaiters()
    {
        var pending = new List<SimulationResourceWaiter>(_waiters.Count);
        foreach (var waiter in _waiters)
        {
            if (!waiter.IsResolved)
            {
                pending.Add(waiter);
            }
        }

        return [.. pending];
    }

    /// <summary>Enumerates every waiter (resolved or not) in deterministic FIFO order.</summary>
    internal SimulationResourceWaiter[] SnapshotAllWaiters() => [.. _waiters];

    private string DebuggerDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"{Id} [{Kind}] '{Name}' owner={(Owner?.Id.ToString() ?? "none")} waiters={_waiters.Count}");

    /// <inheritdoc />
    public override string ToString() => DebuggerDisplay;
}
