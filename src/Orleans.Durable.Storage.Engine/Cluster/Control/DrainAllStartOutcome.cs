namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// The outcome of starting a planned whole-cluster drain off node-backed ledgers into object-store ledgers before an
/// instance-releasing stop (orleans.md §5.14, §8): <see cref="Started"/>, <see cref="ObjectLedgersNotConfigured"/>, or
/// <see cref="ObjectLedgersNotReady"/>.
/// </summary>
internal readonly union DrainAllStartOutcome(
    DrainAllStartOutcome.Started,
    DrainAllStartOutcome.ObjectLedgersNotConfigured,
    DrainAllStartOutcome.ObjectLedgersNotReady) : IEquatable<DrainAllStartOutcome>
{
    /// <summary>
    /// Drain-all is active in this controller activation: new node-backed placement is suppressed, open node-backed ledgers
    /// roll onto the object store, closed node-backed ledgers drain and drop, and once zero node-backed ledgers remain and
    /// no node-backed transition is in flight, active diskful nodes retire and the map's retirement floor rises to cover
    /// them.
    /// </summary>
    public sealed record Started
    {
        private Started()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static Started Instance { get; } = new();
    }

    /// <summary>
    /// Rejected because neither the controller's silo nor the node registry has an object-ledger store configured; draining
    /// off node-backed ledgers requires object-store ledgers (orleans.md §4, §5.14).
    /// </summary>
    public sealed record ObjectLedgersNotConfigured
    {
        private ObjectLedgersNotConfigured()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static ObjectLedgersNotConfigured Instance { get; } = new();
    }

    /// <summary>
    /// Rejected because object-store ledgers are configured, but this controller activation has not yet adopted the
    /// object-ledger store layout (its silo's object-ledger gate is still pending, or this silo lacks the configuration
    /// while the cluster registry names a store). Retry once the gate passes or the activation moves to a ready silo.
    /// </summary>
    public sealed record ObjectLedgersNotReady
    {
        private ObjectLedgersNotReady()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static ObjectLedgersNotReady Instance { get; } = new();
    }

    /// <inheritdoc/>
    public bool Equals(DrainAllStartOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DrainAllStartOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
