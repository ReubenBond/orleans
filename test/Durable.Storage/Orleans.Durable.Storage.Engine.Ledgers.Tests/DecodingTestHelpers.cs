using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers.Store;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Reads a decoding's value, or throws the rejection it holds, for tests which expect one or the other; and applies a
/// catalog transition the same way.
/// </summary>
internal static class DecodingTestHelpers
{
    /// <summary>
    /// Returns the catalog a create record leaves, or throws the rejection naming how it disagrees.
    /// </summary>
    internal static ReplicaCatalog Created(this ReplicaCatalog catalog, IReadOnlyList<CatalogPair> pairs, ulong file) =>
        catalog.Create(pairs, file, out var created) is { } contradiction ? throw contradiction : created!;

    /// <summary>
    /// Returns the catalog a drop record leaves, or throws the rejection naming how it disagrees.
    /// </summary>
    internal static ReplicaCatalog Dropped(this ReplicaCatalog catalog, LedgerId ledger) =>
        catalog.Drop(ledger, out var dropped) is { } contradiction ? throw contradiction : dropped!;

    /// <summary>
    /// Returns the catalog a create or drop record leaves, or throws the rejection naming how it disagrees.
    /// </summary>
    internal static ReplicaCatalog Applied(this ReplicaCatalog catalog, StoreRecord record, ulong file) =>
        catalog.Apply(record, file, out var applied) is { } contradiction ? throw contradiction : applied!;

    internal static CatalogCheckpoint Checkpoint(this CatalogDecoding decoding) => decoding switch
    {
        CatalogCheckpoint checkpoint => checkpoint,
        InvalidDataException rejection => throw rejection,
        null => throw new UnreachableException(),
    };

    internal static NodeId Node(this NodeFileDecoding decoding) => decoding switch
    {
        NodeId node => node,
        InvalidDataException rejection => throw rejection,
        null => throw new UnreachableException(),
    };

    internal static CheckpointRoot Root(this CheckpointRootDecoding decoding) => decoding switch
    {
        CheckpointRoot root => root,
        InvalidDataException rejection => throw rejection,
        null => throw new UnreachableException(),
    };

    internal static FrozenLeaf Leaf(this SlotLeafDecoding decoding) => decoding switch
    {
        FrozenLeaf leaf => leaf,
        InvalidDataException rejection => throw rejection,
        null => throw new UnreachableException(),
    };

    /// <summary>
    /// Reads a leaf object's bytes, or throws the rejection naming the rule they break.
    /// </summary>
    internal static async ValueTask<byte[]> ReadLeafBytesAsync(this SlotCheckpoints checkpoints, CheckpointRef reference, CancellationToken cancellationToken)
    {
        var bytes = new byte[reference.Length];
        return await checkpoints.ReadObjectAsync(reference, bytes, cancellationToken) is { } rejection ? throw rejection : bytes;
    }
}
