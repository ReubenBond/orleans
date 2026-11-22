using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// A log's position (archive-logs.md §2.1, §4): its current generation, the rows of its key the generation's base folds,
/// the length of its write-ahead content, and the units that content was written in (committed blocks on Azure, chunks
/// on the chunk layout). <see cref="Origin"/>, <c>(0, 0, 0, 0)</c>, is the position of an absent log. The typed form of
/// an archive-log token (§7.2).
/// </summary>
/// <param name="Generation">The generation, which every opener raises; 0 only at <see cref="Origin"/>.</param>
/// <param name="BaseRows">
/// The key's rows in the opener's segment which the generation's base folds, the rows the opener superseded among them, so
/// that every state those rows pass through has its own position; 0 for a generation a key's first append run created.
/// </param>
/// <param name="Length">The length of the current generation's write-ahead content, in bytes.</param>
/// <param name="Units">The units that content was written in.</param>
public readonly record struct ArchiveLogPosition(ulong Generation, ulong BaseRows, ulong Length, ulong Units) : IComparable<ArchiveLogPosition>
{
    /// <summary>Gets the position of an absent log, <c>(0, 0, 0, 0)</c>.</summary>
    public static ArchiveLogPosition Origin => default;

    /// <summary>Gets a value indicating whether the position is <see cref="Origin"/>.</summary>
    public bool IsOrigin => this == default;

    public static bool operator <(ArchiveLogPosition left, ArchiveLogPosition right) => left.CompareTo(right) < 0;

    public static bool operator >(ArchiveLogPosition left, ArchiveLogPosition right) => left.CompareTo(right) > 0;

    public static bool operator <=(ArchiveLogPosition left, ArchiveLogPosition right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ArchiveLogPosition left, ArchiveLogPosition right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Orders positions by generation, then base rows, then length, then units: within a key's chain positions strictly
    /// increase on <c>(Generation, Length)</c>, and a generation's base rows are fixed at its creation (archive-logs.md §7.2).
    /// </summary>
    public int CompareTo(ArchiveLogPosition other) =>
        Generation != other.Generation ? Generation.CompareTo(other.Generation)
        : BaseRows != other.BaseRows ? BaseRows.CompareTo(other.BaseRows)
        : Length != other.Length ? Length.CompareTo(other.Length)
        : Units.CompareTo(other.Units);

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({Generation}, {BaseRows}, {Length}, {Units})");
}
