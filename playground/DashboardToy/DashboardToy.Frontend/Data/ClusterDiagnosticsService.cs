using System.Runtime.InteropServices;
using Orleans.Runtime;

namespace DashboardToy.Frontend.Data;

public class ClusterDiagnosticsService(IGrainFactory grainFactory)
{
    private readonly Dictionary<GrainId, int> _grainKeys= new();
    private readonly Dictionary<SiloAddress, int> _hostKeys= new();
    private readonly Dictionary<Key, ulong> _edges = new();
    public readonly IManagementGrain _managementGrain = grainFactory.GetGrain<IManagementGrain>(0);

    public async ValueTask<CallGraph> GetGrainCallFrequencies()
    {
        _edges.Clear();
        var maxCount = 0UL;
        await foreach (var edge in _managementGrain.GetGrainCallFrequencies())
        {
            var sourceId = GetGrainKey(edge.SourceGrain);
            var targetId = GetGrainKey(edge.TargetGrain);
            var sourceHostId = GetHostKey(edge.SourceHost);
            var targetHostId = GetHostKey(edge.TargetHost);
            maxCount = Math.Max(maxCount, edge.CallCount);
            UpdateEdge(new(sourceId, targetId, sourceHostId, targetHostId), edge.CallCount);
        }

        var grainIds = new List<GraphNode>(_grainKeys.Count);
        CollectionsMarshal.SetCount(grainIds, _grainKeys.Count);
        foreach ((var grainId, var key) in _grainKeys)
        {
            grainIds[key] = new(grainId.ToString());
        }

        var hostIds = new List<string>(_hostKeys.Count);
        CollectionsMarshal.SetCount(hostIds, _hostKeys.Count);
        foreach ((var hostId, var key) in _hostKeys)
        {
            hostIds[key] = hostId.ToString();
        }

        var edges = new List<GraphEdge>();

        var distanceFactor = maxCount / 1000.0;
        foreach (var edge in _edges)
        {
            edges.Add(new (edge.Key.Source, edge.Key.Target, edge.Key.SourceHost, edge.Key.TargetHost, edge.Value/distanceFactor));
        }

        return new(grainIds, hostIds, edges);
    }

    private int GetGrainKey(GrainId grainId)
    {
        ref var key = ref CollectionsMarshal.GetValueRefOrAddDefault(_grainKeys, grainId, out var exists);
        if (!exists)
        {
            key = _grainKeys.Count - 1;
        }

        return key;
    }   

    private int GetHostKey(SiloAddress silo)
    {
        ref var key = ref CollectionsMarshal.GetValueRefOrAddDefault(_hostKeys, silo, out var exists);
        if (!exists)
        {
            key = _hostKeys.Count - 1;
        }

        return key;
    } 

    private void UpdateEdge(Key key, ulong increment)
    {
        ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(_edges, key, out var exists);
        count += increment;
    } 
}

public record class CallGraph(List<GraphNode> GrainIds, List<string> HostIds, List<GraphEdge> Edges);

public record struct GraphNode(string Name, double R);
public record struct Key(int Source, int Target, int SourceHost, int TargetHost);
public record struct GraphEdge(int Source, int Target, int SourceHost, int TargetHost, double Distance);
