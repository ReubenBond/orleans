using BenchmarkDotNet.Attributes;
using Benchmarks.Models;
using Benchmarks.Utilities;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Buffers;
using Xunit;
using System.Linq;

namespace Benchmarks
{
    [Trait("Category", "Benchmark")]
    [Config(typeof(BenchmarkConfig))]
    [MemoryDiagnoser]
    public class ComplexTypeBenchmarks
    {
        private static SingleSegmentBuffer Buffer = new(new byte[1000]);
        private static byte[] _buffer = new byte[1000];
        private readonly Serializer<SimpleStruct> _structSerializer;
        private readonly DeepCopier<SimpleStruct> _structCopier;
        private readonly Serializer<ComplexClass> _serializer;
        private readonly DeepCopier<ComplexClass> _copier;
        private readonly SerializerSessionPool _sessionPool;
        private readonly ComplexClass _value;
        private readonly SerializerSession _session;
        private readonly byte[] _serializedPayload;
        private readonly byte[] _structSerializedPayload;
        private SimpleStruct _structValue;

        public ComplexTypeBenchmarks()
        {
            var services = new ServiceCollection();
            _ = services
                .AddSerializer();
            var serviceProvider = services.BuildServiceProvider();
            _serializer = serviceProvider.GetRequiredService<Serializer<ComplexClass>>();
            _copier = serviceProvider.GetRequiredService<DeepCopier<ComplexClass>>();
            _structSerializer = serviceProvider.GetRequiredService<Serializer<SimpleStruct>>();
            _structCopier = serviceProvider.GetRequiredService<DeepCopier<SimpleStruct>>();
            _sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
            _value = new ComplexClass
            {
                BaseInt = 192,
                Int = 501,
                String = "bananas",
                Array = Enumerable.Range(0, 60).ToArray(),
                MultiDimensionalArray = new[,] {{0, 2, 4}, {1, 5, 6}}
            };
            _value.AlsoSelf = _value.BaseSelf = _value.Self = _value;

            _structValue = new SimpleStruct
            {
                Int = 42,
                Bool = true,
                Guid = Guid.NewGuid()
            };
            _session = _sessionPool.GetSession();

            _serializedPayload = _serializer.SerializeToArray(_value);
            _structSerializedPayload  = _structSerializer.SerializeToArray(_structValue);
        }

        [Fact]
        [Benchmark]
        public void RoundTripComplexClass()
        {
            var writer = Buffer.CreateWriter(_session);
            _session.FullReset();
            _serializer.Serialize(_value, ref writer);

            _session.FullReset();
            var reader = Reader.Create(writer.Output.GetReadOnlySequence(), _session);
            _ = _serializer.Deserialize(ref reader);
            Buffer.Reset();
        }

        [Fact]
        public void CopyComplexClass() => _copier.Copy(_value); 

        [Fact]
        public void CopySimpleStruct() => _structCopier.Copy(_structValue);

        [Fact]
        [Benchmark]
        public SimpleStruct RoundTripSimpleStruct()
        {
            var writer = Buffer.CreateWriter(_session);
            _session.FullReset();
            _structSerializer.Serialize(_structValue, ref writer);

            _session.FullReset();
            var reader = Reader.Create(writer.Output.GetReadOnlySequence(), _session);
            var result = _structSerializer.Deserialize(ref reader);
            Buffer.Reset();
            return result;
        }

        [Fact]
        [Benchmark]
        public long SerializeComplexClass() => _serializer.Serialize(_value, _buffer);

        [Fact]
        [Benchmark]
        public object DeserializeComplexClass() => _serializer.Deserialize(_serializedPayload);

        [Fact]
        [Benchmark]
        public long SerializeSimpleStruct() => _structSerializer.Serialize(_structValue, _buffer);

        [Fact]
        [Benchmark]
        public SimpleStruct DeserializeSimpleStruct() => _structSerializer.Deserialize(_structSerializedPayload);
    }
}
