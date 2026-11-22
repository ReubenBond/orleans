using Orleans.Consensus.CasPaxos.Server;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.CasPaxos;

/// <summary>
/// A MetaStore whose registers share one Fast CASPaxos register service, keyed <c>{cluster:N}/{name}</c>.
/// </summary>
/// <remarks>
/// The register service is caller-owned, so embedded services, the Orleans-hosted consensus integration, and the shared
/// consensus simulation use the same adapter. Disposing the factory refuses new registers and leaves the service and
/// existing registers to their owner.
/// </remarks>
public sealed class CasPaxosMetaStoreFactory : IMetaStore, IDisposable
{
    private readonly IRegisterService _registers;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CasPaxosMetaStoreFactory"/> class over a caller-owned service.
    /// </summary>
    /// <param name="registers">The register service.</param>
    public CasPaxosMetaStoreFactory(IRegisterService registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        _registers = registers;
    }

    /// <inheritdoc/>
    public ValueTask<IConditionalRegister> GetRegisterAsync(
        Guid cluster,
        string name,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = MetaStoreRegisterName.ToKey(cluster, name);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IConditionalRegister>(new CasPaxosMetaStore(_registers, key));
    }

    /// <inheritdoc/>
    public void Dispose() => Volatile.Write(ref _disposed, 1);
}
