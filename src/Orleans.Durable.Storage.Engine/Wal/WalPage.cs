namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// Identifies a data page of a write-ahead log by its file and its 0-based data page index.
/// </summary>
/// <remarks>
/// Data page <see cref="Index"/> starts at byte <c>P × (1 + Index)</c> of its file, where P is the file's page
/// size. A record's page is the page holding its first frame. Pages order by file, then index, which is the order
/// of the records starting on them.
/// </remarks>
/// <param name="File">The file number.</param>
/// <param name="Index">The data page index within the file.</param>
public readonly record struct WalPage(ulong File, uint Index) : IComparable<WalPage>
{
    public static bool operator <(WalPage left, WalPage right) => left.CompareTo(right) < 0;

    public static bool operator >(WalPage left, WalPage right) => left.CompareTo(right) > 0;

    public static bool operator <=(WalPage left, WalPage right) => left.CompareTo(right) <= 0;

    public static bool operator >=(WalPage left, WalPage right) => left.CompareTo(right) >= 0;

    /// <inheritdoc/>
    public int CompareTo(WalPage other)
    {
        var file = File.CompareTo(other.File);
        return file != 0 ? file : Index.CompareTo(other.Index);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{File}#{Index}";
}
