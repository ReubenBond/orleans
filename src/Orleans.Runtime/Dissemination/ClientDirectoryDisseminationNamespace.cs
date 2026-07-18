using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

internal interface IClientDirectoryDisseminationParticipant
{
    ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> GetRoutesForDissemination();

    DisseminationApplyResult ApplyDisseminatedRoute(
        SiloAddress siloAddress,
        long? expectedVersion,
        ClientDirectoryRoute route);
}

internal readonly record struct ClientDirectoryRoute(
    ImmutableHashSet<GrainId> ConnectedClients,
    long Version);

// Client routes are independently versioned by their owning silo. Retained rows permit compact add/remove
// deltas while a full row from version zero remains available to repair an unknown peer.
internal sealed class ClientDirectoryDisseminationNamespace(
    IClientDirectoryDisseminationParticipant clientDirectory,
    IOptions<SiloMessagingOptions> options,
    Serializer serializer) : IDisseminationNamespace
{
    private const int MaxRouteHistory = 32;
    private readonly object _historyLock = new();
    private readonly Dictionary<SiloAddress, RouteHistory> _routeHistories = [];

    public DisseminationNamespace Name => DisseminationNamespaceNames.ClientDirectory;

    public DisseminationNamespaceOptions Options => options.Value.ClientDirectoryDissemination;

    public IEnumerable<DigestEntry> Digests
    {
        get
        {
            var routes = clientDirectory.GetRoutesForDissemination();
            PruneHistory(routes.Keys);
            foreach (var (siloAddress, route) in routes)
            {
                RememberRoute(siloAddress, new(route.ConnectedClients, route.Version));
                yield return new DigestEntry(siloAddress, route.Version);
            }
        }
    }

    public async ValueTask<bool> PublishAsync(
        IDisseminationService disseminationService,
        SiloAddress siloAddress,
        long version,
        CancellationToken cancellationToken)
    {
        var routes = clientDirectory.GetRoutesForDissemination();
        if (!routes.TryGetValue(siloAddress, out var route) || route.Version != version)
        {
            return false;
        }

        RememberRoute(siloAddress, new(route.ConnectedClients, route.Version));
        return await disseminationService.Publish(this, siloAddress, version, cancellationToken);
    }

    public long GetVersion(DisseminationKey key) =>
        key.Value is SiloAddress siloAddress
        && clientDirectory.GetRoutesForDissemination().TryGetValue(siloAddress, out var route)
            ? route.Version
            : 0;

    public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request)
    {
        if (request.Key.Value is not SiloAddress siloAddress)
        {
            return DisseminationRepairResult.Unavailable(version: 0);
        }

        var routes = clientDirectory.GetRoutesForDissemination();
        if (!routes.TryGetValue(siloAddress, out var currentRoute))
        {
            return DisseminationRepairResult.Unavailable(version: 0);
        }

        lock (_historyLock)
        {
            var history = GetOrCreateHistory(siloAddress);
            RememberRouteUnsafe(history, new(currentRoute.ConnectedClients, currentRoute.Version));
            var targetVersion = request.ToVersion ?? currentRoute.Version;
            if (targetVersion > currentRoute.Version
                || !history.Snapshots.TryGetValue(targetVersion, out var targetClients))
            {
                return DisseminationRepairResult.Unavailable(currentRoute.Version);
            }

            if (request.FromVersion is { } peerVersion && peerVersion >= targetVersion)
            {
                return DisseminationRepairResult.Current(targetVersion);
            }

            if (request.MaxItemCount <= 0)
            {
                return DisseminationRepairResult.InsufficientCapacity(targetVersion);
            }

            var snapshotValue = CreateSnapshotValue(siloAddress, targetVersion, targetClients, history);
            var selectedValue = snapshotValue;
            if (request.FromVersion is { } fromVersion
                && fromVersion > 0
                && history.Snapshots.TryGetValue(fromVersion, out var baseClients))
            {
                var deltaValue = CreateDeltaValue(
                    siloAddress,
                    fromVersion,
                    targetVersion,
                    baseClients,
                    targetClients,
                    history);
                if (deltaValue.Payload.Length < snapshotValue.Payload.Length)
                {
                    selectedValue = deltaValue;
                }
            }

            if (selectedValue.Payload.Length > request.MaxPayloadBytes
                || selectedValue.Payload.Length > request.MaxBatchBytes)
            {
                if (selectedValue.FromVersion != 0
                    && snapshotValue.Payload.Length <= request.MaxPayloadBytes
                    && snapshotValue.Payload.Length <= request.MaxBatchBytes)
                {
                    selectedValue = snapshotValue;
                }
                else
                {
                    return DisseminationRepairResult.InsufficientCapacity(targetVersion);
                }
            }

            return DisseminationRepairResult.Produced(targetVersion, [selectedValue]);
        }
    }

    public ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        if (value.Key.Value is not SiloAddress siloAddress)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var update = serializer.Deserialize<ClientDirectoryRouteUpdate>(value.Payload);
        var routes = clientDirectory.GetRoutesForDissemination();
        routes.TryGetValue(siloAddress, out var current);
        if (current.Version > 0)
        {
            RememberRoute(siloAddress, new(current.ConnectedClients, current.Version));
        }

        ClientDirectoryRoute route;
        long? expectedVersion;
        if (update.Snapshot is { } snapshot)
        {
            if (update.Delta is not null || value.FromVersion != 0)
            {
                return ValueTask.FromResult(DisseminationApplyResult.Rejected);
            }

            route = new(snapshot, value.ToVersion);
            expectedVersion = null;
        }
        else if (update.Delta is { } delta)
        {
            if (value.FromVersion <= 0
                || value.FromVersion != delta.BaseVersion
                || value.ToVersion != delta.Version)
            {
                return ValueTask.FromResult(DisseminationApplyResult.Rejected);
            }

            if (current.Version != value.FromVersion)
            {
                return ValueTask.FromResult(DisseminationApplyResult.Rejected);
            }

            route = new(
                current.ConnectedClients.Except(delta.RemovedClients).Union(delta.AddedClients),
                value.ToVersion);
            expectedVersion = value.FromVersion;
        }
        else
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var result = clientDirectory.ApplyDisseminatedRoute(siloAddress, expectedVersion, route);
        if (result is DisseminationApplyResult.Applied or DisseminationApplyResult.Duplicate)
        {
            RememberRoute(siloAddress, route);
        }

        return ValueTask.FromResult(result);
    }

    private DisseminationValue CreateSnapshotValue(
        SiloAddress siloAddress,
        long version,
        ImmutableHashSet<GrainId> clients,
        RouteHistory history)
    {
        if (!history.SnapshotPayloads.TryGetValue(version, out var payload))
        {
            payload = serializer.SerializeToArray(new ClientDirectoryRouteUpdate { Snapshot = clients });
            history.SnapshotPayloads.Add(version, payload);
        }

        return new DisseminationValue(siloAddress, fromVersion: 0, version, payload);
    }

    private DisseminationValue CreateDeltaValue(
        SiloAddress siloAddress,
        long fromVersion,
        long toVersion,
        ImmutableHashSet<GrainId> baseClients,
        ImmutableHashSet<GrainId> targetClients,
        RouteHistory history)
    {
        var key = (fromVersion, toVersion);
        if (!history.DeltaPayloads.TryGetValue(key, out var payload))
        {
            payload = serializer.SerializeToArray(new ClientDirectoryRouteUpdate
            {
                Delta = new ClientDirectoryRouteDelta
                {
                    BaseVersion = fromVersion,
                    Version = toVersion,
                    AddedClients = targetClients.Except(baseClients),
                    RemovedClients = baseClients.Except(targetClients),
                },
            });
            history.DeltaPayloads.Add(key, payload);
        }

        return new DisseminationValue(siloAddress, fromVersion, toVersion, payload);
    }

    private void RememberRoute(SiloAddress siloAddress, ClientDirectoryRoute route)
    {
        lock (_historyLock)
        {
            RememberRouteUnsafe(GetOrCreateHistory(siloAddress), route);
        }
    }

    private static void RememberRouteUnsafe(RouteHistory history, ClientDirectoryRoute route)
    {
        if (history.Snapshots.TryGetValue(route.Version, out var previous)
            && !previous.SetEquals(route.ConnectedClients))
        {
            history.SnapshotPayloads.Remove(route.Version);
            foreach (var key in history.DeltaPayloads.Keys
                .Where(key => key.FromVersion == route.Version || key.ToVersion == route.Version)
                .ToArray())
            {
                history.DeltaPayloads.Remove(key);
            }
        }

        history.Snapshots[route.Version] = route.ConnectedClients;
        while (history.Snapshots.Count > MaxRouteHistory)
        {
            var removedVersion = history.Snapshots.Keys.First();
            history.Snapshots.Remove(removedVersion);
            history.SnapshotPayloads.Remove(removedVersion);
            foreach (var key in history.DeltaPayloads.Keys
                .Where(key => key.FromVersion == removedVersion || key.ToVersion == removedVersion)
                .ToArray())
            {
                history.DeltaPayloads.Remove(key);
            }
        }
    }

    private RouteHistory GetOrCreateHistory(SiloAddress siloAddress)
    {
        if (!_routeHistories.TryGetValue(siloAddress, out var history))
        {
            history = new();
            _routeHistories.Add(siloAddress, history);
        }

        return history;
    }

    private void PruneHistory(IEnumerable<SiloAddress> currentSilos)
    {
        var current = currentSilos.ToHashSet();
        lock (_historyLock)
        {
            foreach (var siloAddress in _routeHistories.Keys.Where(key => !current.Contains(key)).ToArray())
            {
                _routeHistories.Remove(siloAddress);
            }
        }
    }

    private sealed class RouteHistory
    {
        public SortedDictionary<long, ImmutableHashSet<GrainId>> Snapshots { get; } = [];

        public Dictionary<long, ReadOnlyMemory<byte>> SnapshotPayloads { get; } = [];

        public Dictionary<(long FromVersion, long ToVersion), ReadOnlyMemory<byte>> DeltaPayloads { get; } = [];
    }
}

[GenerateSerializer, Immutable]
internal sealed class ClientDirectoryRouteUpdate
{
    [Id(0)]
    public ImmutableHashSet<GrainId>? Snapshot { get; init; }

    [Id(1)]
    public ClientDirectoryRouteDelta? Delta { get; init; }
}

[GenerateSerializer, Immutable]
internal sealed class ClientDirectoryRouteDelta
{
    [Id(0)]
    public long BaseVersion { get; init; }

    [Id(1)]
    public long Version { get; init; }

    [Id(2)]
    public ImmutableHashSet<GrainId> AddedClients { get; init; } = [];

    [Id(3)]
    public ImmutableHashSet<GrainId> RemovedClients { get; init; } = [];
}
