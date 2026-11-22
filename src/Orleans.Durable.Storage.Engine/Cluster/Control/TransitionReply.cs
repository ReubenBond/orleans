using Orleans.Durable.Storage.Engine.Ledgers.Control;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A node's answer to a transition the controller sent it (orleans.md §7.2): the executor's verdict, with the deciding
/// value's epoch for <c>Applied</c> and <c>Superseded</c>, or <see cref="Declined"/> when the node is retired or not the
/// transition's executor.
/// </summary>
internal readonly union TransitionReply(
    LedgerMapCommitOutcome.Applied,
    LedgerMapCommitOutcome.Superseded,
    LedgerMapCommitOutcome.Refused,
    LedgerMapCommitOutcome.Rejected,
    TransitionReply.Declined) : IEquatable<TransitionReply>
{
    /// <inheritdoc/>
    public bool Equals(TransitionReply other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TransitionReply other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>Returns the reply which relays the executor's <paramref name="outcome"/>.</summary>
    public static TransitionReply Of(LedgerMapCommitOutcome outcome) => outcome switch
    {
        LedgerMapCommitOutcome.Applied applied => applied,
        LedgerMapCommitOutcome.Superseded superseded => superseded,
        LedgerMapCommitOutcome.Refused refused => refused,
        LedgerMapCommitOutcome.Rejected rejected => rejected,
        null => throw new ArgumentException("An outcome holds a case.", nameof(outcome)),
    };

    /// <summary>The node executed nothing: it is retired in its registry view (OR-I3), or another node executes the transition.</summary>
    public sealed record Declined
    {
        private Declined()
        {
        }

        /// <summary>Gets the one declined reply.</summary>
        public static Declined Instance { get; } = new();
    }
}
