using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// Creates and verifies storage-root cluster claims, remembering each verified root.
/// </summary>
/// <remarks>
/// <para>
/// LS-I12: "Every storage root holds one cluster claim, <c>{root}/cluster</c>. The first creator of a root creates
/// the claim; every provider instance verifies it once per root on create and on resolve; a root claimed by another
/// cluster fails with <c>LedgerStoreClusterMismatchException</c>." The claim holds exactly the 32 lowercase hexadecimal
/// digits of the cluster identifier (ledgerstore.md §7.1).
/// </para>
/// <para>
/// A provider owns one instance and passes every root through <see cref="EnsureAsync"/> when it creates or
/// resolves a replica, or through <see cref="TryVerifyAsync"/> when its resolution only verifies. A verified root
/// needs no further request for the provider's lifetime, so appends never pay for the claim.
/// </para>
/// </remarks>
internal sealed class LedgerStoreClusterClaims
{
    /// <summary>
    /// The claim's key below its root.
    /// </summary>
    internal const string Name = "cluster";

    /// <summary>The length of a claim: 32 hexadecimal digits.</summary>
    internal const int Length = 32;

    private readonly ConcurrentDictionary<string, Guid> _verified = new(StringComparer.Ordinal);

    /// <summary>
    /// Formats a cluster identifier as its claim content.
    /// </summary>
    internal static string Format(Guid cluster) => cluster.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Encodes a cluster identifier as its claim bytes.
    /// </summary>
    internal static byte[] Encode(Guid cluster) => Encoding.ASCII.GetBytes(Format(cluster));

    /// <summary>
    /// Ensures that the claim at <paramref name="claim"/> holds <paramref name="cluster"/>, creating it when
    /// absent.
    /// </summary>
    /// <param name="claim">The claim's location, used as the cache key and in diagnostics.</param>
    /// <param name="cluster">The cluster which requires the root.</param>
    /// <param name="createIfAbsent">
    /// Creates the claim with the supplied content only when no claim exists: returns <see langword="true"/> once it
    /// created the claim, and <see langword="false"/> when a claim exists, which it leaves as it is.
    /// </param>
    /// <param name="read">Reads the claim's content, or returns <see langword="null"/> when it is absent.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="LedgerStoreClusterMismatchException">Another cluster holds the claim.</exception>
    /// <exception cref="LedgerReplicaCorruptException">The claim was found to exist and then read as absent.</exception>
    internal ValueTask EnsureAsync(
        string claim,
        Guid cluster,
        Func<byte[], CancellationToken, ValueTask<bool>> createIfAbsent,
        Func<CancellationToken, ValueTask<byte[]?>> read,
        CancellationToken cancellationToken)
    {
        if (_verified.TryGetValue(claim, out var verified))
        {
            return verified == cluster
                ? ValueTask.CompletedTask
                : throw new LedgerStoreClusterMismatchException(claim, cluster, Format(verified));
        }

        return VerifyAsync(claim, cluster, createIfAbsent, read, cancellationToken);
    }

    /// <summary>
    /// Verifies the claim at <paramref name="claim"/> without creating it: returns <see langword="true"/> when it holds
    /// <paramref name="cluster"/> and <see langword="false"/> when it is absent.
    /// </summary>
    /// <param name="claim">The claim's location, used as the cache key and in diagnostics.</param>
    /// <param name="cluster">The cluster which requires the root.</param>
    /// <param name="read">Reads the claim's content, or returns <see langword="null"/> when it is absent.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="LedgerStoreClusterMismatchException">Another cluster holds the claim.</exception>
    internal async ValueTask<bool> TryVerifyAsync(
        string claim,
        Guid cluster,
        Func<CancellationToken, ValueTask<byte[]?>> read,
        CancellationToken cancellationToken)
    {
        if (_verified.TryGetValue(claim, out var verified))
        {
            return verified == cluster
                ? true
                : throw new LedgerStoreClusterMismatchException(claim, cluster, Format(verified));
        }

        var current = await read(cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return false;
        }

        if (!current.AsSpan().SequenceEqual(Encode(cluster)))
        {
            throw new LedgerStoreClusterMismatchException(claim, cluster, Encoding.UTF8.GetString(current));
        }

        _ = _verified.TryAdd(claim, cluster);
        return true;
    }

    private async ValueTask VerifyAsync(
        string claim,
        Guid cluster,
        Func<byte[], CancellationToken, ValueTask<bool>> createIfAbsent,
        Func<CancellationToken, ValueTask<byte[]?>> read,
        CancellationToken cancellationToken)
    {
        var expected = Encode(cluster);

        // Every other failure of the create propagates: a local storage failure stops the process before the claim's
        // outcome matters (conventions.md §14).
        if (!await createIfAbsent(expected, cancellationToken).ConfigureAwait(false))
        {
            // Another creator's claim holds the name; the stored claim decides, and a claim is never overwritten.
            var current = await read(cancellationToken).ConfigureAwait(false)
                ?? throw new LedgerReplicaCorruptException($"The cluster claim '{claim}' existed when it was created and is absent when read: the root lost its claim.");
            if (!current.AsSpan().SequenceEqual(expected))
            {
                throw new LedgerStoreClusterMismatchException(claim, cluster, Encoding.UTF8.GetString(current));
            }
        }

        _ = _verified.TryAdd(claim, cluster);
    }
}
