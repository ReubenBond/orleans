using Orleans.Consensus.CasPaxos.Grpc;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.CasPaxos;

/// <summary>
/// A MetaStore whose registers share one Fast CASPaxos register client, keyed <c>{cluster:N}/{name}</c>.
/// </summary>
/// <remarks>
/// A factory constructed from endpoints owns its channels. An injected register service remains
/// caller-owned, allowing embedded services and the shared upstream simulation to use the same adapter.
/// </remarks>
public sealed class CasPaxosMetaStoreFactory : IMetaStore, IDisposable
{
    private readonly IRegisterService _registers;
    private readonly GrpcRegisterClient? _ownedClient;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="CasPaxosMetaStoreFactory"/> class with owned gRPC channels.
    /// </summary>
    /// <param name="endpoints">The hosts' gRPC endpoints.</param>
    /// <param name="timeProvider">The clock used for client deadlines.</param>
    /// <param name="options">The bounded client operation and transport settings.</param>
    public CasPaxosMetaStoreFactory(
        IEnumerable<Uri> endpoints,
        TimeProvider timeProvider,
        GrpcRegisterClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var client = new GrpcRegisterClient(endpoints.ToArray(), options, timeProvider);
        _registers = client;
        _ownedClient = client;
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
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ownedClient?.Dispose();
        }
    }
}
