namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Indicates that a storage root is claimed by another cluster.
/// </summary>
/// <remarks>
/// LS-I12: "The claim is created before the root holds any replica and verified before any replica in it is served; a
/// root claimed by another cluster fails with <c>LedgerStoreClusterMismatchException</c>. A claim is never overwritten".
/// </remarks>
public sealed class LedgerStoreClusterMismatchException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerStoreClusterMismatchException"/> class.
    /// </summary>
    public LedgerStoreClusterMismatchException()
        : base("A LedgerStore node root is claimed by another cluster.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerStoreClusterMismatchException"/> class.
    /// </summary>
    public LedgerStoreClusterMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerStoreClusterMismatchException"/> class.
    /// </summary>
    public LedgerStoreClusterMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerStoreClusterMismatchException"/> class for a claim.
    /// </summary>
    /// <param name="claim">The claim's location.</param>
    /// <param name="cluster">The cluster which required the root.</param>
    /// <param name="claimedCluster">The claim's stored content.</param>
    public LedgerStoreClusterMismatchException(string claim, Guid cluster, string claimedCluster)
        : base(
            $"The node root's cluster claim '{claim}' holds '{claimedCluster}', not cluster '{cluster:N}'. "
            + "Each node root belongs to one cluster; configure a different root for this cluster.")
    {
        Claim = claim;
        Cluster = cluster;
        ClaimedCluster = claimedCluster;
    }

    /// <summary>
    /// Gets the claim's location, when known.
    /// </summary>
    public string? Claim { get; }

    /// <summary>
    /// Gets the cluster which required the root, when known.
    /// </summary>
    public Guid? Cluster { get; }

    /// <summary>
    /// Gets the claim's stored content, when known.
    /// </summary>
    public string? ClaimedCluster { get; }
}
