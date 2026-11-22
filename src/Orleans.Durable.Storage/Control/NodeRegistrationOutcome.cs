using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// The answer to a silo's registration (orleans.md §5.3, §7.2), decided from a node registry value read after the
/// registration was submitted, or written by a write which landed (OR-I19). Every case but
/// <see cref="Registered"/> stops the silo.
/// </summary>
internal readonly union NodeRegistrationOutcome(
    NodeRegistrationOutcome.Registered,
    NodeRegistrationOutcome.Stale,
    NodeRegistrationOutcome.WrongArchiveStore,
    NodeRegistrationOutcome.Refused,
    NodeRegistrationOutcome.OrdinalOccupied,
    NodeRegistrationOutcome.WrongObjectLedgerStore) : IEquatable<NodeRegistrationOutcome>
{
    /// <summary>
    /// The registry at <paramref name="Revision"/> holds the node's entry at the silo's own address and generation: active,
    /// or retired, when the silo of a retired node restarted, which serves its replicas until its retirement gate opens.
    /// </summary>
    /// <param name="Revision">The revision of the deciding value.</param>
    public sealed record Registered(ulong Revision);

    /// <summary>
    /// The registry holds the node's entry at a generation at or above the silo's at another address; no attempt of the
    /// registration applies from then on (OR-I2).
    /// </summary>
    public sealed record Stale
    {
        private Stale()
        {
        }

        /// <summary>Gets the one stale answer.</summary>
        public static Stale Instance { get; } = new();
    }

    /// <summary>
    /// The registry records another archive store than the silo's, in kind or ID; no attempt of the registration applies
    /// (OR-I20).
    /// </summary>
    public sealed record WrongArchiveStore
    {
        private WrongArchiveStore()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static WrongArchiveStore Instance { get; } = new();
    }

    /// <summary>
    /// The registry records another object-ledger store ID than the silo's; no attempt of the registration applies
    /// (orleans.md §5.14, OR-I20).
    /// </summary>
    public sealed record WrongObjectLedgerStore
    {
        private WrongObjectLedgerStore()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static WrongObjectLedgerStore Instance { get; } = new();
    }

    /// <summary>
    /// The registration would make the registry's reserved length exceed the register's value limit, decided against the
    /// value read; no write carried it.
    /// </summary>
    public sealed record Refused
    {
        private Refused()
        {
        }

        /// <summary>Gets the one refusal.</summary>
        public static Refused Instance { get; } = new();
    }

    /// <summary>
    /// The registration's ordinal belongs to another node's active entry, which the registration does not name for
    /// replacement, decided against the value read; no write carried it. It holds while that entry stays active.
    /// </summary>
    /// <param name="Ordinal">The registration's ordinal.</param>
    /// <param name="Occupant">The node whose active entry holds the ordinal.</param>
    /// <param name="HasEntry">
    /// Whether the registering node already has an active entry: a node whose volume is bound to another ordinal's pod,
    /// rather than a new node.
    /// </param>
    /// <param name="EntryOrdinal">The ordinal of the registering node's active entry, when it has one with an ordinal.</param>
    public sealed record OrdinalOccupied(int Ordinal, NodeId Occupant, bool HasEntry, int? EntryOrdinal);

    /// <inheritdoc/>
    public bool Equals(NodeRegistrationOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is NodeRegistrationOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
