using System.Buffers;
using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Benchmarks;

[MemoryDiagnoser]
public class BufferWriteBenchmarks
{
    private byte[] _payload = null!;

    [Params(128, 4096, 65_536)]
    public int PayloadLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = GC.AllocateUninitializedArray<byte>(PayloadLength);
    }

    [Benchmark(Baseline = true)]
    public int ArrayBufferWriter()
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write(_payload);
        return writer.WrittenCount;
    }

    [Benchmark]
    public int PooledArcBuffer()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(_payload);
        return writer.Length;
    }

    [Benchmark]
    public int PooledArcBufferPresized()
    {
        using var writer = new ArcBufferWriter(PayloadLength);
        writer.Write(_payload);
        return writer.Length;
    }
}
