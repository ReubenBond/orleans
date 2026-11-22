using Orleans.Durable.Storage.Engine.Threading;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Creates a node's replica call budget (ledger-replicas.md §2.2): a <see cref="CallBudget"/> of the request payload and
/// reply bytes, and the calls, a transport keeps outstanding to one node, whose timed-out calls fail with
/// <see cref="LedgerReplicaQueueTimeoutException"/>, unsent and without effect. The memory transport and the Orleans
/// composition's transport bound their replica calls with it.
/// </summary>
public static class NodeCallBudget
{
    /// <summary>Creates <paramref name="node"/>'s budget.</summary>
    /// <param name="node">The node whose calls the budget bounds.</param>
    /// <param name="maxBytes">The bytes of request payloads and replies outstanding at once, at least the payload limit.</param>
    /// <param name="maxCalls">The calls outstanding at once.</param>
    /// <param name="timeProvider">Times the deadlines.</param>
    /// <returns>The budget.</returns>
    public static CallBudget Create(NodeId node, long maxBytes, int maxCalls, TimeProvider timeProvider) =>
        new(maxBytes, maxCalls, timeProvider, waited => new LedgerReplicaQueueTimeoutException(node, waited));
}
