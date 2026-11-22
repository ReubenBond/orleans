using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Identifies a ledger within its cluster: a non-zero <see cref="ulong"/> the ledger map assigns in installation
/// order and never reuses (identity.md §3, LG-I3).
/// </summary>
/// <remarks>
/// Equality is by value and the order is numeric. Binary encodings carry the value as a <c>u64</c>, and its text form
/// is its decimal digits. Only the constructor makes a ledger ID: reading <see cref="Value"/> of the default instance
/// throws, so every encoding and comparison reads a validated ID.
/// </remarks>
public readonly record struct LedgerId : IComparable<LedgerId>
{
    private readonly ulong _value;

    /// <summary>Initializes a new instance of the <see cref="LedgerId"/> struct.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is 0.</exception>
    public LedgerId(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfZero(value);
        _value = value;
    }

    /// <summary>Gets the ledger's number.</summary>
    /// <exception cref="InvalidOperationException">The instance is the default, which no constructor made.</exception>
    public ulong Value =>
        _value != 0 ? _value : throw new InvalidOperationException("The default LedgerId is not a ledger ID.");

    /// <inheritdoc/>
    public int CompareTo(LedgerId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the text form, the value's decimal digits.</summary>
    public override string ToString() => _value.ToString(CultureInfo.InvariantCulture);

    public static bool operator <(LedgerId left, LedgerId right) => left.Value < right.Value;

    public static bool operator >(LedgerId left, LedgerId right) => left.Value > right.Value;

    public static bool operator <=(LedgerId left, LedgerId right) => left.Value <= right.Value;

    public static bool operator >=(LedgerId left, LedgerId right) => left.Value >= right.Value;
}
