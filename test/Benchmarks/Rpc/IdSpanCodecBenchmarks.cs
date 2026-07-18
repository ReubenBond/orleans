using System.Buffers;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Benchmarks.Rpc;

[Config(typeof(RpcBenchmarkConfig))]
[BenchmarkCategory("Rpc", "Serialization")]
public class IdSpanCodecBenchmarks
{
    private byte[] _encoded;
    private CachingIdSpanCodec _codec;
    private ArrayBufferWriter<byte> _output;
    private SerializerSession _session;
    private IdSpan _value;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        _session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        _codec = new CachingIdSpanCodec();
        _output = new ArrayBufferWriter<byte>();
        _value = IdSpan.Create("benchmark-grain-type");

        var writer = Writer.Create(_output, _session);
        IdSpanCodec.WriteRaw(ref writer, _value);
        writer.Commit();
        _encoded = _output.WrittenSpan.ToArray();

        _ = ReadHot();
        _ = WriteHot();
    }

    [Benchmark]
    public IdSpan ReadHot()
    {
        var reader = Reader.Create(_encoded.AsSpan(), _session);
        return _codec.ReadRaw(ref reader);
    }

    [Benchmark]
    public int WriteHot()
    {
        _output.Clear();
        var writer = Writer.Create(_output, _session);
        _codec.WriteRaw(ref writer, _value);
        writer.Commit();
        return _output.WrittenCount;
    }
}
