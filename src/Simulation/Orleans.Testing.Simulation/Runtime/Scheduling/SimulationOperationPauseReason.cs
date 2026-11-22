namespace Orleans.Testing.Simulation.Runtime.Scheduling;

/// <summary>
/// A coarse classification of <em>why</em> a <see cref="SimulationOperation"/> released the
/// permission baton and entered <see cref="SimulationOperationState.Paused"/>. This is intentionally
/// a small, open set: controlled-operation kernel only needs generic categories, and the concrete resource-wait
/// categories a resource model will add (monitor waits, semaphore waits, wait handles,
/// synchronous task waits) can be represented as <see cref="ResourceWait"/> with a descriptive
/// <see cref="SimulationPauseReason.Detail"/> until they earn dedicated members.
/// </summary>
public enum SimulationOperationPauseReason
{
    /// <summary>
    /// The operation yielded to give the scheduler an opportunity to run another operation, but is
    /// otherwise immediately eligible to continue. Used for cooperative fairness points.
    /// </summary>
    Yield,

    /// <summary>
    /// The operation is waiting for a resource, signal, or condition that another operation is
    /// expected to satisfy (a monitor, semaphore, wait handle, or synchronous task wait). controlled-operation kernel
    /// exposes this generically; resource/wait scheduler builds the concrete resource model on top of it.
    /// </summary>
    ResourceWait,

    /// <summary>
    /// The operation paused for a scheduler-defined or test-defined reason not covered by the other
    /// kinds. The <see cref="SimulationPauseReason.Detail"/> carries the specifics.
    /// </summary>
    Other,
}

/// <summary>
/// <para>
/// Immutable metadata describing why a <see cref="SimulationOperation"/> is paused. A paused
/// operation always has a non-<see langword="null"/> reason; a running/runnable/terminal operation
/// has none.
/// </para>
/// <para>
/// The <see cref="Detail"/> is a stable, human-readable string suitable for deterministic
/// diagnostics and pending-work summaries - it must not embed non-deterministic data (timestamps,
/// hash codes, managed thread ids) so that the same simulation run produces the same reason text.
/// </para>
/// </summary>
/// <param name="Kind">The coarse category of the pause.</param>
/// <param name="Detail">
/// A short, stable description of the specific wait, such as its resource tag. Never
/// <see langword="null"/>; use an empty string when there is nothing to add.
/// </param>
public sealed record SimulationPauseReason(SimulationOperationPauseReason Kind, string Detail)
{
    /// <summary>
    /// Gets the canonical reason for a cooperative <see cref="SimulationOperationPauseReason.Yield"/>.
    /// </summary>
    public static SimulationPauseReason Yield { get; } = new(SimulationOperationPauseReason.Yield, "cooperative yield");

    /// <summary>
    /// Gets the description supplied at construction, guaranteed non-<see langword="null"/>.
    /// </summary>
    public string Detail { get; } = Detail ?? throw new ArgumentNullException(nameof(Detail));

    /// <summary>
    /// Creates a generic <see cref="SimulationOperationPauseReason.ResourceWait"/> reason with the
    /// given stable detail.
    /// </summary>
    /// <param name="detail">A short, stable description of the resource being waited on.</param>
    /// <returns>A resource-wait pause reason.</returns>
    public static SimulationPauseReason ResourceWait(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new SimulationPauseReason(SimulationOperationPauseReason.ResourceWait, detail);
    }

    /// <summary>
    /// Creates a generic <see cref="SimulationOperationPauseReason.Other"/> reason with the given
    /// stable detail.
    /// </summary>
    /// <param name="detail">A short, stable description of the pause.</param>
    /// <returns>An "other" pause reason.</returns>
    public static SimulationPauseReason Custom(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new SimulationPauseReason(SimulationOperationPauseReason.Other, detail);
    }

    /// <inheritdoc />
    public override string ToString() => Detail.Length == 0 ? Kind.ToString() : $"{Kind}({Detail})";
}
