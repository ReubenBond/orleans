using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

internal interface ISiloMetadataDisseminationParticipant
{
    ImmutableDictionary<SiloAddress, SiloMetadata> GetSiloMetadataForDissemination();

    DisseminationApplyResult ApplyDisseminatedSiloMetadata(SiloAddress siloAddress, SiloMetadata metadata);
}

// Metadata is immutable for a silo generation, so every row is a full version-1 value.
internal sealed class SiloMetadataDisseminationNamespace(
    ISiloMetadataDisseminationParticipant siloMetadataCache,
    IOptions<SiloMessagingOptions> options,
    Serializer serializer) : IDisseminationNamespace
{
    private const long MetadataVersion = 1;
    private readonly object _cacheLock = new();
    private readonly Dictionary<SiloAddress, (SiloMetadata Metadata, ReadOnlyMemory<byte> Payload)> _cachedValues = [];

    public DisseminationNamespace Name => DisseminationNamespaceNames.SiloMetadata;

    public DisseminationNamespaceOptions Options => options.Value.SiloMetadataDissemination;

    public IEnumerable<DigestEntry> Digests
    {
        get
        {
            var metadata = siloMetadataCache.GetSiloMetadataForDissemination();
            PruneCache(metadata.Keys);
            foreach (var siloAddress in metadata.Keys)
            {
                yield return new DigestEntry(siloAddress, MetadataVersion);
            }
        }
    }

    public async ValueTask<bool> PublishAsync(
        IDisseminationService disseminationService,
        SiloAddress siloAddress,
        CancellationToken cancellationToken)
    {
        if (!siloMetadataCache.GetSiloMetadataForDissemination().ContainsKey(siloAddress))
        {
            return false;
        }

        return await disseminationService.Publish(this, siloAddress, MetadataVersion, cancellationToken);
    }

    public long GetVersion(DisseminationKey key) =>
        key.Value is SiloAddress siloAddress
        && siloMetadataCache.GetSiloMetadataForDissemination().ContainsKey(siloAddress)
            ? MetadataVersion
            : 0;

    public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request)
    {
        if (request.Key.Value is not SiloAddress siloAddress
            || !siloMetadataCache.GetSiloMetadataForDissemination().TryGetValue(siloAddress, out var metadata))
        {
            return DisseminationRepairResult.Unavailable(version: 0);
        }

        if (request.ToVersion is { } targetVersion && targetVersion != MetadataVersion)
        {
            return DisseminationRepairResult.Unavailable(MetadataVersion);
        }

        if (request.FromVersion is { } peerVersion && peerVersion >= MetadataVersion)
        {
            return DisseminationRepairResult.Current(MetadataVersion);
        }

        if (request.MaxItemCount <= 0)
        {
            return DisseminationRepairResult.InsufficientCapacity(MetadataVersion);
        }

        var value = CreateValue(siloAddress, metadata);
        return value.Payload.Length <= request.MaxPayloadBytes
            && value.Payload.Length <= request.MaxBatchBytes
                ? DisseminationRepairResult.Produced(MetadataVersion, [value])
                : DisseminationRepairResult.InsufficientCapacity(MetadataVersion);
    }

    public ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        if (value.Key.Value is not SiloAddress siloAddress
            || value.FromVersion != 0
            || value.ToVersion != MetadataVersion)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var metadata = serializer.Deserialize<SiloMetadata>(value.Payload);
        var result = siloMetadataCache.ApplyDisseminatedSiloMetadata(siloAddress, metadata);
        if (result is DisseminationApplyResult.Applied or DisseminationApplyResult.Duplicate)
        {
            CacheValue(siloAddress, metadata, value.Payload);
        }

        return ValueTask.FromResult(result);
    }

    private DisseminationValue CreateValue(SiloAddress siloAddress, SiloMetadata metadata)
    {
        lock (_cacheLock)
        {
            if (!_cachedValues.TryGetValue(siloAddress, out var cached)
                || !SiloMetadataEquals(cached.Metadata, metadata))
            {
                cached = (metadata, serializer.SerializeToArray(metadata));
                _cachedValues[siloAddress] = cached;
            }

            return new DisseminationValue(siloAddress, fromVersion: 0, MetadataVersion, cached.Payload);
        }
    }

    private void CacheValue(SiloAddress siloAddress, SiloMetadata metadata, ReadOnlyMemory<byte> payload)
    {
        lock (_cacheLock)
        {
            _cachedValues[siloAddress] = (metadata, payload);
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

    internal static bool SiloMetadataEquals(SiloMetadata left, SiloMetadata right) =>
        left.Metadata.Count == right.Metadata.Count
        && left.Metadata.All(entry =>
            right.Metadata.TryGetValue(entry.Key, out var value)
            && string.Equals(entry.Value, value, StringComparison.Ordinal));
}
