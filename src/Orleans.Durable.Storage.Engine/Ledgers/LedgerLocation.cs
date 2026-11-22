using System.Collections.Immutable;
using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Where a ledger's storage is, fixed in its map entry for the ledger's life (ledgers.md §4): <see cref="Nodes"/>, its
/// replicas on <c>N</c> nodes, which the ledger protocol's quorum writes; or <see cref="ObjectStore"/>, one copy in the
/// cluster's object store (object-ledgers.md).
/// </summary>
public readonly union LedgerLocation(
    LedgerLocation.Nodes,
    LedgerLocation.ObjectStore) : IEquatable<LedgerLocation>
{
    /// <summary>The most replicas a ledger has: the map stores <c>N</c> in one byte (ledgers.md §7).</summary>
    public const int MaxReplicas = byte.MaxValue;

    /// <summary>
    /// The ledger's replicas are on <c>N</c> nodes, in the map's order: an odd number, at most <see cref="MaxReplicas"/>,
    /// of distinct node identities, with the quorum <c>Q = (N + 1) / 2</c>. Equality compares the replicas element by
    /// element, in order.
    /// </summary>
    public sealed record Nodes
    {
        /// <summary>Initializes a new instance of the <see cref="Nodes"/> class.</summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="replicas"/> is not an odd number, at most <see cref="MaxReplicas"/>, of distinct node identities.
        /// </exception>
        public Nodes(ImmutableArray<NodeId> replicas)
        {
            CheckReplicas(replicas);
            Replicas = replicas;
        }

        /// <summary>Gets the ledger's <c>N</c> replicas, in the map's order.</summary>
        public ImmutableArray<NodeId> Replicas { get; }

        /// <summary>Gets the quorum, <c>(N + 1) / 2</c>.</summary>
        public int Quorum => (Replicas.Length + 1) / 2;

        /// <inheritdoc/>
        public bool Equals(Nodes? other) => other is not null && Replicas.AsSpan().SequenceEqual(other.Replicas.AsSpan());

        /// <inheritdoc/>
        public override int GetHashCode() => Replicas.Length;

        /// <inheritdoc/>
        public override string ToString() => string.Join(", ", Replicas);
    }

    /// <summary>
    /// The ledger's one copy is in the cluster's object store, written by its primary (object-ledgers.md); no node holds a
    /// replica of it.
    /// </summary>
    public sealed record ObjectStore
    {
        private ObjectStore()
        {
        }

        /// <summary>Gets the one object-store location.</summary>
        public static ObjectStore Instance { get; } = new();
    }

    /// <summary>Returns whether <paramref name="node"/> holds a replica of the ledger.</summary>
    public bool IsOnNode(NodeId node) => Value is Nodes nodes && nodes.Replicas.Contains(node);

    /// <inheritdoc/>
    public bool Equals(LedgerLocation other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LedgerLocation other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>Checks that <paramref name="replicas"/> are an odd number, at most <see cref="MaxReplicas"/>, of distinct node identities.</summary>
    /// <exception cref="ArgumentException">They are not.</exception>
    internal static void CheckReplicas(ImmutableArray<NodeId> replicas)
    {
        if (replicas.IsDefault || replicas.Length % 2 == 0 || replicas.Length > MaxReplicas)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A ledger has an odd number of replicas, at most {MaxReplicas}, not {(replicas.IsDefault ? 0 : replicas.Length)}."),
                nameof(replicas));
        }

        for (var index = 0; index < replicas.Length; index++)
        {
            if (replicas[index] == default)
            {
                throw new ArgumentException("A ledger's replicas are node identities.", nameof(replicas));
            }

            if (replicas.AsSpan()[..index].Contains(replicas[index]))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A ledger's replicas are distinct, but {replicas[index]} repeats."), nameof(replicas));
            }
        }
    }
}
