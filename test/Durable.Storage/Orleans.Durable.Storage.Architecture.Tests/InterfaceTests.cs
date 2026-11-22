using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Production code declares an interface only for a named I/O boundary or grain contract, which several providers
/// implement in full (implementation plan, decision 11): a component which one class implements is that class, and no
/// interface adds an optional capability to another. A test or simulation double counts as a provider only where it
/// stands in for external I/O, a process boundary, or time. A new interface is an explicit addition to this list, with
/// the reason it qualifies.
/// </summary>
public sealed class InterfaceTests
{
    private static readonly string[] Interfaces =
    [
        // The group committer's binding to each value it writes, over the value's store and its fold, and a fold's
        // decision of one batch: the ledger map's, the manifest's, and the node registry's (conventions.md §6).
        "Orleans.Durable.Storage.Engine.GroupCommit.IGroupCommitBatch`3",
        "Orleans.Durable.Storage.Engine.GroupCommit.IGroupCommitRules`5",

        // The appendable objects of an object store: the memory, file-system, and Azure providers, and the doubles which
        // inject its failures and stand in for it in simulation (object-ledgers.md §3.1).
        "Orleans.Durable.Storage.Engine.ObjectStore.IAppendableObjectStore",

        // The object store: the memory and file-system providers, the cloud providers, and the doubles which inject its
        // failures and stand in for it in simulation (archive §3).
        "Orleans.Durable.Storage.Engine.ObjectStore.IObjectStore",

        // The store the live ledgers drain into: the LSM's and archive logs' archive stores, and the tests' memory store
        // (archive.md §3).
        "Orleans.Durable.Storage.Engine.Archive.IArchiveStore",

        // The archive-log store: the memory store, Azure's append blobs, external I/O, and the chunk layout, not yet in code
        // (archive-logs.md §3, §9).
        "Orleans.Durable.Storage.Engine.Archive.Logs.IArchiveLogStore",

        // The container of a cluster's archive logs, which opens its logs and durable storage's objects under its store prefix:
        // Azure's, the memory container, and the tests' (archive-logs.md §3, orleans.md §5.13).
        "Orleans.Durable.Storage.Engine.Archive.Logs.IArchiveLogContainer",

        // The replica contract: the memory and LedgerStore replicas, and the transports' handles of another node's
        // (decision 12).
        "Orleans.Durable.Storage.Engine.Ledgers.ILedgerReplica",

        // Any node's replicas: the memory transport, the simulation's, which stands in for the network between nodes,
        // and the Orleans composition's.
        "Orleans.Durable.Storage.Engine.Ledgers.ILedgerReplicaTransport",

        // A node's listing and drops: the LedgerStore, and the memory and simulation transports' nodes.
        "Orleans.Durable.Storage.Engine.Ledgers.ILedgerStore",

        // A ledger's storage below its Ledger: the quorum over its node replicas, and the object-store ledger, not yet in
        // code (ledger-protocol.md §2.7, object-ledgers.md).
        "Orleans.Durable.Storage.Engine.Ledgers.ILedgerStorage",

        // The node root's cluster claim, a file: external I/O, which the test and simulation doubles stand in for.
        "Orleans.Durable.Storage.Engine.Ledgers.Store.INodeRootClaim",

        // The controller grain's contract, which Orleans' generated proxy implements across silos (orleans.md §7.2).
        "Orleans.Durable.Storage.Control.IClusterControllerGrain",

        // A node's control endpoint, which Orleans' generated proxy implements across silos (orleans.md §7.2).
        "Orleans.Durable.Storage.Control.INodeControlGrain",

        // A node's journal endpoint, which Orleans' generated proxy implements across silos (orleans.md §5.5, §7.2).
        "Orleans.Durable.Storage.IJournalStoreGrain",

        // A node's replica endpoint, which Orleans' generated proxy implements across silos, and the transport tests'
        // double stands in for the process boundary (orleans.md §5.12, §7.2).
        "Orleans.Durable.Storage.ILedgerStoreGrain",

        // The MetaStore's register and store: the memory, file-system, Fast CASPaxos, and cloud providers.
        "Orleans.Durable.Storage.Engine.MetaStore.IConditionalRegister",
        "Orleans.Durable.Storage.Engine.MetaStore.IMetaStore",

        // The node's derived journal index: the RocksDB provider and the memory provider for simulation and unit tests
        // (journals.md §8.3).
        "Orleans.Durable.Storage.Engine.Journals.IJournalIndex",

        // An open file: the operating system's, the reporting wrapper of the WAL's fail-stop seam, and the test doubles
        // which stand in for the file system's I/O.
        "Orleans.Durable.Storage.Engine.Wal.FileSystem.IWalFileSystemFile",
    ];

    [Fact]
    public void ProductionInterfacesAreTheNamedBoundaries()
    {
        var declared = new List<string>();
        foreach (var (project, assemblyPath) in Repository.ProductionAssemblies())
        {
            Assert.True(File.Exists(assemblyPath), $"{project} is not referenced by Orleans.Durable.Storage.Architecture.Tests.");
            using var reader = new PEReader(File.OpenRead(assemblyPath));
            var metadata = reader.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                if (type.Attributes.HasFlag(TypeAttributes.Interface) && !metadata.GetString(type.Name).StartsWith('<'))
                {
                    declared.Add(FullName(metadata, type));
                }
            }
        }

        Assert.Equal(Interfaces.Order(StringComparer.Ordinal), declared.Order(StringComparer.Ordinal));
    }

    private static string FullName(MetadataReader metadata, TypeDefinition type)
    {
        var name = metadata.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        return declaring.IsNil
            ? $"{metadata.GetString(type.Namespace)}.{name}"
            : $"{FullName(metadata, metadata.GetTypeDefinition(declaring))}+{name}";
    }
}
