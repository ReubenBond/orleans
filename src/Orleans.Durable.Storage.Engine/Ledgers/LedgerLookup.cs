using System.Collections.Immutable;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The ledgers of a map value whose ranges contain one key (ledgers.md §2.3): the open ledger, and every closed ledger,
/// in ID order.
/// </summary>
public readonly record struct LedgerLookup
{
    /// <summary>Initializes a new instance of the <see cref="LedgerLookup"/> struct.</summary>
    /// <param name="open">The open ledger whose range contains the key.</param>
    /// <param name="closed">The closed ledgers whose ranges contain the key, in ID order.</param>
    public LedgerLookup(LedgerMetadata open, ImmutableArray<LedgerMetadata> closed)
    {
        Open = open;
        Closed = closed.IsDefault ? [] : closed;
    }

    /// <summary>Gets the open ledger whose range contains the key: one, by LG-I1.</summary>
    public LedgerMetadata Open { get; }

    /// <summary>Gets the closed ledgers whose ranges contain the key, in ID order: the key's earlier owners still listed.</summary>
    public ImmutableArray<LedgerMetadata> Closed { get; }

    /// <inheritdoc/>
    public bool Equals(LedgerLookup other) => Open.Equals(other.Open) && Closed.AsSpan().SequenceEqual(other.Closed.AsSpan());

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Open, Closed.Length);
}
