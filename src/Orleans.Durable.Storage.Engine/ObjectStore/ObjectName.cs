using System.Buffers;
using System.Diagnostics;

namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// The name of an object in the archive's object store (archive §4, identity.md §11): <c>manifest</c>, <c>store</c>,
/// or <c>{kind}/{scope}/{id}</c>.
/// </summary>
/// <remarks>
/// A name is 1 to <see cref="MaxLength"/> characters of <c>a</c>–<c>z</c>, <c>0</c>–<c>9</c>, <c>-</c>, and <c>_</c>, in
/// components separated by single <c>/</c>s (identity.md §11). The bound is the archive's: every name it makes is at
/// most 75 characters, and the manifest's format carries a name's length in one byte. A name holds no <c>.</c>, so a
/// provider may mark files of its own with one. Equality and order are ordinal.
/// </remarks>
public sealed record ObjectName : IComparable<ObjectName>
{
    /// <summary>
    /// The length of the longest name, in characters.
    /// </summary>
    public const int MaxLength = 200;

    private static readonly SearchValues<char> Characters = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-_/");

    /// <summary>
    /// Initializes a new instance of the <see cref="ObjectName"/> class.
    /// </summary>
    /// <param name="value">The name.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not an object name.</exception>
    public ObjectName(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"'{value}' is not an object name: 1 to {MaxLength} characters of a-z, 0-9, '-', and '_', in components separated by single '/'s.",
                nameof(value));
        }

        Value = value;
    }

    private ObjectName(string value, bool validated)
    {
        Debug.Assert(validated && IsValid(value), "Validated ObjectName construction requires a valid object name.");
        Value = value;
    }

    /// <summary>
    /// Returns <c>{validatedPrefix}{name}</c> without re-scanning already validated characters when the combined length fits
    /// <see cref="MaxLength"/>.
    /// </summary>
    internal static ObjectName WithValidatedPrefix(string validatedPrefix, ObjectName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (validatedPrefix.Length + name.Value.Length > MaxLength)
        {
            throw new ArgumentException(
                $"'{validatedPrefix}{name.Value}' exceeds an object name's maximum length of {MaxLength} characters.",
                nameof(name));
        }

        return new ObjectName(string.Concat(validatedPrefix, name.Value), validated: true);
    }

    /// <summary>
    /// Gets the manifest's name.
    /// </summary>
    public static ObjectName Manifest { get; } = new("manifest");

    /// <summary>
    /// Gets the name of the store ID, which the LSM's archive store creates once (identity.md §11).
    /// </summary>
    public static ObjectName Store { get; } = new("store");

    /// <summary>
    /// Gets the name's text.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Returns whether <paramref name="value"/> is an object name.
    /// </summary>
    public static bool IsValid(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > 0 && value[^1] != '/' && IsValidPrefix(value);
    }

    /// <summary>
    /// Returns whether <paramref name="prefix"/> is a listing prefix: empty, or the start of an object name, so at most
    /// <see cref="MaxLength"/> characters of a name, with no leading <c>/</c> and no <c>//</c>.
    /// </summary>
    public static bool IsValidPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return prefix.Length <= MaxLength
            && !prefix.StartsWith('/')
            && !prefix.Contains("//", StringComparison.Ordinal)
            && !prefix.AsSpan().ContainsAnyExcept(Characters);
    }

    /// <inheritdoc/>
    public int CompareTo(ObjectName? other) => other is null ? 1 : string.CompareOrdinal(Value, other.Value);

    /// <inheritdoc/>
    public override string ToString() => Value;

    public static bool operator <(ObjectName left, ObjectName right) => Compare(left, right) < 0;

    public static bool operator >(ObjectName left, ObjectName right) => Compare(left, right) > 0;

    public static bool operator <=(ObjectName left, ObjectName right) => Compare(left, right) <= 0;

    public static bool operator >=(ObjectName left, ObjectName right) => Compare(left, right) >= 0;

    private static int Compare(ObjectName? left, ObjectName? right) => left?.CompareTo(right) ?? (right is null ? 0 : -1);
}
