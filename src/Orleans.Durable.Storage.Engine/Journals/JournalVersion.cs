using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A journal's version (journals.md §4.3): opaque, compared for equality only. A caller sees <see cref="Origin"/>, the
/// version of a journal with no history, and the canonical text form, the grain's ETag, which it formats and parses; an
/// absent journal's ETag is <see cref="Origin"/>'s text. Within Journals a version is a base, which the archive provider
/// minted, and a count which every accepted entry advances by one, converted to and from Ledgers' <see cref="RowVersion"/>.
/// </summary>
/// <remarks>
/// The text form (journals.md §7) is base64url without padding of the version's encoding, the base's length (<c>u8</c>),
/// the base's bytes, and the count as a shortest varint: 3 to 58 characters. Each version has one text, so string
/// equality is version equality.
/// </remarks>
public readonly record struct JournalVersion
{
    /// <summary>The most characters a version's text takes.</summary>
    private const int MaxTextLength = (RowVersionCodec.MaxLength * 4 + 2) / 3;

    internal JournalVersion(RowToken @base, ulong count)
    {
        Base = @base;
        Count = count;
    }

    /// <summary>Gets the version of a journal with no history: the empty base and the count 0.</summary>
    public static JournalVersion Origin => default;

    /// <summary>Gets the version's base: <c>Origin</c>'s empty token, or a prefix's version base.</summary>
    internal RowToken Base { get; }

    /// <summary>Gets the version's count.</summary>
    internal ulong Count { get; }

    /// <summary>Gets the length of the version's encoding in an entry record and its text form.</summary>
    internal int EncodedLength => RowVersionCodec.Length(ToRowVersion());

    /// <summary>Parses a version's canonical text form.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="FormatException"><paramref name="text"/> is not a version's canonical text.</exception>
    public static JournalVersion Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var version)
            ? version
            : throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"'{text}' is not a journal version's canonical text."));
    }

    /// <summary>
    /// Parses a version's text form: it decodes the base64url, checks the base's length and the count's varint as the
    /// entry record does, and accepts only text equal to the value's re-encoding (journals.md §7).
    /// </summary>
    /// <returns>Whether <paramref name="text"/> is a version's canonical text.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out JournalVersion version)
    {
        version = default;
        if (text.Length is 0 or > MaxTextLength)
        {
            return false;
        }

        // At most MaxTextLength characters decode to at most RowVersionCodec.MaxLength bytes.
        Span<byte> bytes = stackalloc byte[RowVersionCodec.MaxLength];
        if (Base64Url.DecodeFromChars(text, bytes, out var consumed, out var written) != OperationStatus.Done
            || consumed != text.Length
            || RowVersionCodec.Read(bytes[..written], out var parsed, out _) != RowVersionCodec.Status.Valid)
        {
            return false;
        }

        // The re-encoding refuses bytes after the count, padding, set unused bits, and any other alternative text.
        var candidate = FromRowVersion(parsed);
        Span<char> canonical = stackalloc char[MaxTextLength];
        if (!candidate.TryFormat(canonical, out var length) || !text.SequenceEqual(canonical[..length]))
        {
            return false;
        }

        version = candidate;
        return true;
    }

    /// <summary>Returns the version's canonical text form, the grain's ETag.</summary>
    public override string ToString()
    {
        Span<char> text = stackalloc char[MaxTextLength];
        var formatted = TryFormat(text, out var length);
        Debug.Assert(formatted, "A version's text fits its bound.");
        return new string(text[..length]);
    }

    /// <summary>Returns the version Ledgers stores as <paramref name="version"/>.</summary>
    internal static JournalVersion FromRowVersion(RowVersion version) => new(version.Base, version.Count);

    /// <summary>Returns the version as Ledgers stores it.</summary>
    internal RowVersion ToRowVersion() => new(Base, Count);

    /// <summary>Returns the version after one more entry: the same base, and the count advanced by one.</summary>
    /// <exception cref="OverflowException">The count is at the <c>u64</c> maximum.</exception>
    internal JournalVersion Next() => new(Base, checked(Count + 1));

    /// <summary>Writes the version's encoding at the start of <paramref name="destination"/> and returns its length.</summary>
    internal int Write(Span<byte> destination) => RowVersionCodec.Write(destination, ToRowVersion());

    private bool TryFormat(Span<char> destination, out int written)
    {
        Span<byte> bytes = stackalloc byte[RowVersionCodec.MaxLength];
        var length = Write(bytes);
        return Base64Url.TryEncodeToChars(bytes[..length], destination, out written);
    }
}
