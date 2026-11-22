using System.Collections.Concurrent;

namespace Orleans.Durable.Storage.Engine.MetaStore;

/// <summary>
/// Keeps every register in process memory, for tests and single-process use.
/// </summary>
/// <remarks>
/// Registers are keyed by <c>(cluster, name)</c> and are lost when the store is discarded.
/// </remarks>
public sealed class InMemoryMetaStore : IMetaStore
{
    private readonly ConcurrentDictionary<string, InMemoryConditionalRegister> _registers =
        new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public ValueTask<IConditionalRegister> GetRegisterAsync(
        Guid cluster,
        string name,
        CancellationToken cancellationToken = default)
    {
        var key = MetaStoreRegisterName.ToKey(cluster, name);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IConditionalRegister>(
            _registers.GetOrAdd(key, static _ => new InMemoryConditionalRegister()));
    }
}
