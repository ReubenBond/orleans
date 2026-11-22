using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures <c>CLOE</c> v1 extent tombstone encoding and decoding (object-ledgers.md §7.5, §10).
/// </summary>
[MemoryDiagnoser]
public class ObjectLedgerTombstoneCodecBenchmarks
{
    [Benchmark]
    public long EncodeAndDecodeTombstone()
    {
        var encoded = ObjectLedgerExtentFormat.EncodeTombstone(new Slot(42));
        var decoded = ObjectLedgerExtentFormat.DecodeTombstone(encoded);
        return decoded switch
        {
            Slot slot => slot.Value,
            InvalidDataException error => throw new InvalidOperationException("CLOE tombstone decode failed.", error),
            null => throw new InvalidOperationException("Unexpected null decode result."),
        };
    }
}
