using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>Orders nodes by the bytes of their identities, the placement policy's tie break (orleans.md §5.6).</summary>
internal sealed class NodeOrder : IComparer<NodeId>
{
    public static NodeOrder Instance { get; } = new();

    public int Compare(NodeId x, NodeId y)
    {
        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];
        _ = x.Value.TryWriteBytes(left, bigEndian: true, out _);
        _ = y.Value.TryWriteBytes(right, bigEndian: true, out _);
        return left.SequenceCompareTo(right);
    }
}
