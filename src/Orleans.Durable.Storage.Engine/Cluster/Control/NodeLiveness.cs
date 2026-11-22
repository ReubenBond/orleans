namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// What the controller's membership view says of a node's silo (orleans.md §5.7): <see cref="Live"/>;
/// <see cref="Departing"/>, shutting down; or <see cref="Dead"/> since a time, which also stands for a silo membership no
/// longer lists whose generation is older than the observer's own. Liveness only: the seal and the conditional map write
/// decide every outcome (R2).
/// </summary>
/// <remarks>
/// A departing node's claims have not ended, so its ledgers are handed off rather than taken over, and its hand-off
/// request stays pending; it is given no work: it executes no transition, receives no ledger, and is chosen as no new
/// replica.
/// </remarks>
internal readonly union NodeLiveness(NodeLiveness.Live, NodeLiveness.Departing, NodeLiveness.Dead) : IEquatable<NodeLiveness>
{
    /// <inheritdoc/>
    public bool Equals(NodeLiveness other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is NodeLiveness other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>Membership shows the node's registered silo active.</summary>
    public sealed record Live
    {
        private Live()
        {
        }

        /// <summary>Gets the one live liveness.</summary>
        public static Live Instance { get; } = new();
    }

    /// <summary>Membership shows the node's registered silo shutting down or stopping.</summary>
    public sealed record Departing
    {
        private Departing()
        {
        }

        /// <summary>Gets the one departing liveness.</summary>
        public static Departing Instance { get; } = new();
    }

    /// <summary>Membership declared the node's registered silo dead at <paramref name="Since"/>, or no longer lists it.</summary>
    /// <param name="Since">When the controller first saw the node dead.</param>
    public sealed record Dead(DateTimeOffset Since);
}
