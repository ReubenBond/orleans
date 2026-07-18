using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

internal interface IClusterManifestDisseminationParticipant
{
    ImmutableDictionary<SiloAddress, ManifestHash> GetManifestReferencesForDissemination();

    ImmutableDictionary<ManifestHash, GrainManifest> GetManifestContentsForDissemination();

    DisseminationApplyResult ApplyDisseminatedManifestReference(SiloAddress siloAddress, ManifestHash manifestHash);

    DisseminationApplyResult ApplyDisseminatedManifestContent(ManifestHash manifestHash, GrainManifest manifest);
}

// A silo's manifest is immutable for its generation, so each address owns one version-1 content reference.
internal sealed class ClusterManifestDisseminationNamespace(
    IClusterManifestDisseminationParticipant clusterManifestProvider,
    IOptions<SiloMessagingOptions> options,
    Serializer serializer) : IDisseminationNamespace
{
    private const long ManifestVersion = 1;
    private readonly object _cacheLock = new();
    private readonly Dictionary<SiloAddress, (ManifestHash Hash, ReadOnlyMemory<byte> Payload)> _cachedValues = [];

    public DisseminationNamespace Name => DisseminationNamespaceNames.ClusterManifest;

    public DisseminationNamespaceOptions Options => options.Value.ClusterManifestDissemination;

    public IEnumerable<DigestEntry> Digests
    {
        get
        {
            var references = clusterManifestProvider.GetManifestReferencesForDissemination();
            PruneCache(references.Keys);
            foreach (var siloAddress in references.Keys)
            {
                yield return new DigestEntry(siloAddress, ManifestVersion);
            }
        }
    }

    public async ValueTask<bool> PublishAsync(
        IDisseminationService disseminationService,
        SiloAddress siloAddress,
        CancellationToken cancellationToken)
    {
        if (!clusterManifestProvider.GetManifestReferencesForDissemination().ContainsKey(siloAddress))
        {
            return false;
        }

        return await disseminationService.Publish(this, siloAddress, ManifestVersion, cancellationToken);
    }

    public long GetVersion(DisseminationKey key) =>
        key.Value is SiloAddress siloAddress
        && clusterManifestProvider.GetManifestReferencesForDissemination().ContainsKey(siloAddress)
            ? ManifestVersion
            : 0;

    public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request)
    {
        if (request.Key.Value is not SiloAddress siloAddress
            || !clusterManifestProvider.GetManifestReferencesForDissemination().TryGetValue(siloAddress, out var manifestHash))
        {
            return DisseminationRepairResult.Unavailable(version: 0);
        }

        if (request.ToVersion is { } targetVersion && targetVersion != ManifestVersion)
        {
            return DisseminationRepairResult.Unavailable(ManifestVersion);
        }

        if (request.FromVersion is { } peerVersion && peerVersion >= ManifestVersion)
        {
            return DisseminationRepairResult.Current(ManifestVersion);
        }

        if (request.MaxItemCount <= 0)
        {
            return DisseminationRepairResult.InsufficientCapacity(ManifestVersion);
        }

        var value = CreateValue(siloAddress, manifestHash);
        return value.Payload.Length <= request.MaxPayloadBytes
            && value.Payload.Length <= request.MaxBatchBytes
                ? DisseminationRepairResult.Produced(ManifestVersion, [value])
                : DisseminationRepairResult.InsufficientCapacity(ManifestVersion);
    }

    public ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        if (value.Key.Value is not SiloAddress siloAddress
            || value.FromVersion != 0
            || value.ToVersion != ManifestVersion)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var reference = serializer.Deserialize<ClusterManifestReference>(value.Payload);
        if (string.IsNullOrEmpty(reference.ManifestHash.Value))
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var result = clusterManifestProvider.ApplyDisseminatedManifestReference(siloAddress, reference.ManifestHash);
        if (result is DisseminationApplyResult.Applied or DisseminationApplyResult.Duplicate)
        {
            CacheValue(siloAddress, reference.ManifestHash, value.Payload);
        }

        return ValueTask.FromResult(result);
    }

    private DisseminationValue CreateValue(SiloAddress siloAddress, ManifestHash manifestHash)
    {
        lock (_cacheLock)
        {
            if (!_cachedValues.TryGetValue(siloAddress, out var cached) || cached.Hash != manifestHash)
            {
                cached = (manifestHash, serializer.SerializeToArray(new ClusterManifestReference(manifestHash)));
                _cachedValues[siloAddress] = cached;
            }

            return new DisseminationValue(siloAddress, fromVersion: 0, ManifestVersion, cached.Payload);
        }
    }

    private void CacheValue(SiloAddress siloAddress, ManifestHash manifestHash, ReadOnlyMemory<byte> payload)
    {
        lock (_cacheLock)
        {
            _cachedValues[siloAddress] = (manifestHash, payload);
        }
    }

    private void PruneCache(IEnumerable<SiloAddress> currentSilos)
    {
        var current = currentSilos.ToHashSet();
        lock (_cacheLock)
        {
            foreach (var siloAddress in _cachedValues.Keys.Where(key => !current.Contains(key)).ToArray())
            {
                _cachedValues.Remove(siloAddress);
            }
        }
    }
}

