using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Indicates that a replica transport's byte budget for <see cref="Node"/> stayed short until the call's deadline, so
/// the call never left its caller and had no effect (ledger-replicas.md §2.2, §4). Repeating the call may succeed
/// (§6.4): the writer repeats a write which timed out in the queue, and a recovery waits it out.
/// </summary>
public sealed class LedgerReplicaQueueTimeoutException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="LedgerReplicaQueueTimeoutException"/> class.</summary>
    public LedgerReplicaQueueTimeoutException()
        : base("A replica call waited for its node's byte budget until its deadline, and never left its caller.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LedgerReplicaQueueTimeoutException"/> class.</summary>
    public LedgerReplicaQueueTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LedgerReplicaQueueTimeoutException"/> class.</summary>
    public LedgerReplicaQueueTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerReplicaQueueTimeoutException"/> class for a call to
    /// <paramref name="node"/> which waited <paramref name="deadline"/>.
    /// </summary>
    public LedgerReplicaQueueTimeoutException(NodeId node, TimeSpan deadline)
        : base(string.Create(CultureInfo.InvariantCulture, $"A replica call to node {node} waited {deadline} for the node's byte budget, and never left its caller."))
    {
        Node = node;
    }

    /// <summary>Gets the node whose budget stayed short, when known.</summary>
    public NodeId? Node { get; }
}
