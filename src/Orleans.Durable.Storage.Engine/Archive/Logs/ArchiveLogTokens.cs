using System.Globalization;

using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>What decoding an archive-log token gives (conventions.md §15): its position, or the rejection naming the rule it breaks.</summary>
internal readonly union ArchiveLogTokenDecoding(ArchiveLogPosition, InvalidDataException);

/// <summary>
/// Archive logs' tokens (archive-logs.md §7.2), private to <c>Orleans.Durable.Storage.Engine.Archive.Logs</c>: a log's position
/// <c>(g, r, P, b)</c>. <see cref="ArchiveLogPosition.Origin"/> is the empty token, and any other is a version byte, 1, then
/// <c>g</c>, <c>r</c>, <c>P</c>, and <c>b</c> as unsigned LEB128 varints in their shortest form: <c>g</c> and <c>P</c>
/// below <c>2^56</c>, eight bytes at most, and <c>r</c> and <c>b</c> below <c>2^35</c>, five at most, so a token is at
/// most 27 bytes. Each encoding is canonical, so byte equality is token equality.
/// </summary>
internal static class ArchiveLogTokens
{
    /// <summary>The version byte of every token but <c>Origin</c>.</summary>
    public const byte Version = 1;

    /// <summary>The largest generation or length a token holds, <c>2^56 - 1</c>: an eight-byte varint.</summary>
    public const ulong MaxWide = (1UL << 56) - 1;

    /// <summary>The largest base rows or units a token holds, <c>2^35 - 1</c>: a five-byte varint.</summary>
    public const ulong MaxNarrow = ArchiveLogOpener.MaxRows;

    /// <summary>The longest token: the version byte, two eight-byte varints, and two five-byte ones.</summary>
    public const int MaxLength = 1 + 8 + 5 + 8 + 5;

    internal const string ReasonBadVersion = "bad-version";
    internal const string ReasonBadVarint = "bad-varint";
    internal const string ReasonTrailingBytes = "trailing-bytes";
    internal const string ReasonZeroGeneration = "zero-generation";
    internal const string ReasonBadUnits = "bad-units";
    internal const string ReasonUnitsAboveLength = "units-above-length";
    internal const string ReasonOutOfRange = "out-of-range";

    /// <summary>Returns the token of <paramref name="position"/>.</summary>
    /// <exception cref="ArgumentException">The position is no token's: <c>g = 0</c> off <c>Origin</c>, a field past its bound, or its units break §7.2's rules.</exception>
    public static RowToken Encode(ArchiveLogPosition position)
    {
        if (position.IsOrigin)
        {
            return default;
        }

        if (Violation(position) is { } reason)
        {
            throw new ArgumentException($"The position {position} encodes as no token: {reason}.", nameof(position));
        }

        Span<byte> bytes = stackalloc byte[MaxLength];
        bytes[0] = Version;
        var length = 1 + Varint.Write(bytes[1..], position.Generation);
        length += Varint.Write(bytes[length..], position.BaseRows);
        length += Varint.Write(bytes[length..], position.Length);
        length += Varint.Write(bytes[length..], position.Units);
        return new RowToken(bytes[..length]);
    }

    /// <summary>
    /// Decodes a token, checking in order its version (<c>bad-version</c>), its four varints (<c>bad-varint</c>), that
    /// nothing follows them (<c>trailing-bytes</c>), that <c>g</c> is not 0 (<c>zero-generation</c>), that each field lies
    /// within its bound (<c>out-of-range</c>), that <c>b</c> is 0 exactly when <c>P</c> is (<c>bad-units</c>), and that
    /// <c>b ≤ P</c> (<c>units-above-length</c>). The empty token is <see cref="ArchiveLogPosition.Origin"/>.
    /// </summary>
    /// <returns>The position, or the rejection naming the first rule the bytes break.</returns>
    public static ArchiveLogTokenDecoding Decode(ReadOnlySpan<byte> token)
    {
        if (token.IsEmpty)
        {
            return ArchiveLogPosition.Origin;
        }

        if (token[0] != Version)
        {
            return DecodeRejection.Create(ReasonBadVersion, string.Create(CultureInfo.InvariantCulture, $"An archive-log token's version is {token[0]}, not {Version}."));
        }

        Span<ulong> fields = stackalloc ulong[4];
        var at = 1;
        for (var index = 0; index < fields.Length; index++)
        {
            if (Varint.Read(token[at..], out fields[index], out var length) != Varint.Status.Valid)
            {
                return DecodeRejection.Create(ReasonBadVarint, string.Create(CultureInfo.InvariantCulture, $"An archive-log token's field {index} is not a varint in its shortest form."));
            }

            at += length;
        }

        if (at != token.Length)
        {
            return DecodeRejection.Create(ReasonTrailingBytes, "Bytes follow an archive-log token's units.");
        }

        var position = new ArchiveLogPosition(fields[0], fields[1], fields[2], fields[3]);
        return ReasonOf(position) is { } reason ? DecodeRejection.Create(reason, "An archive-log token is malformed: " + Describe(reason) + ".") : position;
    }

    /// <summary>Returns the position <paramref name="token"/> encodes.</summary>
    /// <exception cref="ArchiveCorruptException">The token is not one archive logs mint.</exception>
    public static ArchiveLogPosition PositionOf(RowToken token) => Decode(token.AsSpan()) switch
    {
        ArchiveLogPosition position => position,
        InvalidDataException rejection => throw new ArchiveCorruptException(
            string.Create(CultureInfo.InvariantCulture, $"Archive logs could not have minted the token {token}: {rejection.Message}"),
            rejection),
        null => throw new System.Diagnostics.UnreachableException(),
    };

    /// <summary>Returns why a position other than <c>Origin</c> is no token's, or <see langword="null"/> when it is one.</summary>
    internal static string? Violation(ArchiveLogPosition position) => ReasonOf(position) is { } reason ? Describe(reason) : null;

    private static string Describe(string reason) => reason switch
    {
        ReasonZeroGeneration => "its generation is 0, which only Origin, the empty token, has",
        ReasonOutOfRange => "a field passes its bound: 2^56 for the generation and the length, 2^35 for the base rows and the units",
        ReasonBadUnits => "its units are 0 exactly when its length is not",
        ReasonUnitsAboveLength => "its units exceed its length, though every unit holds a byte",
        _ => throw new System.Diagnostics.UnreachableException(),
    };

    private static string? ReasonOf(ArchiveLogPosition position) =>
        position.Generation == 0 ? ReasonZeroGeneration
        : position.Generation > MaxWide || position.Length > MaxWide || position.BaseRows > MaxNarrow || position.Units > MaxNarrow ? ReasonOutOfRange
        : position.Units == 0 ^ position.Length == 0 ? ReasonBadUnits
        : position.Units > position.Length ? ReasonUnitsAboveLength
        : null;
}
