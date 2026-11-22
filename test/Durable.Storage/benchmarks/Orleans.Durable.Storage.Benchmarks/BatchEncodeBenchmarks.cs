using System.Globalization;
using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures encoding rows into a batch (ledgers.md §7), per batch and per row, from rows already built and from rows
/// built and disposed with each batch, as a primary builds them from the entries it proposes.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(PerRowConfig))]
public class BatchEncodeBenchmarks
{
    private byte[][] _keys = null!;
    private ArcBuffer[] _payloads = null!;
    private Row[] _rows = null!;
    private Row[] _built = null!;

    /// <summary>Gets or sets the rows in the batch.</summary>
    [Params(1, 16, 256)]
    public int Rows { get; set; } = 1;

    /// <summary>Gets or sets each row's payload length, in bytes.</summary>
    [Params(16, 256, 4096)]
    public int PayloadLength { get; set; } = 16;

    [GlobalSetup]
    public void Setup()
    {
        _keys = new byte[Rows][];
        _payloads = new ArcBuffer[Rows];
        _rows = new Row[Rows];
        _built = new Row[Rows];
        for (var rowIndex = 0; rowIndex < Rows; rowIndex++)
        {
            _keys[rowIndex] = new byte[16];
            _ = rowIndex.TryFormat(_keys[rowIndex], out _, "D16", CultureInfo.InvariantCulture);
            using var writer = new ArcBufferWriter(PayloadLength);
            writer.Write(new byte[PayloadLength]);
            _payloads[rowIndex] = writer.ConsumeSlice(PayloadLength);
            _rows[rowIndex] = new Row(_keys[rowIndex], Kind(rowIndex), _payloads[rowIndex]);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var row in _rows)
        {
            row.Dispose();
        }

        foreach (var payload in _payloads)
        {
            payload.Dispose();
        }
    }

    [Benchmark]
    public int Encode()
    {
        using var batch = BatchView.Encode(_rows);
        return batch.Length;
    }

    /// <summary>Builds each row, copying its key and retaining its payload, encodes them, and disposes them.</summary>
    [Benchmark]
    public int BuildAndEncode()
    {
        for (var rowIndex = 0; rowIndex < Rows; rowIndex++)
        {
            _built[rowIndex] = new Row(_keys[rowIndex], Kind(rowIndex), _payloads[rowIndex]);
        }

        try
        {
            using var batch = BatchView.Encode(_built);
            return batch.Length;
        }
        finally
        {
            foreach (var row in _built)
            {
                row.Dispose();
            }
        }
    }

    private static RowKind Kind(int rowIndex) => (RowKind)(rowIndex % 3);
}
