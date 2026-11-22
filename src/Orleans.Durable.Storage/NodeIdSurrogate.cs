using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage;

/// <summary>
/// A node identity as a grain message carries it: the node's UUID (identity.md §2), so Ledgers names no Orleans type
/// (orleans.md §7.2).
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct NodeIdSurrogate
{
    /// <summary>Gets or initializes the node's UUID.</summary>
    [Id(0)]
    public Guid Value { get; init; }
}

/// <summary>Converts a node identity to its surrogate and back; the default identity crosses as the empty UUID.</summary>
[RegisterConverter]
internal sealed class NodeIdConverter : IConverter<NodeId, NodeIdSurrogate>
{
    /// <inheritdoc/>
    public NodeId ConvertFromSurrogate(in NodeIdSurrogate surrogate) => surrogate.Value == Guid.Empty ? default : new NodeId(surrogate.Value);

    /// <inheritdoc/>
    public NodeIdSurrogate ConvertToSurrogate(in NodeId value) => new() { Value = value == default ? Guid.Empty : value.Value };
}
