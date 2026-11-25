using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Serialization;

namespace Orleans.Durable.Storage.Hosting;

/// <summary>Registers a durable storage node in an Orleans silo (orleans.md §2).</summary>
public static class DurableStorageSiloBuilderExtensions
{
    /// <summary>
    /// Registers the silo's one durable storage node as Orleans Journaling's default journal storage provider, with the silo's options
    /// bound from <paramref name="configuration"/>: a section, the silo's durable storage options alone, whose <c>Node</c> and
    /// <c>Control</c> subsections name the members of <see cref="DurableStorageOptions"/> and <see cref="ControlOptions"/>
    /// (orleans.md §6). A key which names no option fails the silo's start, so a misspelled option never goes unnoticed; a
    /// configuration which names an option a later release adds stops an earlier release's silo, as a rollback meets it.
    /// The silo validates the options as it starts, and does not start with a missing or out-of-range option.
    /// </summary>
    /// <remarks>
    /// The node: its LedgerStore and registration (<see cref="DurableStorageNode"/>), the node registry resolver, the
    /// placement of node grains and of journaled grains (<see cref="JournalPlacement"/>), the controller grain and the node
    /// control grain, the node's Ledgers runtime and control loop (<see cref="NodeLedgers"/>), with what the node observes
    /// of itself (<see cref="DurableStorageStatus"/>), the node's journal endpoint behind <see cref="IJournalStoreGrain"/>,
    /// with the snapshots it pins for forwarded reads, the sweep host, and the replica transport: the node's replica host
    /// behind <see cref="ILedgerStoreGrain"/> and the silo's <see cref="ILedgerReplicaTransport"/>, within the
    /// <see cref="ReplicaCallLimits"/> computed from the options and Orleans' messaging options. The silo's services supply
    /// the <see cref="IMetaStore"/> holding the cluster's registers; the <see cref="WalStorage"/>, by default
    /// <see cref="PortableWalStorage"/>; a <see cref="TimeProvider"/>, by default the system's; on the LSM, its
    /// <see cref="IObjectStore"/>, with an <see cref="ObjectStoreMaintenance"/> when the store needs one; and on archive logs,
    /// the <c>IArchiveLogContainer</c> of the logs and durable storage's objects in one container, which <c>Orleans.Durable.Storage.AzureStorage</c>'s
    /// <c>AddAzureArchiveLogs</c> registers (<see cref="SiloArchives"/>). The silo places either under the cluster's
    /// store prefix, <see cref="DurableStorageOptions.ArchiveStorePrefix"/>. The silo does not start unless they supply what
    /// its archive store, <see cref="DurableStorageOptions.ArchiveStore"/>, needs.
    /// </remarks>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configuration">The silo's durable storage section, for example <c>configuration.GetSection("DurableStorage")</c>.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="InvalidOperationException">The silo already hosts a durable storage node: a silo hosts one (OR-I9).</exception>
    public static ISiloBuilder AddDurableJournalStorage(this ISiloBuilder builder, IConfiguration configuration) =>
        builder.AddDurableJournalStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configuration);

    /// <summary>
    /// Registers the silo's one durable storage node, as <see cref="AddDurableJournalStorage(ISiloBuilder, IConfiguration)"/> does, as the
    /// Orleans Journaling provider named <paramref name="providerName"/>: the keyed <c>IJournalStorageProvider</c> and
    /// journaled state manager factory of that name, or the default ones for Orleans' default name. A silo hosts one node
    /// (OR-I9), so it registers the node under one name, and its options are the silo's unnamed
    /// <see cref="DurableStorageSiloOptions"/> whatever the provider's name, unlike Orleans' providers, whose options a
    /// named provider names after itself.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="providerName">The journal storage provider's name.</param>
    /// <param name="configuration">The silo's durable storage section.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="InvalidOperationException">The silo already hosts a durable storage node: a silo hosts one (OR-I9).</exception>
    public static ISiloBuilder AddDurableJournalStorage(this ISiloBuilder builder, string providerName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return builder.AddDurableJournalStorage(providerName, options => options.Bind(configuration, static binder => binder.ErrorOnUnknownConfiguration = true));
    }

    /// <summary>
    /// Registers the silo's one durable storage node, as <see cref="AddDurableJournalStorage(ISiloBuilder, IConfiguration)"/> does, with
    /// options which <paramref name="configure"/> builds through Microsoft.Extensions.Options: binding a configuration
    /// section, configuring them in code, or both. The silo validates them as it starts, and does not start with a missing
    /// or out-of-range option.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">Configures the silo's options.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="InvalidOperationException">The silo already hosts a durable storage node: a silo hosts one (OR-I9).</exception>
    public static ISiloBuilder AddDurableJournalStorage(this ISiloBuilder builder, Action<OptionsBuilder<DurableStorageSiloOptions>> configure) =>
        builder.AddDurableJournalStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configure);

    /// <summary>
    /// Registers the silo's one durable storage node under the journal storage provider named <paramref name="providerName"/>, as
    /// <see cref="AddDurableJournalStorage(ISiloBuilder, string, IConfiguration)"/> does, with options which
    /// <paramref name="configure"/> builds as <see cref="AddDurableJournalStorage(ISiloBuilder, Action{OptionsBuilder{DurableStorageSiloOptions}})"/>
    /// takes them: the silo's unnamed options, whatever the provider's name (OR-I9).
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="providerName">The journal storage provider's name.</param>
    /// <param name="configure">Configures the silo's options.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="InvalidOperationException">The silo already hosts a durable storage node: a silo hosts one (OR-I9).</exception>
    public static ISiloBuilder AddDurableJournalStorage(this ISiloBuilder builder, string providerName, Action<OptionsBuilder<DurableStorageSiloOptions>> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(configure);
        var services = builder.Services;
        if (services.Any(static service => service.ServiceType == typeof(DurableStorageNode)))
        {
            throw new InvalidOperationException("A silo hosts one durable storage node (OR-I9).");
        }

        configure(services.AddOptions<DurableStorageSiloOptions>().ValidateOnStart());
        _ = services.AddSingleton<IValidateOptions<DurableStorageSiloOptions>, DurableStorageSiloOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<WalStorage, PortableWalStorage>();
        _ = services.AddSingleton(static provider => provider.GetRequiredService<IOptions<DurableStorageSiloOptions>>().Value.Node);
        _ = services.AddSingleton(static provider => provider.GetRequiredService<IOptions<DurableStorageSiloOptions>>().Value.Control);
        _ = services.AddSingleton<NodeRegistryResolver>();
        _ = services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(static provider => provider.GetRequiredService<NodeRegistryResolver>());
        _ = services.AddSingleton<LocalNode>();
        _ = services.AddSingleton(static provider => ReplicaCallLimits.From(
            provider.GetRequiredService<DurableStorageOptions>(),
            provider.GetRequiredService<IOptions<SiloMessagingOptions>>().Value));
        _ = services.AddSingleton<DurableStorageNode>();
        _ = services.AddSingleton<LedgerReplicaHost>();
        _ = services.AddSingleton<OrleansLedgerReplicaTransport>();
        _ = services.AddSingleton<ILedgerReplicaTransport>(static provider => provider.GetRequiredService<OrleansLedgerReplicaTransport>());
        _ = services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(static provider => provider.GetRequiredService<DurableStorageNode>());
        _ = services.AddSingleton(SiloArchives.Create);
        _ = services.AddSingleton(SiloArchives.CreateObjectLedgers);
        _ = services.AddSingleton<LocalNodeControl>();
        _ = services.AddSingleton<LocalJournals>();
        _ = services.AddSingleton<ForwardedReads>();
        _ = services.AddSingleton<JournalNodeHost>();
        _ = services.AddSingleton<LocalRouting>();
        _ = services.AddSingleton<ForwardingBudgets>();
        _ = services.AddSingleton(static provider =>
        {
            var grains = provider.GetRequiredService<IGrainFactory>();
            return new JournalRouter(
                provider.GetRequiredService<LocalRouting>(),
                provider.GetRequiredService<LocalJournals>(),
                provider.GetRequiredService<NodeRegistryResolver>().RequestReread,
                provider.GetRequiredService<ForwardingBudgets>(),
                provider.GetRequiredService<ReplicaCallLimits>(),
                node => grains.GetGrain<IJournalStoreGrain>(node.Value),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<JournalRouter>>());
        });
        _ = services.AddSingleton<JournalPlacementDirector>();
        _ = services.AddSingleton(static provider =>
        {
            var strategies = provider.GetRequiredService<PlacementStrategyResolver>();
            return new JournalReplacement(
                provider.GetRequiredService<LocalRouting>(),
                provider.GetRequiredService<JournalPlacementDirector>(),
                provider.GetRequiredService<IClusterMembershipService>(),
                provider.GetRequiredService<IClusterManifestProvider>(),
                provider.GetRequiredService<ILocalSiloDetails>(),
                provider.GetRequiredService<IGrainContextAccessor>(),
                type => strategies.GetPlacementStrategy(type) is JournalPlacement,
                provider.GetRequiredService<ControlOptions>().MapRefreshInterval,
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILoggerFactory>());
        });
        _ = services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(static provider => provider.GetRequiredService<JournalReplacement>());
        _ = services.AddSingleton(static provider => new DurableJournalStorageProvider(
            provider.GetRequiredService<JournalRouter>(),
            provider.GetRequiredService<LocalRouting>(),
            provider.GetRequiredService<JournalReplacement>(),
            provider.GetRequiredService<DurableStorageOptions>(),
            provider.GetRequiredService<IOptions<JournaledStateManagerOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<MessageChannelRegistration>().KnownChannels));
        _ = builder.AddJournalStorage(providerName, static provider => provider.GetRequiredService<DurableJournalStorageProvider>());
        _ = services.AddSingleton(static provider => new NodePressure(
            provider.GetRequiredService<ControlOptions>().ControlInterval,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<NodePressure>>()));
        _ = services.AddSingleton(static provider =>
        {
            var handlers = provider.GetServices<IMessageHandler>().ToList();
            var processors = provider.GetServices<IMessageProcessor>().ToList();
            IDisposable? ownedDisposable = null;
            if (!handlers.Any(static h => h.Channel == ReminderMessageHandler.ReminderChannel)
                && !processors.Any(static p => p.Channel == ReminderMessageHandler.ReminderChannel)
                && provider.GetService<ISealedJobShardPublisher>() is { } publisher)
            {
                var nodeOptions = provider.GetRequiredService<DurableStorageOptions>();
                var time = provider.GetRequiredService<TimeProvider>();
                var loggers = provider.GetRequiredService<ILoggerFactory>();
                var dispatcher = new GrainFactoryReminderDispatcher(provider.GetRequiredService<IGrainFactory>());
                var reminderHandler = new ReminderMessageHandler(nodeOptions.Reminders, dispatcher, time, loggers.CreateLogger<ReminderMessageHandler>());
                var reminderProcessor = new ReminderMessageProcessor(publisher, nodeOptions.Reminders, time, loggers.CreateLogger<ReminderMessageProcessor>());
                handlers.Add(reminderHandler);
                processors.Add(reminderProcessor);
                ownedDisposable = reminderProcessor;
            }

            return new MessageChannelRegistration([.. handlers], [.. processors], ownedDisposable);
        });
        _ = services.AddSingleton<NodeLedgers>();
        _ = services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(static provider => provider.GetRequiredService<NodeLedgers>());
        _ = services.AddSingleton(static provider => new SweepHost(
            provider.GetRequiredService<ControlOptions>(),
            Sweeps(provider),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>(),
            ObjectLedgersSweeps(provider)));
        _ = services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(static provider => provider.GetRequiredService<SweepHost>());
        _ = builder.AddPlacementDirector<RegisteredNodePlacement, RegisteredNodePlacementDirector>();
        _ = builder.AddPlacementDirector<ControllerPlacement, ControllerPlacementDirector>();
        _ = builder.AddPlacementDirector<JournalPlacement>(static provider => provider.GetRequiredService<JournalPlacementDirector>());
        _ = services.AddSingleton<DurableStorageStatus>();

        // A storage failure a grain throws crosses with its whole chain (orleans.md §7.2).
        _ = services.Configure<ExceptionSerializationOptions>(static serialization => serialization.SupportedNamespacePrefixes.Add("Orleans.Durable.Storage"));
        return builder;
    }

    /// <summary>
    /// Registers the silo's durable <see cref="IJournalIndex"/> from the node's <see cref="DurableStorageOptions.Root"/>,
    /// such as <c>builder.AddDurableJournalIndex(RocksDbJournalIndex.ForNode)</c> (orleans.md §5.10, journals.md §8.3).
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="createIndex">Builds the node's <see cref="IJournalIndex"/> from <see cref="DurableStorageOptions.Root"/>.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddDurableJournalIndex(this ISiloBuilder builder, Func<string, IJournalIndex> createIndex)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createIndex);
        _ = builder.Services.AddSingleton(provider => createIndex(provider.GetRequiredService<DurableStorageOptions>().Root));
        return builder;
    }

    /// <summary>
    /// Returns the sweeps of the silo's archive store, which wait for its archive gate (orleans.md §5.6, §5.13): the LSM's
    /// sweeper over its object store, or archive logs' sweeper over the gate's evidence.
    /// </summary>
    private static ArchiveSweeps Sweeps(IServiceProvider provider)
    {
        var archive = provider.GetRequiredService<SiloArchive>();
        var time = provider.GetRequiredService<TimeProvider>();
        var loggers = provider.GetRequiredService<ILoggerFactory>();
        return archive switch
        {
            SiloArchive.Logs logs => logs.Sweeps(provider.GetRequiredService<IMetaStore>(), provider.GetRequiredService<DurableStorageOptions>().Cluster, loggers),
            SiloArchive.Lsm lsm => lsm.Sweeps(provider.GetService<ObjectStoreMaintenance>(), time, loggers),
            _ => throw new UnreachableException("A silo's archive store is the LSM or archive logs."),
        };
    }

    private static ObjectLedgerSweeps? ObjectLedgersSweeps(IServiceProvider provider)
    {
        var objectLedgers = provider.GetRequiredService<SiloObjectLedgers>();
        return objectLedgers.IsConfigured
            ? new ObjectLedgerSweeps(objectLedgers.Gate.Passed, provider.GetRequiredService<ILoggerFactory>())
            : null;
    }
}
