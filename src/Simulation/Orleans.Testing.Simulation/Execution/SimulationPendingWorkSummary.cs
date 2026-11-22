using System.Globalization;
using System.Text;

namespace Orleans.Testing.Simulation;

/// <summary>
/// <para>
/// A snapshot of outstanding work across a <see cref="SimulationCluster"/>'s cluster
/// queue and every node queue, captured at the end of a drive-loop execution.
/// </para>
/// <para>
/// This distinguishes work that could run right now (<see cref="RunnableCount"/>), work that is
/// ready but stuck behind a suspended node (<see cref="BlockedCount"/>), and work that is simply
/// scheduled for a later simulated time (<see cref="WaitingCount"/>).
/// </para>
/// </summary>
public sealed class SimulationPendingWorkSummary
{
    /// <summary>
    /// Gets an empty summary with no pending work of any kind.
    /// </summary>
    public static SimulationPendingWorkSummary Empty { get; } = new(0, 0, 0, []);

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationPendingWorkSummary"/> class.
    /// </summary>
    /// <param name="runnableCount">The number of items that are ready and could execute right now.</param>
    /// <param name="waitingCount">The number of items scheduled for a future simulated time.</param>
    /// <param name="blockedCount">The number of items that are ready but cannot run because their node is suspended.</param>
    /// <param name="items">The individual item diagnostics, in stable order (by due time, then sequence number, then queue identity).</param>
    public SimulationPendingWorkSummary(int runnableCount, int waitingCount, int blockedCount, IReadOnlyList<SimulationScheduledItemDiagnostic> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        RunnableCount = runnableCount;
        WaitingCount = waitingCount;
        BlockedCount = blockedCount;
        Items = Array.AsReadOnly(items.ToArray());
    }

    /// <summary>
    /// Gets the number of items that are ready and could execute on the next round-robin step.
    /// </summary>
    public int RunnableCount { get; }

    /// <summary>
    /// Gets the number of items scheduled for a future simulated time (not yet ready).
    /// </summary>
    public int WaitingCount { get; }

    /// <summary>
    /// Gets the number of items that are ready (due time has passed) but cannot execute because
    /// their owning node is currently suspended.
    /// </summary>
    public int BlockedCount { get; }

    /// <summary>
    /// Gets the total number of items pending across all queues: <see cref="RunnableCount"/> +
    /// <see cref="WaitingCount"/> + <see cref="BlockedCount"/>.
    /// </summary>
    public int PendingCount => RunnableCount + WaitingCount + BlockedCount;

    /// <summary>
    /// Gets the individual item diagnostics that make up this summary, in stable order (by due
    /// time, then sequence number, then queue identity).
    /// </summary>
    public IReadOnlyList<SimulationScheduledItemDiagnostic> Items { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Runnable={RunnableCount}, Waiting={WaitingCount}, Blocked={BlockedCount}, Total={PendingCount}");
        foreach (var item in Items)
        {
            builder.Append("\n  ").Append(item);
        }

        return builder.ToString();
    }
}
