using System.Globalization;

namespace Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

/// <summary>
/// <para>
/// A stable, per-scheduler identity for one <see cref="SimulationResource"/>. Values are assigned
/// by the owning <see cref="SimulationScheduler"/> in strictly increasing creation order
/// starting at 1, so a resource's id is both a deterministic tie-break for diagnostics ordering and
/// a stable, process-friendly key for wait-for-graph and deadlock reporting.
/// </para>
/// <para>
/// This identity is deliberately independent of the CLR object that a controlled
/// <c>Monitor</c>/<c>SemaphoreSlim</c>/wait-handle shim (controlled synchronization) will associate with the
/// resource: the shim maps its own key (e.g. the sync-object reference) to a
/// <see cref="SimulationResource"/>, and the resource keeps this stable id for its whole lifetime
/// regardless of how many operations wait on it.
/// </para>
/// </summary>
/// <param name="Value">The 1-based, per-scheduler monotonic identity. <c>0</c> is <see cref="None"/>.</param>
public readonly record struct SimulationResourceId(long Value) : IComparable<SimulationResourceId>
{
    /// <summary>
    /// Gets the sentinel identity used where no resource is present. Never assigned to a created
    /// resource.
    /// </summary>
    public static SimulationResourceId None => default;

    /// <summary>Gets a value indicating whether this is <see cref="None"/>.</summary>
    public bool IsNone => Value == 0;

    /// <inheritdoc />
    public int CompareTo(SimulationResourceId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() =>
        IsNone ? "res:none" : string.Create(CultureInfo.InvariantCulture, $"res:{Value}");

    /// <summary>Compares two identities by their underlying value.</summary>
    public static bool operator <(SimulationResourceId left, SimulationResourceId right) => left.Value < right.Value;

    /// <summary>Compares two identities by their underlying value.</summary>
    public static bool operator >(SimulationResourceId left, SimulationResourceId right) => left.Value > right.Value;

    /// <summary>Compares two identities by their underlying value.</summary>
    public static bool operator <=(SimulationResourceId left, SimulationResourceId right) => left.Value <= right.Value;

    /// <summary>Compares two identities by their underlying value.</summary>
    public static bool operator >=(SimulationResourceId left, SimulationResourceId right) => left.Value >= right.Value;
}
