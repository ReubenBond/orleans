using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Memory;

/// <summary>
/// The memory store's archive tokens (archive.md §9): a key's position, its generation, the rows its opener superseded,
/// and the appends that generation holds. <c>Origin</c>, the position of a key with no history, is the empty token; any
/// other is a version byte, 1, then the generation, the superseded rows, and the appends as varints in their shortest form. A position <c>Plan</c> never mints, a non-empty
/// encoding of <c>Origin</c>, is no token, so each encoding is canonical and byte equality is equality (HI-I3).
/// </summary>
internal static class InMemoryArchiveTokens
{
    /// <summary>The version byte of every token but <c>Origin</c>.</summary>
    public const byte Version = 1;

    /// <summary>Returns the token of <paramref name="position"/>.</summary>
    public static RowToken Encode(InMemoryArchivePosition position)
    {
        if (position.IsOrigin)
        {
            return default;
        }

        Span<byte> bytes = stackalloc byte[1 + 3 * Varint.MaxLength];
        bytes[0] = Version;
        var length = 1 + Varint.Write(bytes[1..], position.Generation);
        length += Varint.Write(bytes[length..], position.Superseded);
        length += Varint.Write(bytes[length..], position.Appends);
        return new RowToken(bytes[..length]);
    }

    /// <summary>Returns the position <paramref name="token"/> encodes.</summary>
    /// <exception cref="ArchiveCorruptException">The token is not one the store mints.</exception>
    public static InMemoryArchivePosition Decode(RowToken token)
    {
        var bytes = token.AsSpan();
        if (bytes.IsEmpty)
        {
            return default;
        }

        if (bytes[0] != Version)
        {
            throw Malformed(token, "its version is not 1");
        }

        if (Varint.Read(bytes[1..], out var generation, out var length) != Varint.Status.Valid)
        {
            throw Malformed(token, "its generation is not a varint in its shortest form");
        }

        var at = 1 + length;
        if (Varint.Read(bytes[at..], out var superseded, out length) != Varint.Status.Valid)
        {
            throw Malformed(token, "its superseded rows are not a varint in its shortest form");
        }

        at += length;
        if (Varint.Read(bytes[at..], out var appends, out length) != Varint.Status.Valid)
        {
            throw Malformed(token, "its appends are not a varint in its shortest form");
        }

        if (at + length != bytes.Length)
        {
            throw Malformed(token, "bytes follow its appends");
        }

        var position = new InMemoryArchivePosition(generation, superseded, appends);
        return position.IsOrigin ? throw Malformed(token, "it encodes Origin, which is the empty token") : position;
    }

    private static ArchiveCorruptException Malformed(RowToken token, string reason) =>
        new(string.Create(CultureInfo.InvariantCulture, $"The memory store could not have minted the token {token}: {reason}."));
}

/// <summary>
/// A key's position in the memory store: its generation, which each opener raises, the rows of its segment the opener
/// superseded, so each state the segment's rows pass through has its own position (archive.md §3), and the appends that
/// generation holds. Positions order a key's chain by generation, then superseded rows, then appends.
/// </summary>
/// <param name="Generation">The generation, 0 for a key whose only writes are append runs.</param>
/// <param name="Superseded">The rows of its segment the generation's opener superseded; 0 without an opener.</param>
/// <param name="Appends">The appends the generation holds.</param>
internal readonly record struct InMemoryArchivePosition(ulong Generation, ulong Superseded, ulong Appends) : IComparable<InMemoryArchivePosition>
{
    /// <summary>Gets a value indicating whether the position is <c>Origin</c>, a key's with no history.</summary>
    public bool IsOrigin => Generation == 0 && Superseded == 0 && Appends == 0;

    public static bool operator <(InMemoryArchivePosition left, InMemoryArchivePosition right) => left.CompareTo(right) < 0;

    public static bool operator >(InMemoryArchivePosition left, InMemoryArchivePosition right) => left.CompareTo(right) > 0;

    public static bool operator <=(InMemoryArchivePosition left, InMemoryArchivePosition right) => left.CompareTo(right) <= 0;

    public static bool operator >=(InMemoryArchivePosition left, InMemoryArchivePosition right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Returns the position after <paramref name="command"/>: an opener opens the next generation, having superseded
    /// <paramref name="superseded"/> rows, and an append adds to it.
    /// </summary>
    /// <exception cref="ArchiveCorruptException">
    /// The position is at the last generation or append count, which no chain the store planned reaches: its token was
    /// not one the store minted.
    /// </exception>
    public InMemoryArchivePosition After(ArchiveCommand command, int superseded = 0)
    {
        if (command.IsOpener ? Generation == ulong.MaxValue : Appends == ulong.MaxValue)
        {
            throw new ArchiveCorruptException(string.Create(
                CultureInfo.InvariantCulture,
                $"The memory store could not have minted a token at generation {Generation} with {Appends} appends: no chain passes it."));
        }

        return command.IsOpener ? new(Generation + 1, (ulong)superseded, 0) : this with { Appends = Appends + 1 };
    }

    /// <inheritdoc/>
    public int CompareTo(InMemoryArchivePosition other) =>
        Generation != other.Generation ? Generation.CompareTo(other.Generation)
        : Superseded != other.Superseded ? Superseded.CompareTo(other.Superseded)
        : Appends.CompareTo(other.Appends);
}
