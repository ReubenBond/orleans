using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

internal interface IClusterManifestDisseminationParticipant
{
    ClusterManifestDisseminationSnapshot GetManifestForDissemination();

    DisseminationApplyResult ApplyDisseminatedManifest(ClusterManifestUpdate update);
}

internal readonly record struct ClusterManifestDisseminationSnapshot(
    ClusterManifest Manifest,
    bool IncludesAllActiveServers);

// Cluster manifests form a single monotonically-versioned stream. Full retained snapshots provide
// universal repair while same-version fingerprints reconcile independently assembled partial views.
internal sealed class ClusterManifestDisseminationNamespace(
    IClusterManifestDisseminationParticipant clusterManifestProvider,
    IOptions<SiloMessagingOptions> options,
    Serializer serializer) : IDisseminationNamespace
{
    private const int MaxSnapshotHistory = 32;
    private readonly object _historyLock = new();
    private readonly SortedDictionary<long, ClusterManifestDisseminationSnapshot> _snapshotHistory = [];
    private readonly Dictionary<long, ReadOnlyMemory<byte>> _snapshotPayloads = [];

    public DisseminationNamespace Name => DisseminationNamespaceNames.ClusterManifest;

    public DisseminationNamespaceOptions Options => options.Value.ClusterManifestDissemination;

    public IEnumerable<DigestEntry> Digests
    {
        get
        {
            var snapshot = clusterManifestProvider.GetManifestForDissemination();
            RememberSnapshot(snapshot);
            var version = GetDisseminationVersion(snapshot.Manifest.Version);
            if (version > 0)
            {
                yield return new DigestEntry(
                    DisseminationKey.Default,
                    version,
                    GetFingerprint(snapshot.Manifest));
            }
        }
    }

    public async ValueTask<bool> PublishAsync(
        IDisseminationService disseminationService,
        MajorMinorVersion version,
        CancellationToken cancellationToken)
    {
        var snapshot = clusterManifestProvider.GetManifestForDissemination();
        if (snapshot.Manifest.Version != version)
        {
            return false;
        }

        RememberSnapshot(snapshot);
        return await disseminationService.Publish(
            this,
            DisseminationKey.Default,
            GetDisseminationVersion(version),
            cancellationToken);
    }

    public long GetVersion(DisseminationKey key) =>
        key == DisseminationKey.Default
            ? GetDisseminationVersion(clusterManifestProvider.GetManifestForDissemination().Manifest.Version)
            : 0;

    public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request)
    {
        if (request.Key != DisseminationKey.Default)
        {
            return DisseminationRepairResult.Unavailable(version: 0);
        }

        lock (_historyLock)
        {
            var current = clusterManifestProvider.GetManifestForDissemination();
            RememberSnapshotUnsafe(current);
            var currentVersion = GetDisseminationVersion(current.Manifest.Version);
            var targetVersion = request.ToVersion ?? currentVersion;
            if (targetVersion <= 0
                || targetVersion > currentVersion
                || !_snapshotHistory.TryGetValue(targetVersion, out var target))
            {
                return DisseminationRepairResult.Unavailable(currentVersion);
            }

            if (request.FromVersion is { } peerVersion && peerVersion > targetVersion)
            {
                return DisseminationRepairResult.Current(targetVersion);
            }

            if (request.MaxItemCount <= 0)
            {
                return DisseminationRepairResult.InsufficientCapacity(targetVersion);
            }

            var value = CreateSnapshotValue(targetVersion, target);
            return value.Payload.Length <= request.MaxPayloadBytes
                && value.Payload.Length <= request.MaxBatchBytes
                    ? DisseminationRepairResult.Produced(targetVersion, [value])
                    : DisseminationRepairResult.InsufficientCapacity(targetVersion);
        }
    }

    public ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        if (value.Key != DisseminationKey.Default || value.FromVersion != 0)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var update = serializer.Deserialize<ClusterManifestUpdate>(value.Payload);
        if (value.ToVersion != GetDisseminationVersion(update.Version))
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var result = clusterManifestProvider.ApplyDisseminatedManifest(update);
        if (result is DisseminationApplyResult.Applied or DisseminationApplyResult.Duplicate)
        {
            RememberSnapshot(clusterManifestProvider.GetManifestForDissemination());
        }

        return ValueTask.FromResult(result);
    }

    private DisseminationValue CreateSnapshotValue(
        long version,
        ClusterManifestDisseminationSnapshot snapshot)
    {
        if (!_snapshotPayloads.TryGetValue(version, out var payload))
        {
            payload = serializer.SerializeToArray(new ClusterManifestUpdate(
                snapshot.Manifest.Version,
                snapshot.Manifest.Silos,
                snapshot.IncludesAllActiveServers));
            _snapshotPayloads.Add(version, payload);
        }

        return new DisseminationValue(DisseminationKey.Default, fromVersion: 0, version, payload);
    }

    private void RememberSnapshot(ClusterManifestDisseminationSnapshot snapshot)
    {
        lock (_historyLock)
        {
            RememberSnapshotUnsafe(snapshot);
        }
    }

    private void RememberSnapshotUnsafe(ClusterManifestDisseminationSnapshot snapshot)
    {
        var version = GetDisseminationVersion(snapshot.Manifest.Version);
        if (version <= 0)
        {
            return;
        }

        if (_snapshotHistory.TryGetValue(version, out var previous)
            && GetFingerprint(previous.Manifest) != GetFingerprint(snapshot.Manifest))
        {
            _snapshotPayloads.Remove(version);
        }

        _snapshotHistory[version] = snapshot;
        while (_snapshotHistory.Count > MaxSnapshotHistory)
        {
            var removedVersion = _snapshotHistory.Keys.First();
            _snapshotHistory.Remove(removedVersion);
            _snapshotPayloads.Remove(removedVersion);
        }
    }

    private static long GetDisseminationVersion(MajorMinorVersion version)
    {
        if (version.Major < 0)
        {
            return 0;
        }

        if (version.Major == long.MaxValue)
        {
            throw new InvalidOperationException($"Cluster manifest version {version} exceeds the dissemination version range.");
        }

        // Minor versions are local progress counters and cannot be globally ordered across silos.
        return version.Major + 1;
    }

    private static long GetFingerprint(ClusterManifest manifest)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var entry in manifest.Silos.OrderBy(static entry => entry.Key))
        {
            hash = unchecked((hash ^ (uint)entry.Key.GetConsistentHashCode()) * prime);
            foreach (var character in ManifestHashCalculator.ComputeHash(entry.Value).Value)
            {
                hash = unchecked((hash ^ character) * prime);
            }
        }

        return unchecked((long)hash);
    }
}
