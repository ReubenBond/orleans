using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A validated immutable ledger-local stream identifier: 1 to <see cref="MaxLength"/> ASCII bytes from
/// <c>[a-z0-9._-]</c> (ledgers.md §2.2, §4).
/// </summary>
public sealed class LocalStreamName : IEquatable<LocalStreamName>, IComparable<LocalStreamName>
{
    /// <summary>The maximum length of a stream name in ASCII bytes.</summary>
    public const int MaxLength = 64;

    private readonly byte[] _bytes;

    /// <summary>Initializes a new instance of the <see cref="LocalStreamName"/> class.</summary>
    /// <param name="value">A non-empty ASCII string of at most <see cref="MaxLength"/> characters in <c>[a-z0-9._-]</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, longer than <see cref="MaxLength"/>, or contains an invalid character.</exception>
    public LocalStreamName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > MaxLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A local stream name is 1 to {MaxLength} characters, not {value.Length}."),
                nameof(value));
        }

        var bytes = new byte[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c > 0x7F || !IsValidByte((byte)c))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Local stream name '{value}' contains an invalid character at index {i}: only ASCII [a-z0-9._-] is allowed."),
                    nameof(value));
            }

            bytes[i] = (byte)c;
        }

        Value = value;
        _bytes = bytes;
    }

    private LocalStreamName(string value, byte[] bytes)
    {
        Debug.Assert(IsValid(bytes), "Internal callers validate stream bytes before adoption.");
        Value = value;
        _bytes = bytes;
    }

    /// <summary>Gets the canonical ASCII string form of the stream name.</summary>
    public string Value { get; }

    /// <summary>Gets the immutable ASCII bytes of the stream name.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>Gets the immutable ASCII byte span of the stream name.</summary>
    public ReadOnlySpan<byte> Span => _bytes;

    /// <summary>Returns whether <paramref name="value"/> is an allowed ASCII byte in <c>[a-z0-9._-]</c>.</summary>
    public static bool IsValidByte(byte value) =>
        value is >= (byte)'a' and <= (byte)'z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'.'
            or (byte)'_'
            or (byte)'-';

    /// <summary>Returns whether <paramref name="bytes"/> is a valid local stream name (1 to <see cref="MaxLength"/> bytes in <c>[a-z0-9._-]</c>).</summary>
    public static bool IsValid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxLength)
        {
            return false;
        }

        foreach (var b in bytes)
        {
            if (!IsValidByte(b))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns whether <paramref name="value"/> is a valid local stream name (1 to <see cref="MaxLength"/> ASCII characters in <c>[a-z0-9._-]</c>).</summary>
    public static bool IsValid(ReadOnlySpan<char> value)
    {
        if (value.Length is 0 or > MaxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c > 0x7F || !IsValidByte((byte)c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Parses <paramref name="value"/> as a <see cref="LocalStreamName"/>.</summary>
    public static LocalStreamName Parse(string value) => new(value);

    /// <summary>Attempts to parse <paramref name="value"/> as a <see cref="LocalStreamName"/>.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out LocalStreamName? name)
    {
        if (value is null || !IsValid(value.AsSpan()))
        {
            name = null;
            return false;
        }

        name = new LocalStreamName(value, Encoding.ASCII.GetBytes(value));
        return true;
    }

    /// <summary>Attempts to parse <paramref name="bytes"/> as a <see cref="LocalStreamName"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out LocalStreamName? name)
    {
        if (!IsValid(bytes))
        {
            name = null;
            return false;
        }

        var copy = bytes.ToArray();
        name = new LocalStreamName(Encoding.ASCII.GetString(copy), copy);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(LocalStreamName? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LocalStreamName other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public int CompareTo(LocalStreamName? other) =>
        other is null ? 1 : string.CompareOrdinal(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Returns whether two stream names are equal by ordinal comparison.</summary>
    public static bool operator ==(LocalStreamName? left, LocalStreamName? right) =>
        ReferenceEquals(left, right) || left is not null && left.Equals(right);

    /// <summary>Returns whether two stream names differ.</summary>
    public static bool operator !=(LocalStreamName? left, LocalStreamName? right) => !(left == right);
}
