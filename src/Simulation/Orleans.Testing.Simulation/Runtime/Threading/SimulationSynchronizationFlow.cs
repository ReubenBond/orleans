using System.Threading;

namespace Orleans.Testing.Simulation.Runtime.Threading;

/// <summary>
/// <para>
/// The ambient logical-strand identity that controlled synchronization primitives
/// (<c>ControlledMonitor</c>, <c>ControlledLock</c>) use to answer "does the currently
/// executing strand already own this lock?". Clockwork runs every controlled strand on a single
/// cooperative logical thread, so ownership and reentrancy cannot be keyed on the physical
/// managed-thread id (they are all the same thread); they must be keyed on which independently
/// scheduled unit of controlled work is running.
/// </para>
/// <para>
/// A fresh, process-unique id is assigned at the single point where a new controlled strand begins -
/// <see cref="Orleans.Testing.Simulation.Runtime.Tasks.SimulationTaskRuntime.QueueWork"/> - via <see cref="RunAsNewStrand"/>,
/// and restored when
/// that synchronous run segment returns. Because every Monitor/Lock operation is synchronous, the id
/// only has to be stable across a synchronous run segment (between cooperative yield points), which the
/// save/restore guarantees even when a strand blocks and re-entrantly pumps the loop (a nested strand
/// sets and restores its own id around its run). Strands scheduled directly onto a node's work queue
/// (rather than through <see cref="Orleans.Testing.Simulation.Runtime.Tasks.SimulationTaskRuntime.QueueWork"/>) observe
/// the root id <see cref="None"/>.
/// </para>
/// <para>
/// The value is never surfaced to user code and never logged or persisted; only equality is
/// meaningful, so the process-global counter does not affect determinism or replay of any single
/// simulation.
/// </para>
/// </summary>
public static class SimulationSynchronizationFlow
{
    private static long _next;
    private static readonly AsyncLocal<long> Ambient = new();

    /// <summary>The identity observed outside any explicitly-entered controlled strand (the root strand).</summary>
    public const long None = 0;

    /// <summary>
    /// Gets the id of the controlled strand currently executing on the logical thread, or
    /// <see cref="None"/> when no strand scope has been entered.
    /// </summary>
    public static long CurrentId => Ambient.Value;

    /// <summary>
    /// Runs <paramref name="work"/> as a fresh controlled strand: assigns a new process-unique id,
    /// makes it the ambient <see cref="CurrentId"/> for the synchronous duration of the call, and
    /// restores the previous id when it returns. This is invoked at the new-strand choke point so each
    /// independently scheduled unit of controlled work carries a distinct owner identity.
    /// </summary>
    /// <param name="work">The strand body to run.</param>
    public static void RunAsNewStrand(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var previous = Ambient.Value;
        Ambient.Value = Interlocked.Increment(ref _next);
        try
        {
            work();
        }
        finally
        {
            Ambient.Value = previous;
        }
    }

    /// <summary>
    /// Restores an already-assigned controlled strand around work that must run under a captured
    /// <see cref="ExecutionContext"/>. The captured context can contain the enqueueing strand's
    /// <see cref="AsyncLocal{T}"/> value, so the fresh queue identity is re-applied inside it.
    /// </summary>
    internal static void RunAsStrand(long strandId, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var previous = Ambient.Value;
        Ambient.Value = strandId;
        try
        {
            work();
        }
        finally
        {
            Ambient.Value = previous;
        }
    }

    internal static void RestoreCurrentId(long strandId) => Ambient.Value = strandId;
}
