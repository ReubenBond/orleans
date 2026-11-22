using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>Registers the native Fast CASPaxos MetaStore in an Orleans silo (caspaxos.md).</summary>
public static class CasPaxosSiloBuilderExtensions
{
    /// <summary>
    /// Registers the silo's <see cref="IMetaStore"/> as the native Fast CASPaxos MetaStore, with its options bound from
    /// <paramref name="configuration"/>: a section whose <c>Voters</c> list names every voter's <c>Id</c>,
    /// <c>Endpoint</c>, and <c>Database</c>, with this silo's <c>ProposerId</c> and <c>ProposerDatabase</c>
    /// (<see cref="CasPaxosMetaStoreOptions"/>). A key which names no option fails the silo's start.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configuration">The section, for example <c>configuration.GetSection("CasPaxos")</c>.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddCasPaxosMetaStore(this ISiloBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return builder.AddCasPaxosMetaStore(options => options.Bind(configuration, static binder => binder.ErrorOnUnknownConfiguration = true));
    }

    /// <summary>
    /// Registers the silo's <see cref="IMetaStore"/> as the native Fast CASPaxos MetaStore, with options which
    /// <paramref name="configure"/> builds through Microsoft.Extensions.Options. The silo validates them as it starts.
    /// </summary>
    /// <remarks>
    /// The silo whose advertised endpoint is a voter's serves that voter through a system target, over the voter's
    /// SQLite database, which it recovers and whose identity binding it validates before the runtime initializes; a fresh
    /// voter's database answers once a majority of the other voters admit it. Every silo's proposer runs the protocol
    /// against every voter over Orleans RPC, addressing each voter at its endpoint's newest live silo in membership, and
    /// keeps its ballot reservations in its own SQLite database. A failed SQLite operation stops the process
    /// (conventions.md §14). The silo waits for its voter's admission after joining membership and before it becomes
    /// active, so durable storage's node registers once its consensus serves.
    /// </remarks>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">Configures the options.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="InvalidOperationException">The silo already registers the native CASPaxos MetaStore.</exception>
    public static ISiloBuilder AddCasPaxosMetaStore(this ISiloBuilder builder, Action<OptionsBuilder<CasPaxosMetaStoreOptions>> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var services = builder.Services;
        if (services.Any(static service => service.ServiceType == typeof(CasPaxosConsensusHost)))
        {
            throw new InvalidOperationException("A silo registers the native CASPaxos MetaStore once.");
        }

        configure(services.AddOptions<CasPaxosMetaStoreOptions>().ValidateOnStart());
        _ = services.AddSingleton<IValidateOptions<CasPaxosMetaStoreOptions>, CasPaxosMetaStoreOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        _ = services.AddSingleton<CasPaxosConsensusHost>();
        _ = services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(static provider => provider.GetRequiredService<CasPaxosConsensusHost>());
        _ = services.AddSingleton<IMetaStore>(static provider => new CasPaxosMetaStoreFactory(provider.GetRequiredService<CasPaxosConsensusHost>()));
        return builder;
    }
}
