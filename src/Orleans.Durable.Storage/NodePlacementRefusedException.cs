using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Runtime;

namespace Orleans.Durable.Storage;

/// <summary>
/// The placement of a node's grain was refused: the caller's registry names no silo for the node, or membership does not
/// show the named silo active (orleans.md §5.12). No activation exists, so the call had no effect.
/// </summary>
internal sealed class NodePlacementRefusedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="NodePlacementRefusedException"/> class.</summary>
    public NodePlacementRefusedException()
        : base("The node has no registered active silo.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NodePlacementRefusedException"/> class.</summary>
    public NodePlacementRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NodePlacementRefusedException"/> class.</summary>
    public NodePlacementRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NodePlacementRefusedException"/> class for a node.</summary>
    /// <param name="node">The node whose grain was not placed.</param>
    /// <param name="registered">The silo the registry names for the node, or <see langword="null"/> when it names none.</param>
    internal NodePlacementRefusedException(NodeId node, SiloAddress? registered)
        : base(registered is null
            ? $"Node {node} has no registered silo."
            : $"Node {node}'s registered silo {registered} is not active in membership.") => Node = node;

    /// <summary>
    /// Gets the node whose grain was not placed, on the silo which refused it; a caller beyond that silo finds the node in
    /// the message.
    /// </summary>
    public NodeId? Node { get; }
}
