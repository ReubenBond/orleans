using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers.Control;

/// <summary>
/// A change of the ledger map (ledgers.md §2.6, §4): <see cref="Install"/> closes open ledgers and opens new ones,
/// <see cref="Drop"/> removes one closed ledger, and <see cref="RaiseFloor"/> raises the retirement floor. Each case's
/// constructor throws <see cref="ArgumentException"/> for a shape no map could take.
/// </summary>
public readonly union LedgerMapChange(
    LedgerMapChange.Install,
    LedgerMapChange.Drop,
    LedgerMapChange.RaiseFloor) : IEquatable<LedgerMapChange>
{
    /// <inheritdoc/>
    public bool Equals(LedgerMapChange other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LedgerMapChange other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <summary>
    /// Closes each ledger of <see cref="Closes"/> at the candidate end its executor's recovery returned, and opens each
    /// ledger of <see cref="Opens"/> under consecutive IDs from the map's <c>NextLedgerId</c>, chosen at revision
    /// <see cref="ChosenAt"/> of the composition's node view. A bootstrap closes nothing.
    /// </summary>
    /// <remarks>Equality compares the closes and opens element by element, so identical submissions merge.</remarks>
    public sealed record Install
    {
        /// <summary>Initializes a new instance of the <see cref="Install"/> class.</summary>
        /// <exception cref="ArgumentException">
        /// It opens no ledger, closes one ledger twice, or opens ranges which overlap one another.
        /// </exception>
        public Install(ImmutableArray<LedgerClosing> closes, ImmutableArray<LedgerOpening> opens, ulong chosenAt)
        {
            closes = closes.IsDefault ? [] : closes;
            if (opens.IsDefaultOrEmpty)
            {
                throw new ArgumentException("An install opens at least one ledger.", nameof(opens));
            }

            for (var index = 0; index < closes.Length; index++)
            {
                if (closes[index] == default)
                {
                    throw new ArgumentException("An install closes ledgers by ID.", nameof(closes));
                }

                for (var earlier = 0; earlier < index; earlier++)
                {
                    if (closes[earlier].Id == closes[index].Id)
                    {
                        throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"An install closes ledger {closes[index].Id} twice."), nameof(closes));
                    }
                }
            }

            for (var index = 0; index < opens.Length; index++)
            {
                if (opens[index] == default)
                {
                    throw new ArgumentException("An install opens ledgers with a range, location, window, and primary.", nameof(opens));
                }

                for (var earlier = 0; earlier < index; earlier++)
                {
                    if (opens[earlier].Range.Overlaps(opens[index].Range))
                    {
                        throw new ArgumentException(
                            string.Create(CultureInfo.InvariantCulture, $"An install opens ranges which overlap: {opens[earlier].Range} and {opens[index].Range}."),
                            nameof(opens));
                    }
                }
            }

            Closes = closes;
            Opens = opens;
            ChosenAt = chosenAt;
        }

        /// <summary>Gets the ledgers the install closes, each with the candidate end it records as <c>T</c>.</summary>
        public ImmutableArray<LedgerClosing> Closes { get; }

        /// <summary>Gets the ledgers the install opens, in the order the write assigns their IDs.</summary>
        public ImmutableArray<LedgerOpening> Opens { get; }

        /// <summary>Gets the revision of the composition's node view the install's nodes were chosen from (LG-I11).</summary>
        public ulong ChosenAt { get; }

        /// <inheritdoc/>
        public bool Equals(Install? other) =>
            other is not null && ChosenAt == other.ChosenAt && Closes.AsSpan().SequenceEqual(other.Closes.AsSpan())
            && Opens.AsSpan().SequenceEqual(other.Opens.AsSpan());

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(ChosenAt, Closes.Length, Opens.Length, Opens[0]);
    }

    /// <summary>
    /// Removes <see cref="Ledger"/>, a closed ledger the Archive covers below its recorded tail, once every listed
    /// ledger with a lower ID whose range overlaps it has left (LG-I10).
    /// </summary>
    public sealed record Drop
    {
        /// <summary>Initializes a new instance of the <see cref="Drop"/> class.</summary>
        /// <exception cref="ArgumentException"><paramref name="ledger"/> is the default.</exception>
        public Drop(LedgerId ledger)
        {
            if (ledger == default)
            {
                throw new ArgumentException("A drop names a ledger.", nameof(ledger));
            }

            Ledger = ledger;
        }

        /// <summary>Gets the ledger to remove.</summary>
        public LedgerId Ledger { get; }
    }

    /// <summary>
    /// Raises the retirement floor to <see cref="Revision"/>, the revision of the composition's node view at which a
    /// node was retired (LG-I11).
    /// </summary>
    /// <param name="Revision">The revision the floor rises to.</param>
    public sealed record RaiseFloor(ulong Revision);
}

