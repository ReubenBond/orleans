using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures the catalog at a year's tombstones: decoding a <c>CLSC</c> catalog checkpoint, a tombstone lookup, and the
/// open's check that the derived catalog follows the checkpoint it read, against an equal catalog built apart from it.
/// The tombstones sit in an <see cref="ImmutableSortedSet{T}"/>; these cases are the measurement which would justify a
/// denser representation (deferred.md).
/// </summary>
[MemoryDiagnoser]
public class ReplicaCatalogBenchmarks
{
    private static readonly WalPosition Cut = new(900, 4096);

    private byte[] _bytes = null!;
    private ReplicaCatalog _catalog = null!;
    private ReplicaCatalog _copy = null!;
    private LedgerId _probe;

    /// <summary>
    /// Gets or sets the number of tombstones the catalog holds.
    /// </summary>
    [Params(1_000_000)]
    public int Tombstones { get; set; } = 1_000_000;

    [GlobalSetup]
    public void Setup()
    {
        var tombstones = Enumerable.Range(1, Tombstones).Select(static value => new LedgerId((ulong)value * 3)).ToImmutableSortedSet();
        CatalogReplica[] replicas = [.. Enumerable.Range(0, 1_000).Select(static index => new CatalogReplica((uint)index, 64, 800, new LedgerId(10_000_000 + (ulong)index)))];
        _catalog = new ReplicaCatalog((uint)replicas.Length, replicas, tombstones);
        _bytes = _catalog.Encode(Cut);
        _copy = ((CatalogCheckpoint)ReplicaCatalog.Decode(_bytes, Cut).Value!).Catalog;
        _probe = new LedgerId((ulong)Tombstones / 2 * 3);
    }

    [Benchmark]
    public int Decode() => ((CatalogCheckpoint)ReplicaCatalog.Decode(_bytes, Cut).Value!).Catalog.Tombstones.Count;

    [Benchmark]
    public bool IsDropped() => _catalog.IsDropped(_probe);

    [Benchmark]
    public bool Follows() => _copy.Follows(_catalog);
}
