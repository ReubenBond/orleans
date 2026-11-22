using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;

namespace Orleans.Durable.Storage.Hosting;

/// <summary>
/// The node's WAL release (orleans.md §5.9, OR-I13): the retention target <c>T</c> near which the node keeps its WAL, the
/// resource distance <c>F</c>, and the WAL-release rolls it requests.
/// </summary>
internal static class WalRelease
{
    /// <summary>
    /// Returns the node's WAL retention target <c>T</c> from its option: the configured bytes, or the smaller of 50 GiB and
    /// half the node's disk budget.
    /// </summary>
    public static long Retention(DurableStorageOptions options, long budgetBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.WalRetentionBytes ?? Math.Min(50L << 30, budgetBytes / 2);
    }

    /// <summary>
    /// Returns the resource distance <c>F</c>, in WAL files, at which the node's oldest replicas are rolled for WAL
    /// release: <c>max(1, T / (2 · WalFileSize))</c>.
    /// </summary>
    public static ulong Distance(long retentionBytes, long walFileSize) => (ulong)Math.Max(1, retentionBytes / (2 * walFileSize));

    /// <summary>
    /// Returns the ledgers whose WAL-release rolls the node requests (OR-I13): every live replica at the lowest first file,
    /// once that file is at least <paramref name="distance"/> files behind the current one, and only while each of their
    /// ledgers is open in <paramref name="map"/>. Rolling only some of them would free no file; while one is closed, its
    /// drain releases the file, and none is rolled.
    /// </summary>
    /// <param name="oldestFirst">The node's live replicas, oldest first (<see cref="LedgerStore.ListRetention"/>).</param>
    /// <param name="currentFile">The WAL file appends go to.</param>
    /// <param name="distance">The resource distance <c>F</c>, in files.</param>
    /// <param name="map">The node's view of the ledger map.</param>
    public static IReadOnlyList<LedgerId> Rolls(IReadOnlyList<LedgerReplicaRetention> oldestFirst, ulong currentFile, ulong distance, LedgerMapValue map)
    {
        ArgumentNullException.ThrowIfNull(oldestFirst);
        ArgumentNullException.ThrowIfNull(map);
        Debug.Assert(oldestFirst.Zip(oldestFirst.Skip(1)).All(static pair => pair.First.FirstFile <= pair.Second.FirstFile), "ListRetention lists the oldest replica first.");
        if (oldestFirst is not [var oldest, ..] || currentFile < oldest.FirstFile + distance)
        {
            return [];
        }

        var tied = oldestFirst.TakeWhile(replica => replica.FirstFile == oldest.FirstFile).ToList();
        return tied.All(replica => map.Get(replica.Ledger) is { IsOpen: true }) ? [.. tied.Select(static replica => replica.Ledger)] : [];
    }
}
