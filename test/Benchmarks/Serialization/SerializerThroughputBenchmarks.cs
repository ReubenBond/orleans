using BenchmarkDotNet.Attributes;
using Benchmarks.Serialization.Models;
using Benchmarks.Serialization.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using System.Diagnostics;

namespace Benchmarks.Serialization;

/// <summary>
/// Exercises representative Orleans request payloads using generated serializers.
/// </summary>
[Config(typeof(SerializerBenchmarkConfig))]
[BenchmarkCategory("Serialization")]
public class SerializerThroughputBenchmarks
{
    private Serializer<SerializerBenchmarkPayload> _serializer;
    private DeepCopier<SerializerBenchmarkPayload> _copier;
    private SerializerSession _session;
    private SerializerBenchmarkPayload _value;
    private byte[] _destination;
    private byte[] _serialized;

    [Params(1, 16, 256)]
    public int ItemCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var serviceProvider = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();

        _serializer = serviceProvider.GetRequiredService<Serializer<SerializerBenchmarkPayload>>();
        _copier = serviceProvider.GetRequiredService<DeepCopier<SerializerBenchmarkPayload>>();
        _session = serviceProvider.GetRequiredService<SerializerSessionPool>().GetSession();
        _value = CreatePayload(ItemCount);
        _serialized = _serializer.SerializeToArray(_value);
        _destination = new byte[_serialized.Length + 64];
    }

    [GlobalCleanup]
    public void Cleanup() => _session.Dispose();

    [Benchmark(Baseline = true)]
    public int SerializeWithSession()
    {
        _session.Reset();
        return _serializer.Serialize(_value, _destination, _session);
    }

    [Benchmark]
    public byte[] SerializeToArray() => _serializer.SerializeToArray(_value);

    [Benchmark]
    public SerializerBenchmarkPayload DeserializeWithSession()
    {
        _session.Reset();
        return _serializer.Deserialize(_serialized, _session);
    }

    [Benchmark]
    public SerializerBenchmarkPayload RoundTripWithSession()
    {
        _session.Reset();
        var length = _serializer.Serialize(_value, _destination, _session);
        _session.Reset();
        return _serializer.Deserialize(_destination.AsSpan(0, length), _session);
    }

    [Benchmark]
    public SerializerBenchmarkPayload DeepCopy() => _copier.Copy(_value);

    public static void Profile(string operation, TimeSpan duration)
    {
        var benchmark = new SerializerThroughputBenchmarks { ItemCount = 256 };
        benchmark.Setup();

        try
        {
            var stopwatch = Stopwatch.StartNew();
            long operations = 0;
            long checksum = 0;
            while (stopwatch.Elapsed < duration)
            {
                for (var i = 0; i < 1_024; i++)
                {
                    checksum += operation switch
                    {
                        "serialize" => benchmark.SerializeWithSession(),
                        "deserialize" => benchmark.DeserializeWithSession().Items.Length,
                        "roundtrip" => benchmark.RoundTripWithSession().Items.Length,
                        "copy" => benchmark.DeepCopy().Items.Length,
                        _ => throw new ArgumentException($"Unknown serializer profile operation: {operation}", nameof(operation)),
                    };
                }

                operations += 1_024;
            }

            Console.WriteLine($"{operation}: {operations:N0} operations in {stopwatch.Elapsed.TotalSeconds:F2}s; checksum={checksum}");
        }
        finally
        {
            benchmark.Cleanup();
        }
    }

    private static SerializerBenchmarkPayload CreatePayload(int itemCount)
    {
        var items = new SerializerBenchmarkItem[itemCount];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new SerializerBenchmarkItem
            {
                ProductId = i % 4 switch
                {
                    0 => i,
                    1 => 1L << 14 | (uint)i,
                    2 => 1L << 28 | (uint)i,
                    _ => 1L << 42 | (uint)i,
                },
                Sku = $"sku-{i:D6}",
                Quantity = i % 25 + 1,
                UnitPrice = 199 + i * 100,
                IsBackordered = i % 7 == 0,
            };
        }

        return new SerializerBenchmarkPayload
        {
            RequestId = new Guid("3ddf1f8f-8597-4c90-969f-bb6f0f4a5180"),
            TenantId = "contoso-westus-production",
            Timestamp = 1_784_302_558_723,
            Headers = new Dictionary<string, string>
            {
                ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                ["content-type"] = "application/octet-stream",
                ["region"] = "westus2",
                ["locale"] = "en-US",
            },
            Items = items,
            Body = Enumerable.Range(0, Math.Max(64, itemCount * 4)).Select(static i => (byte)i).ToArray(),
        };
    }
}
