using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Serialization;
using Xunit;

namespace Benchmarks.Manifest;

/// <summary>
/// Compares payload construction during homogeneous or heterogeneous cluster formation.
/// RPC and dissemination envelopes are excluded, but repeated pull responses and aggregate snapshots are included.
/// </summary>
[Trait("Category", "Benchmark")]
[MemoryDiagnoser]
public class ClusterManifestDisseminationBenchmark
{
    private readonly Serializer _serializer;
    private ClusterManifestUpdate[] _aggregateSnapshots = [];
    private ManifestHash[] _references = [];
    private KeyValuePair<ManifestHash, GrainManifest>[] _contents = [];

    public ClusterManifestDisseminationBenchmark()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        _serializer = services.GetRequiredService<Serializer>();
    }

    [Params(10, 100, 500)]
    public int SiloCount { get; set; }

    [Params(1, 3)]
    public int ManifestVariantCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var variants = Enumerable.Range(0, ManifestVariantCount)
            .Select(CreateManifest)
            .ToArray();
        _contents = variants
            .Select(manifest => KeyValuePair.Create(ManifestHashCalculator.ComputeHash(manifest), manifest))
            .ToArray();

        var silos = ImmutableDictionary.CreateBuilder<SiloAddress, GrainManifest>();
        _aggregateSnapshots = new ClusterManifestUpdate[SiloCount];
        _references = new ManifestHash[SiloCount];
        for (var i = 0; i < SiloCount; i++)
        {
            var variant = i % variants.Length;
            silos.Add(SiloAddress.New(System.Net.IPAddress.Loopback, 10000 + i, i + 1), variants[variant]);
            _references[i] = _contents[variant].Key;
            _aggregateSnapshots[i] = new ClusterManifestUpdate(
                new MajorMinorVersion(i + 1, 0),
                silos.ToImmutable(),
                includesAllActiveServers: true);
        }
    }

    [Benchmark(Baseline = true)]
    public int AggregateSnapshot()
    {
        var bytes = 0;
        foreach (var snapshot in _aggregateSnapshots)
        {
            bytes += _serializer.SerializeToArray(snapshot).Length;
        }

        return bytes;
    }

    [Benchmark]
    public int ExistingPullResponses()
    {
        var bytes = 0;
        for (var receiver = 0; receiver < SiloCount; receiver++)
        {
            foreach (var reference in _references)
            {
                bytes += _serializer.SerializeToArray(reference).Length;
            }

            foreach (var content in _contents)
            {
                bytes += _serializer.SerializeToArray(content.Value).Length;
            }
        }

        return bytes;
    }

    [Benchmark]
    public int ContentAddressedValues()
    {
        var bytes = 0;
        foreach (var reference in _references)
        {
            bytes += _serializer.SerializeToArray(new ClusterManifestReference(reference)).Length;
        }

        foreach (var content in _contents)
        {
            bytes += _serializer.SerializeToArray(new ClusterManifestContent(content.Key, content.Value)).Length;
        }

        return bytes;
    }

    private static GrainManifest CreateManifest(int variant)
    {
        var grains = ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
        for (var i = 0; i < 100; i++)
        {
            var properties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            properties.Add("type", $"Example.Grain{variant}_{i}");
            properties.Add("placement", "RandomPlacement");
            grains.Add(
                GrainType.Create($"grain-{variant}-{i}"),
                new GrainProperties(properties.ToImmutable()));
        }

        return new GrainManifest(
            grains.ToImmutable(),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
    }
}
