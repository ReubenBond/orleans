using BenchmarkDotNet.Attributes;
using Benchmarks.Serialization.Models;
using Benchmarks.Serialization.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Session;

namespace Benchmarks.Serialization;

/// <summary>
/// Measures generated serializer access to mutable, private, readonly, init-only, and get-only members.
/// </summary>
[Config(typeof(SerializerBenchmarkConfig))]
[BenchmarkCategory("Serialization", "MemberAccess")]
public class MemberAccessBenchmarks
{
    private ServiceProvider _serviceProvider;
    private BenchmarkState<PublicMutableMemberPayload> _publicMutable;
    private BenchmarkState<PrivateFieldMemberPayload> _privateFields;
    private BenchmarkState<InitOnlyMemberPayload> _initOnly;
    private BenchmarkState<GetOnlyMemberPayload> _getOnly;

    [Params(1, 16, 256)]
    public int ItemCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _serviceProvider = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();

        _publicMutable = new(_serviceProvider, CreateItems(ItemCount, PublicMutableMemberPayload.Create));
        _privateFields = new(_serviceProvider, CreateItems(ItemCount, PrivateFieldMemberPayload.Create));
        _initOnly = new(_serviceProvider, CreateItems(ItemCount, InitOnlyMemberPayload.Create));
        _getOnly = new(_serviceProvider, CreateItems(ItemCount, GetOnlyMemberPayload.Create));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _publicMutable.Dispose();
        _privateFields.Dispose();
        _initOnly.Dispose();
        _getOnly.Dispose();
        _serviceProvider.Dispose();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Serialize")]
    public int SerializePublicMutable() => _publicMutable.Serialize();

    [Benchmark]
    [BenchmarkCategory("Serialize")]
    public int SerializePrivateFields() => _privateFields.Serialize();

    [Benchmark]
    [BenchmarkCategory("Serialize")]
    public int SerializeInitOnly() => _initOnly.Serialize();

    [Benchmark]
    [BenchmarkCategory("Serialize")]
    public int SerializeGetOnly() => _getOnly.Serialize();

    [Benchmark]
    [BenchmarkCategory("Deserialize")]
    public PublicMutableMemberPayload[] DeserializePublicMutable() => _publicMutable.Deserialize();

    [Benchmark]
    [BenchmarkCategory("Deserialize")]
    public PrivateFieldMemberPayload[] DeserializePrivateFields() => _privateFields.Deserialize();

    [Benchmark]
    [BenchmarkCategory("Deserialize")]
    public InitOnlyMemberPayload[] DeserializeInitOnly() => _initOnly.Deserialize();

    [Benchmark]
    [BenchmarkCategory("Deserialize")]
    public GetOnlyMemberPayload[] DeserializeGetOnly() => _getOnly.Deserialize();

    [Benchmark]
    [BenchmarkCategory("DeepCopy")]
    public PublicMutableMemberPayload[] DeepCopyPublicMutable() => _publicMutable.DeepCopy();

    [Benchmark]
    [BenchmarkCategory("DeepCopy")]
    public PrivateFieldMemberPayload[] DeepCopyPrivateFields() => _privateFields.DeepCopy();

    [Benchmark]
    [BenchmarkCategory("DeepCopy")]
    public InitOnlyMemberPayload[] DeepCopyInitOnly() => _initOnly.DeepCopy();

    [Benchmark]
    [BenchmarkCategory("DeepCopy")]
    public GetOnlyMemberPayload[] DeepCopyGetOnly() => _getOnly.DeepCopy();

    private static T[] CreateItems<T>(int count, Func<T> factory)
    {
        var result = new T[count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = factory();
        }

        return result;
    }

    private sealed class BenchmarkState<T> : IDisposable
    {
        private readonly Serializer<T[]> _serializer;
        private readonly DeepCopier<T[]> _copier;
        private readonly SerializerSession _session;
        private readonly T[] _value;
        private readonly byte[] _serialized;
        private readonly byte[] _destination;

        public BenchmarkState(IServiceProvider serviceProvider, T[] value)
        {
            _serializer = serviceProvider.GetRequiredService<Serializer<T[]>>();
            _copier = serviceProvider.GetRequiredService<DeepCopier<T[]>>();
            _session = serviceProvider.GetRequiredService<SerializerSessionPool>().GetSession();
            _value = value;
            _serialized = _serializer.SerializeToArray(value);
            _destination = new byte[_serialized.Length + 64];
        }

        public int Serialize()
        {
            _session.Reset();
            return _serializer.Serialize(_value, _destination, _session);
        }

        public T[] Deserialize()
        {
            _session.Reset();
            return _serializer.Deserialize(_serialized, _session);
        }

        public T[] DeepCopy() => _copier.Copy(_value);

        public void Dispose() => _session.Dispose();
    }
}
