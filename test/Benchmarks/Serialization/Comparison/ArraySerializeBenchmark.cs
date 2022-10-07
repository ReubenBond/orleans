using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Benchmarks.Serialization.Models;
using Benchmarks.Utilities;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Benchmarks.Serialization.Comparison;

#pragma warning disable IDE1006 // Naming Styles
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Config(typeof(BenchmarkConfig))]
public class ArrayDeserializeBenchmark
{
    private static readonly MyVector3[] _value;
    private static readonly Serializer<MyVector3[]> _orleansSerializer;
    private static readonly byte[] _stjPayload;
    private static readonly byte[] _protobufPayload;
    private static readonly byte[] _orleansPayload;
    private static readonly byte[] _messagePackPayload;
    private static readonly ReadOnlySequence<byte> _orleansReadOnlySequencePayload;
    private static PooledBuffer.BufferSlice _orleansPooledBufferPayload;

    static ArrayDeserializeBenchmark()
    {
        _value = Enumerable.Repeat(new MyVector3 { X = 10.3f, Y = 40.5f, Z = 13411.3f }, 1000).ToArray();
        var serviceProvider = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(ArraySerializeBenchmark).Assembly))
            .BuildServiceProvider();
        _orleansSerializer = serviceProvider.GetRequiredService<Serializer<MyVector3[]>>();


        _orleansPayload = _orleansSerializer.SerializeToArray(_value);
        _messagePackPayload = MessagePackSerializer.Serialize(_value);

        var stream = new MemoryStream();
        ProtoBuf.Serializer.Serialize(stream, _value);
        stream.Position = 0;
        _protobufPayload = stream.ToArray();
        _stjPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_value));

        {
            var pipe = new Pipe();
            pipe.Writer.Write(_orleansPayload);
            pipe.Writer.FlushAsync().GetAwaiter().GetResult();
            var res = pipe.Reader.ReadAsync().GetAwaiter().GetResult();
            _orleansReadOnlySequencePayload = res.Buffer;
        }

        {
            var buffer = new PooledBuffer();
            buffer.Write(_orleansPayload);
            _orleansPooledBufferPayload = buffer.Slice();
        }
    }

    [Benchmark(Baseline = true)]
    public MyVector3[] MessagePackDeserialize() => MessagePackSerializer.Deserialize<MyVector3[]>(_messagePackPayload);

    [Benchmark]
    public MyVector3[] ProtobufNetDeserialize() => ProtoBuf.Serializer.Deserialize<MyVector3[]>(_protobufPayload.AsSpan());

    [Benchmark]
    public MyVector3[] SystemTextJsonDeserialize() => JsonSerializer.Deserialize<MyVector3[]>(_stjPayload);

    [Benchmark]
    public MyVector3[] OrleansDeserialize() => _orleansSerializer.Deserialize(_orleansPayload);

    [Benchmark]
    public MyVector3[] OrleansPipeDeserialize() => _orleansSerializer.Deserialize(_orleansReadOnlySequencePayload);

    [Benchmark]
    public MyVector3[] OrleansPooledBufferDeserialize() => _orleansSerializer.Deserialize(_orleansPooledBufferPayload);
}
#pragma warning restore IDE1006 // Naming Styles
