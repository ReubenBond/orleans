using Orleans.Durable.Storage.Engine.Wal.FileSystem;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// Reads and publishes the cluster claim of a node root.
/// </summary>
/// <remarks>
/// The claim is created only when absent, never replaced; <see cref="LedgerStoreClusterClaims"/> reads a claim a creation
/// found and decides whether it verifies the root (LS-I12, ledgerstore.md §7.1).
/// </remarks>
internal interface INodeRootClaim
{
    /// <summary>
    /// Reads the claim's content, or returns <see langword="null"/> when it is absent.
    /// </summary>
    /// <remarks>
    /// A claim which flushes to disk flushes the root after it reads and before it answers, so the claim the read
    /// returns, even one a competing process linked just before the read, and every directory the store holds in the
    /// root, survive an operating-system crash (conventions.md §13, ledgerstore.md §8.7).
    /// </remarks>
    ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates the claim with <paramref name="content"/> only when no claim exists: returns <see langword="true"/> once
    /// it created the claim, and <see langword="false"/> when a claim exists, which it leaves as it is.
    /// </summary>
    ValueTask<bool> CreateIfAbsentAsync(byte[] content, CancellationToken cancellationToken);
}

/// <summary>
/// Claims a node root directory for one cluster.
/// </summary>
/// <remarks>
/// <c>{root}/cluster</c> is the node root's cluster claim (LS-I12, ledgerstore.md §7.1). Every file-system call goes through
/// the WAL's <see cref="WalFileSystem"/>. The node root is an operator's: it exists, durably, before the store opens, and
/// Durable Storage creates and flushes only the root and what lies beneath it.
/// </remarks>
internal static class FileNodeRootClaim
{
    /// <summary>
    /// Returns the claim of <paramref name="baseDirectory"/>, published with an atomic no-replace create.
    /// </summary>
    internal static INodeRootClaim Claim(string baseDirectory, bool flushToDisk) => Claim(baseDirectory, flushToDisk, WalFileSystem.Os);

    /// <summary>
    /// Returns the claim of <paramref name="baseDirectory"/> in <paramref name="fileSystem"/>.
    /// </summary>
    internal static INodeRootClaim Claim(string baseDirectory, bool flushToDisk, WalFileSystem fileSystem) =>
        new FileClaim(baseDirectory, flushToDisk, fileSystem);

    /// <summary>
    /// Ensures that <paramref name="baseDirectory"/> is claimed by <paramref name="cluster"/>.
    /// </summary>
    internal static ValueTask EnsureClaimAsync(
        LedgerStoreClusterClaims claims,
        string baseDirectory,
        Guid cluster,
        bool flushToDisk,
        CancellationToken cancellationToken)
    {
        var claim = Claim(baseDirectory, flushToDisk);
        return claims.EnsureAsync(
            Path.Combine(baseDirectory, LedgerStoreClusterClaims.Name),
            cluster,
            claim.CreateIfAbsentAsync,
            claim.ReadAsync,
            cancellationToken);
    }

    private sealed class FileClaim(string baseDirectory, bool flushToDisk, WalFileSystem fileSystem) : INodeRootClaim
    {
        private readonly string _claimPath = Path.Combine(baseDirectory, LedgerStoreClusterClaims.Name);

        public async ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            ThrowIfRootMissing();
            var claim = await fileSystem.ReadFileAsync(_claimPath, cancellationToken).ConfigureAwait(false);

            // A read makes every name in the root durable before it answers: a claim whose publication's flush never
            // completed, one a competing process linked after an earlier flush, and a store directory a crashed creator
            // left unsynced. The flush follows the read, so the name the read saw is among them (LS-I12, conventions.md
            // §13, ledgerstore.md §8.7).
            if (flushToDisk)
            {
                fileSystem.FlushDirectory(baseDirectory, DirectoryEntries.Files);
            }

            return claim;
        }

        public async ValueTask<bool> CreateIfAbsentAsync(byte[] content, CancellationToken cancellationToken)
        {
            ThrowIfRootMissing();

            // LS-I12: publish the complete claim with one atomic create-if-absent operation. A failure of a file-system call
            // stops the process (conventions.md §14) and leaves the temporary, which no reader of the root reads.
            var temporaryPath = Path.Combine(baseDirectory, $".{LedgerStoreClusterClaims.Name}.{Guid.NewGuid():N}.tmp");
            try
            {
                using var file = fileSystem.CreateFile(temporaryPath, replace: false, writeThrough: false, preallocationSize: 0);
                await file.WriteAsync(content, 0, cancellationToken).ConfigureAwait(false);
                if (flushToDisk)
                {
                    file.Flush();
                }
            }
            catch (OperationCanceledException)
            {
                // The caller's cancellation, which is no failure: the abandoned temporary goes.
                fileSystem.DeleteFile(temporaryPath);
                throw;
            }

            bool created;
            try
            {
                fileSystem.LinkFile(temporaryPath, _claimPath);
                created = true;
            }
            catch (WalNameExistsException)
            {
                // Another creator's claim holds the name: an answer, which the stored claim decides.
                created = false;
            }

            if (created && flushToDisk)
            {
                fileSystem.FlushDirectory(baseDirectory, DirectoryEntries.Files);
            }

            fileSystem.DeleteFile(temporaryPath);
            return created;
        }

        /// <summary>
        /// Refuses a node root which does not exist: an operator creates it, durably, before the store opens.
        /// </summary>
        private void ThrowIfRootMissing()
        {
            if (!fileSystem.DirectoryExists(baseDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"The node root '{baseDirectory}' does not exist. An operator creates it, durably, before the store opens; durable storage creates only what lies beneath it.");
            }
        }
    }
}
