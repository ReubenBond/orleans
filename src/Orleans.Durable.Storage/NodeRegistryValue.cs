using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Runtime;

namespace Orleans.Durable.Storage;

/// <summary>
/// One value of the <c>node-registry</c> register (orleans.md §5.3, §5.14, §7.3): its revision, which every write
/// increments, one entry per node in ascending node byte order, the cluster's archive store once a registration recorded
/// it, the cluster's object-ledger store ID once a registration recorded it, and the drain-all revision while a planned
/// whole-cluster drain off node-backed ledgers is active. Immutable.
/// </summary>
/// <remarks>
/// An absent register reads as <see cref="Empty"/>, at revision 0. Its binary form is <c>CLNR</c> version 1
/// (<see cref="NodeRegistryCodec"/>).
/// </remarks>
internal sealed class NodeRegistryValue
{
    /// <summary>Initializes a new instance of the <see cref="NodeRegistryValue"/> class.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="nodes"/> is not in ascending node byte order with each node once, an entry's retired revision
    /// lies above <paramref name="revision"/>, <paramref name="archiveStore"/> is the default identity,
    /// <paramref name="objectLedgerStore"/> is the default identity, <paramref name="drainAllRevision"/> lies above
    /// <paramref name="revision"/>, or <paramref name="drainAllRevision"/> is non-zero without an object-ledger store.
    /// </exception>
    internal NodeRegistryValue(
        ulong revision,
        ImmutableArray<NodeRegistryEntry> nodes,
        ArchiveStoreIdentity? archiveStore,
        ObjectLedgerStoreId? objectLedgerStore = null,
        ulong drainAllRevision = 0)
    {
        if (archiveStore == default(ArchiveStoreIdentity))
        {
            throw new ArgumentException("A registry records no archive store as null; the default identity names none.", nameof(archiveStore));
        }

        if (objectLedgerStore == default(ObjectLedgerStoreId))
        {
            throw new ArgumentException("A registry records no object-ledger store as null; the default identity names none.", nameof(objectLedgerStore));
        }

        if (drainAllRevision > revision)
        {
            throw new ArgumentException("A registry's drain-all revision lies at or below its revision.", nameof(drainAllRevision));
        }

        if (drainAllRevision != 0 && objectLedgerStore is null)
        {
            throw new ArgumentException("A registry records drain-all only when an object-ledger store is recorded.", nameof(drainAllRevision));
        }

        nodes = nodes.IsDefault ? [] : nodes;
        var activeNodeCount = 0;
        for (var index = 0; index < nodes.Length; index++)
        {
            if (index > 0 && CompareNodes(nodes[index - 1].Node, nodes[index].Node) >= 0)
            {
                throw new ArgumentException("A registry lists its nodes in ascending node byte order, each once.", nameof(nodes));
            }

            if (nodes[index].RetiredRevision > revision)
            {
                throw new ArgumentException("A node is retired at or below the registry's revision.", nameof(nodes));
            }

            if (!nodes[index].IsRetired)
            {
                activeNodeCount++;
            }
        }

        Revision = revision;
        Nodes = nodes;
        ActiveNodeCount = activeNodeCount;
        ArchiveStore = archiveStore;
        ObjectLedgerStore = objectLedgerStore;
        DrainAllRevision = drainAllRevision;
    }

    /// <summary>The name of the register which holds the node registry (identity.md §9).</summary>
    public const string RegisterName = "node-registry";

    /// <summary>Gets the value of an absent register: revision 0, no nodes, no stores, and no active drain-all.</summary>
    public static NodeRegistryValue Empty { get; } = new(0, [], null);

    /// <summary>Gets the registry's revision: the number of writes the register has applied.</summary>
    public ulong Revision { get; }

    /// <summary>Gets one entry per node, in ascending node byte order.</summary>
    public ImmutableArray<NodeRegistryEntry> Nodes { get; }

    /// <summary>Gets the number of active (non-retired) nodes listed in <see cref="Nodes"/>.</summary>
    public int ActiveNodeCount { get; }

    /// <summary>Gets the cluster's archive store, or <see langword="null"/> before a registration recorded it.</summary>
    internal ArchiveStoreIdentity? ArchiveStore { get; }

    /// <summary>Gets the cluster's object-ledger store ID, or <see langword="null"/> before a registration recorded it (orleans.md §5.14).</summary>
    internal ObjectLedgerStoreId? ObjectLedgerStore { get; }

    /// <summary>
    /// Gets the revision of the write that started an active whole-cluster drain off node-backed ledgers, or <c>0</c> when
    /// no drain-all is active (orleans.md §5.14).
    /// </summary>
    public ulong DrainAllRevision { get; }

    /// <summary>Returns <paramref name="node"/>'s entry, or <see langword="null"/> when the registry has none.</summary>
    public NodeRegistryEntry? Find(NodeId node) => IndexOf(Nodes.AsSpan(), node) is >= 0 and var index ? Nodes[index] : null;

    /// <summary>Returns the entry naming <paramref name="silo"/>, or <see langword="null"/> when none does.</summary>
    public NodeRegistryEntry? FindBySilo(SiloAddress silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        foreach (var entry in Nodes)
        {
            if (entry.Silo.Equals(silo))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Returns whether <paramref name="other"/> holds the same revision, stores, drain-all revision, and entries.</summary>
    public bool ContentEquals(NodeRegistryValue other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Revision == other.Revision
            && ArchiveStore == other.ArchiveStore
            && ObjectLedgerStore == other.ObjectLedgerStore
            && DrainAllRevision == other.DrainAllRevision
            && Nodes.AsSpan().SequenceEqual(other.Nodes.AsSpan());
    }

    /// <summary>Compares two nodes by their identities' 16 bytes, big-endian, as <c>CLNR</c> orders its entries.</summary>
    internal static int CompareNodes(NodeId left, NodeId right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        _ = left.Value.TryWriteBytes(leftBytes, bigEndian: true, out _);
        _ = right.Value.TryWriteBytes(rightBytes, bigEndian: true, out _);
        return leftBytes.SequenceCompareTo(rightBytes);
    }

    /// <summary>Returns the position of <paramref name="node"/> in <paramref name="nodes"/>, in node byte order, or the complement of where it belongs.</summary>
    internal static int IndexOf(ReadOnlySpan<NodeRegistryEntry> nodes, NodeId node)
    {
        int lower = 0, upper = nodes.Length - 1;
        while (lower <= upper)
        {
            var middle = lower + (upper - lower) / 2;
            var order = CompareNodes(nodes[middle].Node, node);
            if (order == 0)
            {
                return middle;
            }

            (lower, upper) = order < 0 ? (middle + 1, upper) : (lower, middle - 1);
        }

        return ~lower;
    }
}
