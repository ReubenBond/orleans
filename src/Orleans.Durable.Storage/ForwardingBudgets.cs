using System.Collections.Concurrent;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Threading;

namespace Orleans.Durable.Storage;

/// <summary>
/// The silo's forwarding lanes (orleans.md §5.5, §5.12, OR-I17): for each destination node, two FIFO queues of their own,
/// apart from the replica calls' lanes and from each other, so a read's backpressure never delays a write. Forwarded writes
/// wait until the bytes outstanding to that node fit <c>MaxForwardCallBytes</c>; first read pages, whose requests carry no
/// payload and whose replies are at most one page, wait until fewer than <c>MaxForwardReadCalls</c> are outstanding to it.
/// Each fails unsent with <see cref="JournalForwardQueueTimeoutException"/> only at its deadline. A pinned snapshot's later
/// pages take no lane: the reading silo asks for one at a time, so they hold at most one page per snapshot, which its pin
/// reserved. A grant bounds the calls being awaited, not the bytes in flight, as the replica lanes' do; the lanes are kept
/// per node for the silo's life.
/// </summary>
internal sealed class ForwardingBudgets(DurableStorageOptions options, TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<NodeId, CallBudget> _writes = new();
    private readonly ConcurrentDictionary<NodeId, CallBudget> _reads = new();

    /// <summary>Gets the lane of forwarded writes to <paramref name="node"/>, a budget of bytes.</summary>
    public CallBudget Writes(NodeId node) => _writes.GetOrAdd(node, static (node, state) => new CallBudget(
        state.options.MaxForwardCallBytes,
        int.MaxValue,
        state.timeProvider,
        waited => new JournalForwardQueueTimeoutException(node, waited)), (options, timeProvider));

    /// <summary>Gets the lane of first read pages to <paramref name="node"/>, a budget of calls alone.</summary>
    public CallBudget Reads(NodeId node) => _reads.GetOrAdd(node, static (node, state) => CallBudget.ForCalls(
        state.options.MaxForwardReadCalls,
        state.timeProvider,
        waited => new JournalForwardQueueTimeoutException(node, waited)), (options, timeProvider));
}

/// <summary>
/// A forwarded call's deadline passed while it waited in its lane to its destination node, before it was sent
/// (orleans.md §5.5): it had no effect, and the router answers <c>ForwardQueueTimeout</c>, which it resends.
/// </summary>
internal sealed class JournalForwardQueueTimeoutException : TimeoutException
{
    /// <summary>Initializes a new instance of the <see cref="JournalForwardQueueTimeoutException"/> class.</summary>
    public JournalForwardQueueTimeoutException(NodeId node, TimeSpan waited)
        : base(string.Create(CultureInfo.InvariantCulture, $"A forwarded call to node {node} waited {waited} in its lane, past its deadline, and was never sent."))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JournalForwardQueueTimeoutException"/> class.</summary>
    public JournalForwardQueueTimeoutException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JournalForwardQueueTimeoutException"/> class.</summary>
    public JournalForwardQueueTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JournalForwardQueueTimeoutException"/> class.</summary>
    public JournalForwardQueueTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
