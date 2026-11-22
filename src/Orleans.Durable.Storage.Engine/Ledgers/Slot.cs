using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Identifies a slot of a ledger: a non-negative position, which holds one batch.
/// </summary>
public readonly struct Slot : IComparable<Slot>, IEquatable<Slot>
{
    /// <summary>Initializes a new instance of the <see cref="Slot"/> struct.</summary>
    public Slot(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    /// <summary>Gets slot zero.</summary>
    public static Slot Zero => default;

    /// <summary>Gets the slot's position.</summary>
    public long Value { get; }

    /// <summary>Returns the slot displaced by <paramref name="offset"/>.</summary>
    /// <remarks>
    /// Arithmetic is checked. Overflow throws <see cref="OverflowException"/>, and an offset which would produce a negative
    /// position throws <see cref="ArgumentOutOfRangeException"/>.
    /// </remarks>
    public Slot Add(long offset) => new(checked(Value + offset));

    /// <summary>
    /// Returns the signed distance from <paramref name="other"/> to this slot.
    /// </summary>
    public long Subtract(Slot other) => checked(Value - other.Value);

    /// <inheritdoc/>
    public int CompareTo(Slot other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    public bool Equals(Slot other) => Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Slot other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value.GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns <paramref name="position"/> advanced by <paramref name="offset"/>.
    /// </summary>
    public static Slot operator +(Slot position, long offset) => position.Add(offset);

    /// <summary>
    /// Returns <paramref name="position"/> reduced by <paramref name="offset"/>.
    /// </summary>
    /// <remarks>A result below slot zero throws <see cref="ArgumentOutOfRangeException"/>.</remarks>
    public static Slot operator -(Slot position, long offset) => position.Add(checked(-offset));

    /// <summary>
    /// Returns the signed distance from <paramref name="right"/> to <paramref name="left"/>.
    /// </summary>
    public static long operator -(Slot left, Slot right) => left.Subtract(right);

    public static bool operator ==(Slot left, Slot right) => left.Equals(right);

    public static bool operator !=(Slot left, Slot right) => !left.Equals(right);

    public static bool operator <(Slot left, Slot right) => left.Value < right.Value;

    public static bool operator >(Slot left, Slot right) => left.Value > right.Value;

    public static bool operator <=(Slot left, Slot right) => left.Value <= right.Value;

    public static bool operator >=(Slot left, Slot right) => left.Value >= right.Value;
}
