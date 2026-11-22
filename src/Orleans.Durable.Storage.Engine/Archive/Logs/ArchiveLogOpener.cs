namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>The kind of an opener: the command which opened a generation (archive-logs.md §2.1).</summary>
public enum ArchiveLogOpenerKind
{
    /// <summary>A <c>Replace</c>: the generation's base is its body, so the base exists even when empty.</summary>
    Replace,

    /// <summary>A <c>Delete</c>: the generation's base holds only the appends after it.</summary>
    Delete,
}

/// <summary>
/// An opener's metadata (archive-logs.md §4): its kind, for a <c>Delete</c> with one its tombstone, and the rows of its
/// key the generation's base folds, written once with its generation.
/// </summary>
/// <remarks>
/// A tombstone is a <c>Delete</c>'s body, which Journals keeps empty or 8 bytes (archive.md §3); it is held as the
/// big-endian value of those 8 bytes, so its 16 lowercase hexadecimal digits are the bytes in order.
/// </remarks>
public readonly record struct ArchiveLogOpener
{
    /// <summary>The length of a tombstone, in bytes.</summary>
    public const int TombstoneLength = sizeof(ulong);

    /// <summary>The most rows a base folds, below <c>2^35</c>, so its token's field takes at most five bytes (archive-logs.md §7.2).</summary>
    public const ulong MaxRows = (1UL << 35) - 1;

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogOpener"/> struct.</summary>
    /// <param name="kind">The opener's kind.</param>
    /// <param name="tombstone">A <c>Delete</c>'s tombstone, or <see langword="null"/>.</param>
    /// <param name="rows">
    /// The key's rows in the opener's segment which the generation's base folds: the rows the opener superseded, the
    /// opener, and the appends after it; at least 1.
    /// </param>
    /// <exception cref="ArgumentException">A <c>Replace</c> carries a tombstone, or the kind is undefined.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> is 0 or above <see cref="MaxRows"/>.</exception>
    public ArchiveLogOpener(ArchiveLogOpenerKind kind, ulong? tombstone, ulong rows)
    {
        ArgumentOutOfRangeException.ThrowIfZero(rows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, MaxRows);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == ArchiveLogOpenerKind.Replace && tombstone is not null)
        {
            throw new ArgumentException("A Replace carries no tombstone.", nameof(tombstone));
        }

        Kind = kind;
        Tombstone = tombstone;
        Rows = rows;
    }

    /// <summary>Gets the opener's kind.</summary>
    public ArchiveLogOpenerKind Kind { get; }

    /// <summary>Gets a <c>Delete</c>'s tombstone, or <see langword="null"/> for a <c>Replace</c> and an empty <c>Delete</c>.</summary>
    public ulong? Tombstone { get; }

    /// <summary>Gets the key's rows the generation's base folds, the generation's <see cref="ArchiveLogPosition.BaseRows"/>.</summary>
    public ulong Rows { get; }
}
