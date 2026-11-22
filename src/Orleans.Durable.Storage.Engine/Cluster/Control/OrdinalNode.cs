using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A node a StatefulSet ordinal holds, with its standing: what a scale-down and a deleted volume's retirement answer for
/// each node whose gate must open before the ordinal's volume is released (orleans.md §5.3).
/// </summary>
/// <param name="Node">The node, which its caller follows up by identity.</param>
/// <param name="Ordinal">Its ordinal.</param>
/// <param name="Standing">Its standing: active, its retirement pending or, on a deleted volume, refused while its silo
/// lives; or retired, its gate not yet open.</param>
internal sealed record OrdinalNode(NodeId Node, int Ordinal, NodeStanding Standing);
