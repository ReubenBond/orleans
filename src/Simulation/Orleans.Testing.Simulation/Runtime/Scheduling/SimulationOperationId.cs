using System.Globalization;

namespace Orleans.Testing.Simulation.Runtime.Scheduling;

/// <summary>
/// <para>
/// A stable, process-friendly identity for one <see cref="SimulationOperation"/> within a single
/// <see cref="SimulationScheduler"/>. Values are assigned by the scheduler in strictly
/// increasing registration order starting at 1, so an operation's id is also a deterministic tie
/// break for scheduler selection and a stable sort key for diagnostics.
/// </para>
/// <para>
/// This is deliberately distinct from both <see cref="Execution.SimulationLogicalExecutionId"/>
/// (the ambient logical-thread identity an operation runs under) and
/// <see cref="Environment.CurrentManagedThreadId"/> (the physical thread it happens to run on):
/// one operation has exactly one <see cref="SimulationOperationId"/> for its whole lifetime, even
/// as the physical thread carrying its permission baton changes.
/// </para>
/// </summary>
/// <param name="Value">The 1-based, per-scheduler monotonic identity. <c>0</c> is <see cref="None"/>.</param>
public readonly record struct SimulationOperationId(long Value) : IComparable<SimulationOperationId>
{
    /// <summary>
    /// Gets the sentinel identity used where no operation is present (e.g. an operation with no
    /// parent). Never assigned to a registered operation.
    /// </summary>
    public static SimulationOperationId None => default;

    /// <summary>
    /// Gets a value indicating whether this is <see cref="None"/>.
    /// </summary>
    public bool IsNone => Value == 0;

    /// <inheritdoc />
    public int CompareTo(SimulationOperationId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() =>
        IsNone ? "op:none" : string.Create(CultureInfo.InvariantCulture, $"op:{Value}");

    /// <summary>Compares two identities by their underlying value.</summary>
    public static bool operator <(SimulationOperationId left, SimulationOperationId right) => left.Value < right.Value;

    /// <summary>Compares two identities by their underlying value.</summary>
    public static bool operator >(SimulationOperationId left, SimulationOperationId right) => left.Value > right.Value;

    /// <summary>Compares two identities by their underlying value.</summary>
    public static bool operator <=(SimulationOperationId left, SimulationOperationId right) => left.Value <= right.Value;

    /// <summary>Compares two identities by their underlying value.</summary>
    public static bool operator >=(SimulationOperationId left, SimulationOperationId right) => left.Value >= right.Value;
}
