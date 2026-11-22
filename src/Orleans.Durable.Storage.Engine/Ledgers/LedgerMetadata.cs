using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// One ledger the map lists (ledgers.md §4): its ID, key range, location, window, primary claim, and status.
/// </summary>
/// <remarks>
/// The location, <c>K</c>, and on nodes the replicas, are fixed for the ledger's life. Equality compares the locations,
/// whose replicas compare element by element, in order.
/// </remarks>
public readonly record struct LedgerMetadata
{
    /// <summary>Initializes a new instance of the <see cref="LedgerMetadata"/> struct.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/>, <paramref name="location"/>, <paramref name="primary"/>, or <paramref name="status"/> is the
    /// default.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="k"/> is below 1.</exception>
    public LedgerMetadata(LedgerId id, KeyRange range, LedgerLocation location, int k, PrimaryClaim primary, LedgerStatus status)
    {
        if (id == default)
        {
            throw new ArgumentException("A listed ledger has an ID.", nameof(id));
        }

        if (location.Value is null)
        {
            throw new ArgumentException("A listed ledger has a location.", nameof(location));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        if (primary == default)
        {
            throw new ArgumentException("A listed ledger names its primary claim.", nameof(primary));
        }

        if (status.Value is null)
        {
            throw new ArgumentException("A listed ledger has a status.", nameof(status));
        }

        Id = id;
        Range = range;
        Location = location;
        K = k;
        Primary = primary;
        Status = status;
    }

    /// <summary>Initializes a new instance of the <see cref="LedgerMetadata"/> struct for a ledger on <paramref name="replicas"/>.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/>, <paramref name="primary"/>, or <paramref name="status"/> is the default, or
    /// <paramref name="replicas"/> is not an odd number, at most <see cref="LedgerLocation.MaxReplicas"/>, of distinct node
    /// identities.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="k"/> is below 1.</exception>
    public LedgerMetadata(LedgerId id, KeyRange range, ImmutableArray<NodeId> replicas, int k, PrimaryClaim primary, LedgerStatus status)
        : this(id, range, new LedgerLocation.Nodes(replicas), k, primary, status)
    {
    }

    /// <summary>Gets the ledger's ID.</summary>
    public LedgerId Id { get; }

    /// <summary>Gets the keys the ledger holds.</summary>
    public KeyRange Range { get; }

    /// <summary>Gets where the ledger's storage is: its replicas' nodes, or the object store.</summary>
    public LedgerLocation Location { get; }

    /// <summary>
    /// Gets the nodes which hold the replicas of a ledger on nodes, in the map's order.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The ledger is in the object store, which has no replicas: a caller which may meet one decides on
    /// <see cref="Location"/> first (ledgers.md §4).
    /// </exception>
    public ImmutableArray<NodeId> Replicas => Location switch
    {
        LedgerLocation.Nodes nodes => nodes.Replicas,
        LedgerLocation.ObjectStore => throw new InvalidOperationException(
            string.Create(CultureInfo.InvariantCulture, $"Ledger {Id} is in the object store, which has no replicas (ledgers.md §4).")),
        null => throw new UnreachableException("conventions.md §15: a listed ledger's location holds a case."),
    };

    /// <summary>Gets the ledger's window: how many slots its sequencer may have in flight.</summary>
    public int K { get; }

    /// <summary>Gets the ledger's primary claim.</summary>
    public PrimaryClaim Primary { get; }

    /// <summary>Gets whether the ledger is open, or closed at its recorded tail.</summary>
    public LedgerStatus Status { get; }

    /// <summary>Gets a value indicating whether the ledger is open.</summary>
    public bool IsOpen => Status.Value is LedgerStatus.Open;

    /// <summary>Returns whether <paramref name="node"/> holds a replica of the ledger: false for every node when the ledger is in the object store.</summary>
    public bool IsOnNode(NodeId node) => Location.IsOnNode(node);

    /// <inheritdoc/>
    public bool Equals(LedgerMetadata other) =>
        Id == other.Id && Range == other.Range && Location.Equals(other.Location) && K == other.K && Primary == other.Primary
        && Status.Equals(other.Status);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Id, Range, Location, K, Primary, Status);

    /// <summary>Returns the ledger closed at <paramref name="t"/>.</summary>
    internal LedgerMetadata Close(Slot t) => new(Id, Range, Location, K, Primary, new LedgerStatus.Closed(t));

    /// <summary>Compares two replica lists element by element, in order.</summary>
    internal static bool SameReplicas(ImmutableArray<NodeId> left, ImmutableArray<NodeId> right) =>
        left.IsDefault || right.IsDefault ? left.IsDefault == right.IsDefault : left.AsSpan().SequenceEqual(right.AsSpan());
}
