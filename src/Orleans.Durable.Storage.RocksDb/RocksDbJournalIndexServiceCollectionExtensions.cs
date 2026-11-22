using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Engine.Journals;

namespace Orleans.Durable.Storage.RocksDb;

/// <summary>
/// Registers <see cref="RocksDbJournalIndex"/> as the node's <see cref="IJournalIndex"/> in dependency injection.
/// </summary>
public static class RocksDbJournalIndexServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RocksDbJournalIndex"/> at <paramref name="path"/> as the singleton
    /// <see cref="IJournalIndex"/>.
    /// </summary>
    public static IServiceCollection AddRocksDbJournalIndex(this IServiceCollection services, string path)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return services.AddSingleton<IJournalIndex>(_ => new RocksDbJournalIndex(path));
    }

    /// <summary>
    /// Registers <see cref="RocksDbJournalIndex"/> at <c>{root}/index/</c> (<see cref="RocksDbJournalIndex.ForNode"/>)
    /// as the singleton <see cref="IJournalIndex"/>.
    /// </summary>
    public static IServiceCollection AddRocksDbJournalIndexForNode(this IServiceCollection services, string root)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(root);
        return services.AddSingleton<IJournalIndex>(_ => RocksDbJournalIndex.ForNode(root));
    }
}