/// <summary>A ledger an install closes, and the candidate end its executor's recovery returned, recorded as its <c>T</c>.</summary>
public readonly record struct LedgerClosing
{
    /// <summary>Initializes a new instance of the <see cref="LedgerClosing"/> struct.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is the default.</exception>
    public LedgerClosing(LedgerId id, Slot t)
    {
        if (id == default)
        {
            throw new ArgumentException("A closing names a ledger.", nameof(id));
        }

        Id = id;
        T = t;
    }

    /// <summary>Gets the ledger to close.</summary>
    public LedgerId Id { get; }

    /// <summary>Gets the candidate end the close records.</summary>
    public Slot T { get; }
}

/// <summary>
/// A ledger an install opens: its range, location, window, and primary claim, without an ID, which the write assigns.
/// </summary>
/// <remarks>Equality compares locations, whose replicas compare element by element, in order.</remarks>
public readonly record struct LedgerOpening
{
    /// <summary>Initializes a new instance of the <see cref="LedgerOpening"/> struct.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="location"/> or <paramref name="primary"/> is the default.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="k"/> is below 1.</exception>
    public LedgerOpening(KeyRange range, LedgerLocation location, int k, PrimaryClaim primary)
    {
        if (location.Value is null)
        {
            throw new ArgumentException("An opened ledger has a location.", nameof(location));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        if (primary == default)
        {
            throw new ArgumentException("An opened ledger names its primary claim.", nameof(primary));
        }

        Range = range;
        Location = location;
        K = k;
        Primary = primary;
    }

    /// <summary>Initializes a new instance of the <see cref="LedgerOpening"/> struct for a ledger on <paramref name="replicas"/>.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="replicas"/> is not an odd number, at most <see cref="LedgerLocation.MaxReplicas"/>, of distinct
    /// node identities, or <paramref name="primary"/> is the default.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="k"/> is below 1.</exception>
    public LedgerOpening(KeyRange range, ImmutableArray<NodeId> replicas, int k, PrimaryClaim primary)
        : this(range, new LedgerLocation.Nodes(replicas), k, primary)
    {
    }

    /// <summary>Gets the keys the ledger holds.</summary>
    public KeyRange Range { get; }

    /// <summary>Gets where the ledger's storage is: its replicas' nodes, or the object store.</summary>
    public LedgerLocation Location { get; }

    /// <summary>Gets the replicas of a ledger on nodes, in the map's order.</summary>
    /// <exception cref="InvalidOperationException">
    /// The opening is in the object store, which has no replicas: a caller which may meet one decides on
    /// <see cref="Location"/> first (ledgers.md §4).
    /// </exception>
    public ImmutableArray<NodeId> Replicas => Location switch
    {
        LedgerLocation.Nodes nodes => nodes.Replicas,
        LedgerLocation.ObjectStore => throw new InvalidOperationException("An object-store ledger opening has no replicas (ledgers.md §4)."),
        null => throw new UnreachableException("conventions.md §15: a ledger opening's location holds a case."),
    };

    /// <summary>Gets the ledger's window.</summary>
    public int K { get; }

    /// <summary>Gets the ledger's primary claim, which names a fresh sequencer lifetime.</summary>
    public PrimaryClaim Primary { get; }

    /// <inheritdoc/>
    public bool Equals(LedgerOpening other) =>
        Range == other.Range && Location.Equals(other.Location) && K == other.K && Primary == other.Primary;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Range, Location, K, Primary);
}
