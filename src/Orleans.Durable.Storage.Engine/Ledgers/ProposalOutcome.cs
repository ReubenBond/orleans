using System.Runtime.CompilerServices;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The outcome of a proposal (ledgers.md §2.4): <see cref="Durable"/>, <see cref="Fenced"/>, or
/// <see cref="Ambiguous"/>.
/// </summary>
/// <remarks>
/// A non-boxing union written by hand, since every proposal returns one (conventions.md §15): it stores its case and one
/// slot, and builds the case when it is read. Two outcomes are equal when they hold the same case, with the same slot for
/// <see cref="Durable"/>.
/// </remarks>
[Union]
public readonly struct ProposalOutcome : IUnion, IEquatable<ProposalOutcome>
{
    private readonly Slot _slot;
    private readonly Kind _kind;

    /// <summary>Initializes a new instance of the <see cref="ProposalOutcome"/> struct holding <paramref name="value"/>.</summary>
    public ProposalOutcome(Durable value)
    {
        _slot = value.Slot;
        _kind = Kind.Durable;
    }

    /// <summary>Initializes a new instance of the <see cref="ProposalOutcome"/> struct holding <paramref name="value"/>.</summary>
    public ProposalOutcome(Fenced value)
    {
        _ = value;
        _kind = Kind.Fenced;
    }

    /// <summary>Initializes a new instance of the <see cref="ProposalOutcome"/> struct holding <paramref name="value"/>.</summary>
    public ProposalOutcome(Ambiguous value)
    {
        _ = value;
        _kind = Kind.Ambiguous;
    }

    private enum Kind : byte
    {
        None,
        Durable,
        Fenced,
        Ambiguous,
    }

    /// <inheritdoc/>
    public object? Value => _kind switch
    {
        Kind.Durable => new Durable(_slot),
        Kind.Fenced => default(Fenced),
        Kind.Ambiguous => default(Ambiguous),
        Kind.None => null,
        _ => throw new InvalidOperationException("A proposal outcome holds an undefined case."),
    };

    /// <summary>Gets a value indicating whether the outcome holds a case.</summary>
    public bool HasValue => _kind != Kind.None;

    public static bool operator ==(ProposalOutcome left, ProposalOutcome right) => left.Equals(right);

    public static bool operator !=(ProposalOutcome left, ProposalOutcome right) => !left.Equals(right);

    /// <summary>Gets the <see cref="Durable"/> case, when the outcome holds it.</summary>
    public bool TryGetValue(out Durable value)
    {
        value = _kind == Kind.Durable ? new Durable(_slot) : default;
        return _kind == Kind.Durable;
    }

    /// <summary>Gets the <see cref="Fenced"/> case, when the outcome holds it.</summary>
    public bool TryGetValue(out Fenced value)
    {
        value = default;
        return _kind == Kind.Fenced;
    }

    /// <summary>Gets the <see cref="Ambiguous"/> case, when the outcome holds it.</summary>
    public bool TryGetValue(out Ambiguous value)
    {
        value = default;
        return _kind == Kind.Ambiguous;
    }

    /// <inheritdoc/>
    public bool Equals(ProposalOutcome other) => _kind == other._kind && _slot == other._slot;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ProposalOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_kind, _slot);

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>
    /// The batch is the decided DATA at <paramref name="Slot"/>, and every lower slot is decided (LG-I5).
    /// </summary>
    /// <param name="Slot">The batch's slot.</param>
    public readonly record struct Durable(Slot Slot);

    /// <summary>
    /// The batch is not in the ledger and never will be: the ledger's recorded <c>T</c> is at or below its slot, or it
    /// took no slot.
    /// </summary>
    public readonly record struct Fenced;

    /// <summary>
    /// The ledger left the map before the primary learned its recorded <c>T</c>; the outcome asserts nothing about the
    /// batch (outcomes.md §8).
    /// </summary>
    public readonly record struct Ambiguous;
}
