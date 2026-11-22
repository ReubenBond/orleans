namespace Orleans.Durable.Storage.Engine.MetaStore;

/// <summary>
/// Maps a cluster and a register name to an independent conditional register.
/// </summary>
/// <remarks>
/// A register is keyed by <c>(cluster, name)</c>, and every backend keys it <c>{cluster:N}/{name}</c> below its
/// configured root, so clusters sharing a backend keep independent registers (MS-I4, metastore.md §5). Two calls
/// with equal arguments, from any process configured with the same backend, address the same register.
/// </remarks>
public interface IMetaStore
{
    /// <summary>
    /// Gets the conditional register keyed by <paramref name="cluster"/> and <paramref name="name"/>.
    /// </summary>
    /// <param name="cluster">The cluster which owns the register; it must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="name">The register name; see <see cref="MetaStoreRegisterName"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The register. Getting it performs no backend I/O and creates nothing.</returns>
    /// <exception cref="ArgumentException">The cluster is empty or the name is not a valid register name.</exception>
    ValueTask<IConditionalRegister> GetRegisterAsync(
        Guid cluster,
        string name,
        CancellationToken cancellationToken = default);
}
