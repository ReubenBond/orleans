using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage;

/// <summary>
/// A replica call to <see cref="Node"/> ended with no answer from its store, so its outcome is unknown: it may still take
/// effect, and repeating it may succeed (orleans.md §5.12, OR-I16, ledger-replicas.md §6.4). The inner exception, when
/// there is one, is the failure the caller met: Orleans', the callee's, or the placement's refusal. A <c>Ledger</c> counts
/// it, as every replica failure, as an absent vote.
/// </summary>
internal sealed class LedgerReplicaUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="LedgerReplicaUnavailableException"/> class.</summary>
    public LedgerReplicaUnavailableException()
        : base("A replica call ended with no answer from its node's store.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LedgerReplicaUnavailableException"/> class.</summary>
    public LedgerReplicaUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LedgerReplicaUnavailableException"/> class.</summary>
    public LedgerReplicaUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerReplicaUnavailableException"/> class for a call to
    /// <paramref name="node"/> which ended as <paramref name="reason"/> describes.
    /// </summary>
    internal LedgerReplicaUnavailableException(NodeId node, string reason, Exception? innerException = null)
        : base(string.Create(CultureInfo.InvariantCulture, $"A replica call to node {node} ended with no answer from its store: {reason}"), innerException) => Node = node;

    /// <summary>Gets the node the call named, when known.</summary>
    public NodeId? Node { get; }
}
