using System.Collections.Frozen;
using Orleans.Durable.Storage.Engine.Cluster.Control;

namespace Orleans.Durable.Storage.Control;

/// <summary>Projects node registry values onto what the controller decides from (orleans.md §5.6).</summary>
internal static class RegistryProjection
{
    /// <summary>Returns <paramref name="value"/>'s revision, node standings, object-ledger store presence, and drain-all revision.</summary>
    public static RegisteredNodes ToRegisteredNodes(this NodeRegistryValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(
            value.Revision,
            value.Nodes.ToFrozenDictionary(static entry => entry.Node, static entry => new RegisteredNode(entry.IsRetired ? entry.RetiredRevision : null, entry.FailureDomain, entry.Silo.Generation, entry.Ordinal, entry.Diskless)),
            value.ObjectLedgerStore is not null,
            value.DrainAllRevision);
    }
}
