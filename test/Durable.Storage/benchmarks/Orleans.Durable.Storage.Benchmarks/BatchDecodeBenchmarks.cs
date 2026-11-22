using System.Globalization;
using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures decoding a batch (ledgers.md §7), and decoding it and walking every row it lends, per batch and per row, from
/// the fewest pages and from 4 KiB pages. Every apply loop, scan, and drain decodes and walks every batch it reads.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(PerRowConfig))]
public class BatchDecodeBenchmarks
{
    private ArcBuffer _batch;

    /// <summary>Gets or sets the rows in the batch.</summary>
    [Params(1, 16, 256)]
    public int Rows { get; set; } = 1;

    /// <summary>Gets or sets each row's payload length, in bytes.</summary>
    [Params(16, 256, 4096)]
    public int PayloadLength { get; set; } = 16;

    /// <summary>
    /// Gets or sets how the decoded batch's bytes lie in pages: the fewest, as a writer which expects the batch's length
    /// gives them (one page up to 1 MiB), or the aligned 4 KiB pages of a read.
    /// </summary>
    [Params("fewest pages", "4 KiB pages")]
    public string Layout { get; set; } = "fewest pages";

    [GlobalSetup]
    public void Setup()
    {
        var rows = new Row[Rows];
        try
        {
            for (var rowIndex = 0; rowIndex < Rows; rowIndex++)
            {
                var key = new byte[16];
                _ = rowIndex.TryFormat(key, out _, "D16", CultureInfo.InvariantCulture);
                using var writer = new ArcBufferWriter(PayloadLength);
                writer.Write(new byte[PayloadLength]);
                using var payload = writer.ConsumeSlice(PayloadLength);
                rows[rowIndex] = new Row(key, (RowKind)(rowIndex % 3), payload);
            }

            using var encoded = BatchView.Encode(rows);
            using var paged = Layout == "fewest pages" ? new ArcBufferWriter(encoded.Length) : ArcBufferWriter.CreateAligned();
            encoded.CopyTo(paged);
            _batch = paged.ConsumeSlice(encoded.Length);
        }
        finally
        {
            foreach (var row in rows)
            {
                row.Dispose();
            }
        }

        if (BatchView.Decode(_batch, out _) is { } rejection)
        {
            throw new InvalidOperationException("The benchmark's batch does not decode.", rejection);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _batch.Dispose();

    [Benchmark]
    public int Decode()
    {
        _ = BatchView.Decode(_batch, out var view);
        return view.Count;
    }

    [Benchmark]
    public long DecodeAndWalk()
    {
        _ = BatchView.Decode(_batch, out var view);
        var bytes = 0L;
        foreach (var row in view)
        {
            bytes += (row.IsDefining ? row.Key.Length : 0) + row.Body.Length;
        }

        return bytes;
    }
}
