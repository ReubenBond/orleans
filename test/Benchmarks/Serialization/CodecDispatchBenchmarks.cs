using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Benchmarks.Serialization.Models;
using Benchmarks.Serialization.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using OrleansCodeGen.Benchmarks.Serialization.Models;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Benchmarks.Serialization;

/// <summary>
/// Measures dispatch overhead when container codecs invoke generated element codecs.
/// </summary>
[Config(typeof(SerializerBenchmarkConfig))]
[BenchmarkCategory("Serialization", "CodecDispatch")]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class CodecDispatchBenchmarks
{
    private ServiceProvider _serviceProvider;
    private SerializerSession _session;
    private CopyContext _copyContext;
    private Codec_SerializerBenchmarkItem _codec;
    private IFieldCodec<SerializerBenchmarkItem> _codecInterface;
    private Copier_SerializerBenchmarkItem _copier;
    private IDeepCopier<SerializerBenchmarkItem> _copierInterface;
    private SerializerBenchmarkItem[] _values;
    private byte[] _destination;
    private byte[] _serialized;

    [Params(1, 16, 256)]
    public int ItemCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _serviceProvider = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
        _session = _serviceProvider.GetRequiredService<SerializerSessionPool>().GetSession();
        _copyContext = _serviceProvider.GetRequiredService<CopyContextPool>().GetContext();
        _codec = new();
        _codecInterface = _codec;
        _copier = new();
        _copierInterface = _copier;
        _values = CreateItems(ItemCount);
        _destination = new byte[ItemCount * 128];
        var length = SerializeConcreteCore(_codec, _values, _destination, _session);
        _serialized = _destination.AsSpan(0, length).ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _copyContext.Dispose();
        _session.Dispose();
        _serviceProvider.Dispose();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Serialize")]
    public int SerializeConcrete() => SerializeConcreteCore(_codec, _values, _destination, _session);

    [Benchmark]
    [BenchmarkCategory("Serialize")]
    public int SerializeInterface() => SerializeInterfaceCore(_codecInterface, _values, _destination, _session);

    [Benchmark]
    [BenchmarkCategory("Serialize")]
    public int SerializeConstrained() => SerializeConstrained(_codec, _values, _destination, _session);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Deserialize")]
    public int DeserializeConcrete() => DeserializeConcreteCore(_codec, _serialized, ItemCount, _session);

    [Benchmark]
    [BenchmarkCategory("Deserialize")]
    public int DeserializeInterface() => DeserializeInterfaceCore(_codecInterface, _serialized, ItemCount, _session);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("DeepCopy")]
    public int DeepCopyConcrete() => DeepCopyConcreteCore(_copier, _values, _copyContext);

    [Benchmark]
    [BenchmarkCategory("DeepCopy")]
    public int DeepCopyInterface() => DeepCopyInterfaceCore(_copierInterface, _values, _copyContext);

    private static int SerializeConcreteCore(
        Codec_SerializerBenchmarkItem codec,
        SerializerBenchmarkItem[] values,
        byte[] destination,
        SerializerSession session)
    {
        session.Reset();
        var writer = Writer.Create(destination, session);
        foreach (var value in values)
        {
            codec.WriteField(ref writer, 0, typeof(SerializerBenchmarkItem), value);
        }

        writer.Commit();
        return writer.Position;
    }

    private static int SerializeInterfaceCore(
        IFieldCodec<SerializerBenchmarkItem> codec,
        SerializerBenchmarkItem[] values,
        byte[] destination,
        SerializerSession session)
    {
        session.Reset();
        var writer = Writer.Create(destination, session);
        foreach (var value in values)
        {
            codec.WriteField(ref writer, 0, typeof(SerializerBenchmarkItem), value);
        }

        writer.Commit();
        return writer.Position;
    }

    private static int SerializeConstrained<TCodec>(
        TCodec codec,
        SerializerBenchmarkItem[] values,
        byte[] destination,
        SerializerSession session)
        where TCodec : IFieldCodec<SerializerBenchmarkItem>
    {
        session.Reset();
        var writer = Writer.Create(destination, session);
        foreach (var value in values)
        {
            WriteField(codec, ref writer, value);
        }

        writer.Commit();
        return writer.Position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteField<TCodec, TBufferWriter>(
        TCodec codec,
        ref Writer<TBufferWriter> writer,
        SerializerBenchmarkItem value)
        where TCodec : IFieldCodec<SerializerBenchmarkItem>
        where TBufferWriter : IBufferWriter<byte>
        => codec.WriteField(ref writer, 0, typeof(SerializerBenchmarkItem), value);

    private static int DeserializeConcreteCore(
        Codec_SerializerBenchmarkItem codec,
        byte[] source,
        int itemCount,
        SerializerSession session)
    {
        session.Reset();
        var reader = Reader.Create(source, session);
        var result = 0;
        for (var i = 0; i < itemCount; i++)
        {
            var field = reader.ReadFieldHeader();
            result += codec.ReadValue(ref reader, field).Quantity;
        }

        return result;
    }

    private static int DeserializeInterfaceCore(
        IFieldCodec<SerializerBenchmarkItem> codec,
        byte[] source,
        int itemCount,
        SerializerSession session)
    {
        session.Reset();
        var reader = Reader.Create(source, session);
        var result = 0;
        for (var i = 0; i < itemCount; i++)
        {
            var field = reader.ReadFieldHeader();
            result += codec.ReadValue(ref reader, field).Quantity;
        }

        return result;
    }

    private static int DeepCopyConcreteCore(
        Copier_SerializerBenchmarkItem copier,
        SerializerBenchmarkItem[] values,
        CopyContext context)
    {
        context.Reset();
        var result = 0;
        foreach (var value in values)
        {
            result += copier.DeepCopy(value, context).Quantity;
        }

        return result;
    }

    private static int DeepCopyInterfaceCore(
        IDeepCopier<SerializerBenchmarkItem> copier,
        SerializerBenchmarkItem[] values,
        CopyContext context)
    {
        context.Reset();
        var result = 0;
        foreach (var value in values)
        {
            result += copier.DeepCopy(value, context).Quantity;
        }

        return result;
    }

    private static SerializerBenchmarkItem[] CreateItems(int count)
    {
        var result = new SerializerBenchmarkItem[count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new SerializerBenchmarkItem
            {
                ProductId = i,
                Sku = $"sku-{i:D6}",
                Quantity = i % 25 + 1,
                UnitPrice = 199 + i * 100,
                IsBackordered = i % 7 == 0,
            };
        }

        return result;
    }
}
