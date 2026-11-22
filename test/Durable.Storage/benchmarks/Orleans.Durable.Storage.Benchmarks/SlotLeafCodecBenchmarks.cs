using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures the <c>CLSI</c> leaf codec per 256-slot leaf: validating a loaded leaf, decoding it whole or entry by entry,
/// and encoding it. Recovery cost scales with validation, and point reads and thaws with decoding.
/// </summary>
[MemoryDiagnoser]
public class SlotLeafCodecBenchmarks
{
    private readonly SlotEntry[] _decoded = new SlotEntry[SlotLeafCodec.Slots];
    private SlotEntry[] _entries = null!;
    private byte[] _bytes = null!;
    private FrozenLeaf _leaf = null!;

    /// <summary>
    /// Gets or sets the leaf's shape: one record per page, records packed several to a page by concurrent appends, or
    /// a placed leaf with repaired outliers stored as exceptions.
    /// </summary>
    [Params("sequential", "packed", "outliers")]
    public string Shape { get; set; } = "sequential";

    [GlobalSetup]
    public void Setup()
    {
        _entries = new SlotEntry[SlotLeafCodec.Slots];
        for (var index = 0; index < _entries.Length; index++)
        {
            var page = Shape == "packed" ? 50_000 + (uint)(index / 7) : 50_000 + (uint)index;
            _entries[index] = new SlotEntry(index % 61 == 0 ? SlotKind.Faulty : SlotKind.Data, new WalPage(12, page));
        }

        if (Shape == "outliers")
        {
            _entries[3] = new SlotEntry(SlotKind.Data, new WalPage(19, 7));
            _entries[200] = new SlotEntry(SlotKind.Data, new WalPage(12, 9));
            _entries[201] = default;
        }

        _bytes = SlotLeafCodec.Encode(_entries);
        _leaf = (FrozenLeaf)FrozenLeaf.Decode(_bytes).Value!;
    }

    [Benchmark]
    public int Decode() => ((FrozenLeaf)FrozenLeaf.Decode(_bytes).Value!).Count;

    [Benchmark]
    public uint DecodeAll()
    {
        _leaf.DecodeAll(_decoded);
        return _decoded[255].Page.Index;
    }

    [Benchmark]
    public ulong GetEach()
    {
        ulong sum = 0;
        for (var index = 0; index < SlotLeafCodec.Slots; index++)
        {
            sum += _leaf.Get(index).Page.Index;
        }

        return sum;
    }

    [Benchmark]
    public int Encode() => SlotLeafCodec.Encode(_entries).Length;
}
