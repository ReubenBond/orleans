using Orleans.Durable.Storage.Engine.Archive;

namespace Orleans.Durable.Storage.Testing;

/// <summary>A scanned live row with its key, as a test's drain of a whole segment, which holds rows of many keys, keeps it.</summary>
/// <param name="Key">The row's key.</param>
/// <param name="Row">The row, which owns a lease on its body.</param>
public readonly record struct KeyedRow(byte[] Key, ArchiveRow Row) : IDisposable
{
    /// <summary>Releases the row's body.</summary>
    public void Dispose() => Row.Dispose();

    /// <summary>
    /// Groups one segment's rows, in tag order, by key, in the order each key first appears: each key's rows, which
    /// <paramref name="drain"/> takes one key at a time, as a drain writes each key's write of the segment.
    /// </summary>
    public static void DrainByKey(ReadOnlySpan<KeyedRow> rows, Action<byte[], ArchiveRow[]> drain)
    {
        ArgumentNullException.ThrowIfNull(drain);
        var keys = new List<byte[]>();
        var byKey = new Dictionary<string, List<ArchiveRow>>(StringComparer.Ordinal);
        foreach (var (key, row) in rows)
        {
            var name = Convert.ToHexString(key);
            if (!byKey.TryGetValue(name, out var list))
            {
                byKey.Add(name, list = []);
                keys.Add(key);
            }

            list.Add(row);
        }

        foreach (var key in keys)
        {
            drain(key, [.. byKey[Convert.ToHexString(key)]]);
        }
    }

    /// <summary>Releases every row's body.</summary>
    public static void Release(IEnumerable<KeyedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            row.Dispose();
        }
    }
}
