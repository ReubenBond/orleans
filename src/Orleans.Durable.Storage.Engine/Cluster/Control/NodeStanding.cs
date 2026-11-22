using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A node's standing in the node registry and, once retired, its retirement gate (orleans.md §5.8, OR-I4):
/// <see cref="NotRegistered"/>, <see cref="Active"/>, or <see cref="Retired"/>, decided from one registry value and one
/// ledger map value, each read from its register or written by a write which landed.
/// </summary>
/// <remarks>
/// <see cref="NotRegistered"/> and a releasable <see cref="Retired"/> end an operator's polling: the first names an identity
/// the registry never held, the second a disk which may be released. A retired node's disk is releasable once the map's
/// floor covers its retirement and no ledger names it: every install which applies over that value was chosen from a
/// registry value at or above the floor, in which the node is retired, so it names the node in no ledger (LG-I11, OR-I5),
/// and the answer holds after any crash.
/// </remarks>
internal readonly union NodeStanding(NodeStanding.NotRegistered, NodeStanding.Active, NodeStanding.Retired) : IEquatable<NodeStanding>
{
    /// <summary>The registry value holds no entry of the node.</summary>
    public sealed record NotRegistered
    {
        private NotRegistered()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static NotRegistered Instance { get; } = new();
    }

    /// <summary>The registry value holds the node active: a retirement requested is still pending.</summary>
    public sealed record Active
    {
        private Active()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static Active Instance { get; } = new();
    }

    /// <summary>The registry value holds the node retired, and the map value decides its gate.</summary>
    /// <param name="RetiredRevision">The revision of the registry write which retired the node.</param>
    /// <param name="Epoch">The epoch of the map value the gate was decided from.</param>
    /// <param name="Floor">That map value's retirement floor.</param>
    /// <param name="Ledgers">The ledgers that map value lists which name the node, among their replicas or as their
    /// primary, in ascending ID order.</param>
    public sealed record Retired(ulong RetiredRevision, ulong Epoch, ulong Floor, ImmutableArray<LedgerId> Ledgers)
    {
        /// <summary>Gets whether the map's retirement floor covers the node's retirement.</summary>
        public bool FloorCovers => Floor >= RetiredRevision;

        /// <summary>Gets whether the node's disk may be released: the floor covers its retirement, and no ledger names it (OR-I4).</summary>
        public bool IsReleasable => FloorCovers && Ledgers.IsEmpty;

        /// <inheritdoc/>
        public bool Equals(Retired? other) =>
            other is not null && (RetiredRevision, Epoch, Floor) == (other.RetiredRevision, other.Epoch, other.Floor) && Ledgers.SequenceEqual(other.Ledgers);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(RetiredRevision, Epoch, Floor, Ledgers.Length);
    }

    /// <summary>Gets whether the node is retired and its disk may be released (OR-I4).</summary>
    public bool IsReleasable => Value is Retired { IsReleasable: true };

    /// <summary>Decides <paramref name="node"/>'s standing from <paramref name="registry"/> and <paramref name="map"/>.</summary>
    public static NodeStanding Of(NodeId node, RegisteredNodes registry, LedgerMapValue map)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(map);
        if (registry.Find(node) is not { } entry)
        {
            return NotRegistered.Instance;
        }

        if (entry.RetiredRevision is not { } retired)
        {
            return Active.Instance;
        }

        var naming = ImmutableArray.CreateBuilder<LedgerId>();
        foreach (var ledger in map.Ledgers)
        {
            // A node ledger pins the retired node until it leaves the map (as primary or replica, open or closed).
            // An object-store ledger pins it only while open as its primary: retirement hands off its object-store
            // primaries as any others and waits for no drain of a closed one (orleans.md §5.14).
            var pins = ledger.Location.Value is LedgerLocation.Nodes
                ? ledger.Primary.Node == node || ledger.IsOnNode(node)
                : ledger.IsOpen && ledger.Primary.Node == node;
            if (pins)
            {
                naming.Add(ledger.Id);
            }
        }

        return new Retired(retired, map.Epoch, map.RetirementFloor, naming.ToImmutable());
    }

    /// <inheritdoc/>
    public bool Equals(NodeStanding other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is NodeStanding other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