// Manifest bodies are immutable and addressed by their canonical hash, so every distinct body is one version-1 value.
internal sealed class GrainManifestDisseminationNamespace(
    IClusterManifestDisseminationParticipant clusterManifestProvider,
    IOptions<SiloMessagingOptions> options,
    Serializer serializer) : IDisseminationNamespace
{
    private const long ManifestVersion = 1;
    private readonly object _cacheLock = new();
    private readonly Dictionary<ManifestHash, ReadOnlyMemory<byte>> _cachedValues = [];

    public DisseminationNamespace Name => DisseminationNamespaceNames.GrainManifest;

    public DisseminationNamespaceOptions Options => options.Value.ClusterManifestDissemination;

    public IEnumerable<DigestEntry> Digests
    {
        get
        {
            var contents = clusterManifestProvider.GetManifestContentsForDissemination();
            PruneCache(contents.Keys);
            foreach (var manifestHash in contents.Keys)
            {
                yield return new DigestEntry(manifestHash.Value, ManifestVersion);
            }
        }
    }

    public async ValueTask<bool> PublishAsync(
        IDisseminationService disseminationService,
        ManifestHash manifestHash,
        CancellationToken cancellationToken)
    {
        if (!clusterManifestProvider.GetManifestContentsForDissemination().ContainsKey(manifestHash))
        {
            return false;
        }

        return await disseminationService.Publish(this, manifestHash.Value, ManifestVersion, cancellationToken);
    }

    public long GetVersion(DisseminationKey key) =>
        key.Value is string hash
        && clusterManifestProvider.GetManifestContentsForDissemination().ContainsKey(new ManifestHash(hash))
            ? ManifestVersion
            : 0;

    public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request)
    {
        if (request.Key.Value is not string hashValue)
        {
            return DisseminationRepairResult.Unavailable(version: 0);
        }

        var manifestHash = new ManifestHash(hashValue);
        if (!clusterManifestProvider.GetManifestContentsForDissemination().TryGetValue(manifestHash, out var manifest))
        {
            return DisseminationRepairResult.Unavailable(version: 0);
        }

        if (request.ToVersion is { } targetVersion && targetVersion != ManifestVersion)
        {
            return DisseminationRepairResult.Unavailable(ManifestVersion);
        }

        if (request.FromVersion is { } peerVersion && peerVersion >= ManifestVersion)
        {
            return DisseminationRepairResult.Current(ManifestVersion);
        }

        if (request.MaxItemCount <= 0)
        {
            return DisseminationRepairResult.InsufficientCapacity(ManifestVersion);
        }

        var value = CreateValue(manifestHash, manifest);
        return value.Payload.Length <= request.MaxPayloadBytes
            && value.Payload.Length <= request.MaxBatchBytes
                ? DisseminationRepairResult.Produced(ManifestVersion, [value])
                : DisseminationRepairResult.InsufficientCapacity(ManifestVersion);
    }

    public ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        if (value.Key.Value is not string hashValue
            || value.FromVersion != 0
            || value.ToVersion != ManifestVersion)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var content = serializer.Deserialize<ClusterManifestContent>(value.Payload);
        var manifestHash = new ManifestHash(hashValue);
        if (content.ManifestHash != manifestHash
            || ManifestHashCalculator.ComputeHash(content.Manifest) != manifestHash)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var result = clusterManifestProvider.ApplyDisseminatedManifestContent(manifestHash, content.Manifest);
        if (result is DisseminationApplyResult.Applied or DisseminationApplyResult.Duplicate)
        {
            CacheValue(manifestHash, value.Payload);
        }

        return ValueTask.FromResult(result);
    }

    private DisseminationValue CreateValue(ManifestHash manifestHash, GrainManifest manifest)
    {
        lock (_cacheLock)
        {
            if (!_cachedValues.TryGetValue(manifestHash, out var payload))
            {
                payload = serializer.SerializeToArray(new ClusterManifestContent(manifestHash, manifest));
                _cachedValues[manifestHash] = payload;
            }

            return new DisseminationValue(manifestHash.Value, fromVersion: 0, ManifestVersion, payload);
        }
    }

    private void CacheValue(ManifestHash manifestHash, ReadOnlyMemory<byte> payload)
    {
        lock (_cacheLock)
        {
            _cachedValues[manifestHash] = payload;
        }
    }

    private void PruneCache(IEnumerable<ManifestHash> currentHashes)
    {
        var current = currentHashes.ToHashSet();
        lock (_cacheLock)
        {
            foreach (var manifestHash in _cachedValues.Keys.Where(key => !current.Contains(key)).ToArray())
            {
                _cachedValues.Remove(manifestHash);
            }
        }
    }
}

[GenerateSerializer, Immutable]
internal sealed record ClusterManifestReference([property: Id(0)] ManifestHash ManifestHash);

[GenerateSerializer, Immutable]
internal sealed record ClusterManifestContent(
    [property: Id(0)] ManifestHash ManifestHash,
    [property: Id(1)] GrainManifest Manifest);
