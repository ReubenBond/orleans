namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// Identifies a byte in a write-ahead log by its file and its offset within the file.
/// </summary>
/// <remarks>
/// Positions order by file, then offset. Files and offsets are never reused, so this order is the order in
/// which records were admitted. A record's position is the offset of its first frame word.
/// </remarks>
/// <param name="File">The file number.</param>
/// <param name="Offset">The byte offset within the file.</param>
public readonly record struct WalPosition(ulong File, ulong Offset) : IComparable<WalPosition>
{
    public static bool operator <(WalPosition left, WalPosition right) => left.CompareTo(right) < 0;

    public static bool operator >(WalPosition left, WalPosition right) => left.CompareTo(right) > 0;

    public static bool operator <=(WalPosition left, WalPosition right) => left.CompareTo(right) <= 0;

    public static bool operator >=(WalPosition left, WalPosition right) => left.CompareTo(right) >= 0;

    /// <inheritdoc/>
    public int CompareTo(WalPosition other)
    {
        var file = File.CompareTo(other.File);
        return file != 0 ? file : Offset.CompareTo(other.Offset);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{File}:{Offset}";
}
