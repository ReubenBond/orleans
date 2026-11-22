
namespace Orleans.Durable.Storage;

/// <summary>
/// The answer of a node's grain whose activation runs beside another node's store (orleans.md §5.12, §7.2, OR-I14): it
/// touched no store, so the call had no effect, the activation deactivates, and the caller re-reads the registry. The
/// replica answers and the journal node grains' answers share it.
/// </summary>
internal sealed record NotThisNode
{
    private NotThisNode()
    {
    }

    /// <summary>Gets the one value.</summary>
    public static NotThisNode Instance { get; } = new();

    /// <summary>
    /// Deactivates <paramref name="grain"/> on its activation's <paramref name="scheduler"/> once idle (OR-I14).
    /// </summary>
    /// <param name="grain">The node's grain.</param>
    /// <param name="scheduler">The grain activation's task scheduler, captured before yielding to the thread pool.</param>
    public static Task DeactivateOnSchedulerAsync(IGrainBase grain, TaskScheduler scheduler) =>
        Task.Factory.StartNew(
            static state => ((IGrainBase)state!).DeactivateOnIdle(),
            grain,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            scheduler);
}
