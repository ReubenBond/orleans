using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A cooperative <see cref="PrepareReleaseRequest"/> the cluster controller decided to send to an outgoing primary
/// before sending its planned replacement transition (orleans.md §5.6, §5.7, OR-I24).
/// </summary>
/// <param name="node">The outgoing primary node that serves the open ledger.</param>
/// <param name="request">The prepare-release request naming the ledger, range, and deadline.</param>
/// <param name="purpose">The planned replacement purpose that triggered the release.</param>
/// <remarks>Its equality is reference equality: the controller matches a reply to the send it answers.</remarks>
internal sealed class SentPrepareRelease(NodeId node, PrepareReleaseRequest request, TransitionPurpose purpose)
{
    /// <summary>Gets the outgoing primary node that serves the open ledger.</summary>
    public NodeId Node { get; } = node;

    /// <summary>Gets the prepare-release request naming the ledger, range, and deadline.</summary>
    public PrepareReleaseRequest Request { get; } = request;

    /// <summary>Gets the planned replacement purpose that triggered the release.</summary>
    public TransitionPurpose Purpose { get; } = purpose;

    /// <summary>
    /// Gets whether this prepare-release counts against <c>MaxConcurrentTransitions</c>: every purpose except a
    /// takeover, WAL-release roll, repair, and departure.
    /// </summary>
    public bool IsBalancing => Purpose is not (TransitionPurpose.Takeover or TransitionPurpose.WalReleaseRoll or TransitionPurpose.Repair or TransitionPurpose.Departure);

    /// <inheritdoc/>
    public override string ToString() => $"PrepareRelease({Purpose}) {Request.Ledger} on {Node}";
}
