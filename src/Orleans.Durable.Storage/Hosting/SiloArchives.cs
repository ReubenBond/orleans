using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Journaling;

namespace Orleans.Durable.Storage.Hosting;

/// <summary>
/// Builds the silo's archive store from its configuration (orleans.md §5.13, §6) over the stores its services supply,
/// under the cluster's store prefix, which it computes once here: on the LSM, the <see cref="IObjectStore"/> of the
/// bucket, under the prefix; on archive logs, the <see cref="IArchiveLogContainer"/>, which opens the keys' logs and
/// durable storage's objects under the prefix in one container, the logs writing the silo's journal format, Orleans'
/// <see cref="JournaledStateManagerOptions.JournalFormatKey"/>. <c>Orleans.Durable.Storage.AzureStorage</c>'s <c>AddAzureArchiveLogs</c>
/// registers a container over an Azure Blob container, and the tests one over the memory stores. The composition takes no
/// storage technology of its own. Azure Table is no archive-log store.
/// </summary>
internal static class SiloArchives
{
    /// <summary>Returns the silo's archive store as its options and services select it.</summary>
    /// <exception cref="InvalidOperationException">The services lack what the selected store needs, or the options select the memory store.</exception>
    public static SiloArchive Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.GetRequiredService<DurableStorageOptions>();
        var prefix = PrefixOf(options, services.GetRequiredService<IOptions<ClusterOptions>>().Value);
        return options.ArchiveStore switch
        {
            ArchiveStoreKind.Lsm => new SiloArchive.Lsm(
                new PrefixedObjectStore(
                    services.GetService<IObjectStore>() ?? throw new InvalidOperationException(
                        "The silo's archive store is the LSM, and its services supply no IObjectStore: register the LSM's object store, for example Orleans.Durable.Storage.AzureStorage's AzureBlobObjectStore, or the node would never drain, drop, or sweep."),
                    prefix.Value + "/"),
                prefix),
            ArchiveStoreKind.Logs => services.GetService<IArchiveLogContainer>() is { } container
                ? CheckBodyLimit(options, new SiloArchive.Logs(container.OpenLogs(prefix.Value, services.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value.JournalFormatKey), container.OpenObjects(prefix.Value), prefix))
                : throw new InvalidOperationException(
                    "The silo's archive store is archive logs, and its services supply no IArchiveLogContainer: call Orleans.Durable.Storage.AzureStorage's AddAzureArchiveLogs on the silo's services, which registers the logs and durable storage's objects over one container."),
            ArchiveStoreKind.Memory => throw new InvalidOperationException("The silo's archive store is the memory store, which only tests run: its node would never drain."),
            _ => throw new InvalidOperationException($"The silo's archive store is of kind {options.ArchiveStore}, which no silo runs."),
        };
    }

    /// <summary>
    /// Returns the silo's object-ledger store holder as <see cref="DurableStorageOptions.ObjectLedgers"/> and its services
    /// select it (orleans.md §5.14, §6): an unconfigured instance (<see cref="SiloObjectLedgers.CreateUnconfigured"/>) when
    /// <c>ObjectLedgers</c> is unset; otherwise
    /// one provider instance implementing both <see cref="IObjectStore"/> and <see cref="IAppendableObjectStore"/> (resolved
    /// under <see cref="DurableStorageOptions.ObjectLedgersStoreServiceKey"/> when registered, otherwise unkeyed) wrapped
    /// under the cluster's store prefix by <see cref="PrefixedAppendableObjectStore"/> (or
    /// <see cref="PrefixedObjectStore"/> for <see cref="ObjectLedgerLayout.PerSlot"/> when the registered
    /// <see cref="IObjectStore"/> does not implement <see cref="IAppendableObjectStore"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The services lack the object store interface(s) required by <see cref="DurableStorageOptions.ObjectLedgers"/>, or
    /// register distinct instances for <see cref="IObjectStore"/> and <see cref="IAppendableObjectStore"/>.
    /// </exception>
    public static SiloObjectLedgers CreateObjectLedgers(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.GetRequiredService<DurableStorageOptions>();
        if (options.ObjectLedgers is not { } ledgerOptions)
        {
            return SiloObjectLedgers.CreateUnconfigured();
        }

        var prefix = PrefixOf(options, services.GetRequiredService<IOptions<ClusterOptions>>().Value);
        var timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var keyedObjects = services.GetKeyedService<IObjectStore>(DurableStorageOptions.ObjectLedgersStoreServiceKey);
        var keyedAppendable = services.GetKeyedService<IAppendableObjectStore>(DurableStorageOptions.ObjectLedgersStoreServiceKey);
        IObjectStore? objects;
        IAppendableObjectStore? appendable;
        if (keyedObjects is not null || keyedAppendable is not null)
        {
            objects = keyedObjects;
            appendable = keyedAppendable;
        }
        else
        {
            objects = services.GetService<IObjectStore>();
            appendable = services.GetService<IAppendableObjectStore>();
        }
        if (objects is null && appendable is IObjectStore appendableAsObjects)
        {
            objects = appendableAsObjects;
        }

        if (appendable is null && objects is IAppendableObjectStore objectsAsAppendable)
        {
            appendable = objectsAsAppendable;
        }

        if (objects is null)
        {
            throw new InvalidOperationException(
                "The silo configures ObjectLedgers, and its services supply no IObjectStore: register an object store provider (implementing both IObjectStore and IAppendableObjectStore for Append, or IObjectStore for PerSlot).");
        }

        if (appendable is not null && !ReferenceEquals(objects, appendable))
        {
            throw new InvalidOperationException(
                "DurableStorageOptions.ObjectLedgers requires one provider instance implementing both IObjectStore and IAppendableObjectStore, not two distinct instances (orleans.md §5.14).");
        }

        var pacer = new ObjectStoreRequestPacer(timeProvider);
        ObjectLedgerStore store;
        if (appendable is not null)
        {
            var prefixed = new PrefixedAppendableObjectStore(objects, appendable, prefix.Value + "/");
            var paced = PacedAppendableObjectStore.Create(prefixed, pacer);
            store = ObjectLedgerStore.OverAppendable(paced, ledgerOptions, timeProvider);
        }
        else if (ledgerOptions.Layout == ObjectLedgerLayout.Append)
        {
            throw new InvalidOperationException(
                "ObjectLedgers:Layout is Append, which requires an IObjectStore provider instance that also implements IAppendableObjectStore (orleans.md §5.14).");
        }
        else
        {
            var prefixed = new PrefixedObjectStore(objects, prefix.Value + "/");
            var paced = new PacedObjectStore(prefixed, pacer);
            store = ObjectLedgerStore.Over(paced, ledgerOptions, timeProvider);
        }

        return new SiloObjectLedgers(store, prefix, pacer);
    }

    /// <summary>
    /// Returns the cluster's store prefix (orleans.md §5.13): the configured one, or the one derived from Orleans' service
    /// ID, which is the same on every silo of a deployment and across its deployments. A service ID Orleans gives by
    /// default, <see cref="ClusterOptions.DefaultServiceId"/> or localhost clustering's <c>dev</c>, derives no prefix: two
    /// clusters which kept it on one bucket would silently share one store.
    /// </summary>
    /// <exception cref="InvalidOperationException">No prefix is configured and the service ID is empty or one of Orleans' defaults.</exception>
    public static ArchiveStorePrefix PrefixOf(DurableStorageOptions options, ClusterOptions cluster)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cluster);
        if (options.ArchiveStorePrefix is { } configured)
        {
            return new ArchiveStorePrefix(configured);
        }

        if (string.IsNullOrEmpty(cluster.ServiceId))
        {
            throw new InvalidOperationException("The cluster's store prefix derives from Orleans' ServiceId, which is empty: configure ClusterOptions.ServiceId or DurableStorageOptions.ArchiveStorePrefix.");
        }

        if (IsOrleansDefault(cluster.ServiceId))
        {
            throw new InvalidOperationException(
                $"The cluster's store prefix derives from Orleans' ServiceId, which is '{cluster.ServiceId}', one of Orleans' defaults, so two clusters which kept it on one bucket or container would share one archive store: configure the deployment's own ClusterOptions.ServiceId or DurableStorageOptions.ArchiveStorePrefix.");
        }

        return ArchiveStorePrefix.Of(cluster.ServiceId);
    }

    /// <summary>Returns whether <paramref name="serviceId"/> is a service ID Orleans gives by default: its own, or localhost clustering's.</summary>
    private static bool IsOrleansDefault(string serviceId) =>
        serviceId is ClusterOptions.DefaultServiceId or "default" or "dev";

    /// <summary>
    /// Refuses a silo on archive logs whose Journals' <c>MaxBodyBytes</c> passes the log store's largest append
    /// (journals.md §8.4), before its first registration can record the parameter in a new cluster's store record.
    /// </summary>
    private static SiloArchive.Logs CheckBodyLimit(DurableStorageOptions options, SiloArchive.Logs logs)
    {
        if (logs.Store.MaxAppendBytes is { } maxAppendBytes && options.Journals.MaxBodyBytes > maxAppendBytes)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The silo's Journals:MaxBodyBytes, {options.Journals.MaxBodyBytes} bytes, passes its archive-log store's largest append, {maxAppendBytes} bytes (journals.md §8.4)."));
        }

        return logs;
    }
}
