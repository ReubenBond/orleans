namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// The answer to a retirement submitted to the node registry committer (orleans.md §5.3), decided from a node registry
/// value read after the retirement was submitted, or written by a write which landed (OR-I19).
/// </summary>
internal readonly union NodeRetirementOutcome(NodeRetirementOutcome.Retired, NodeRetirementOutcome.NotRegistered) : IEquatable<NodeRetirementOutcome>
{
    /// <inheritdoc/>
    public bool Equals(NodeRetirementOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is NodeRetirementOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <summary>
    /// The registry holds the node's entry retired at <paramref name="Revision"/>, the revision of the write which retired
    /// it; a retired entry stays retired (OR-I2).
    /// </summary>
    /// <param name="Revision">The revision of the write which retired the node.</param>
    public sealed record Retired(ulong Revision);

    /// <summary>
    /// The registry value read holds no entry of the node, decided against that value; no write carried the retirement.
    /// </summary>
    public sealed record NotRegistered
    {
        private NotRegistered()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static NotRegistered Instance { get; } = new();
    }
}
