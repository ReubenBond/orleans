using System.Runtime.CompilerServices;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The outcome of an append (ledger-protocol.md §2.4): <see cref="Appended"/>, <see cref="Sealed"/>, or
/// <see cref="Unresolved"/>.
/// </summary>
/// <remarks>
/// A non-boxing union written by hand, since every append returns one (conventions.md §15): it stores its case and one
/// slot, and builds the case when it is read. Two outcomes are equal when they hold the same case with the same slot, so
/// <c>Appended(p)</c> and <c>Sealed(p)</c> differ.
/// </remarks>
[Union]
internal readonly struct AppendOutcome : IUnion, IEquatable<AppendOutcome>
{
    private readonly Slot _slot;
    private readonly Kind _kind;

    public AppendOutcome(Appended value)
    {
        _slot = value.Slot;
        _kind = Kind.Appended;
    }

    public AppendOutcome(Sealed value)
    {
        _slot = value.Slot;
        _kind = Kind.Sealed;
    }

    public AppendOutcome(Unresolved value)
    {
        _slot = value.Slot ?? default;
        _kind = value.Slot is null ? Kind.Unacquired : Kind.Unresolved;
    }

    private enum Kind : byte
    {
        None,
        Appended,
        Sealed,
        Unresolved,

        // Unresolved with no slot: the append acquired none.
        Unacquired,
    }

    /// <inheritdoc/>
    public object? Value => _kind switch
    {
        Kind.Appended => new Appended(_slot),
        Kind.Sealed => new Sealed(_slot),
        Kind.Unresolved or Kind.Unacquired => ToUnresolved(),
        Kind.None => null,
        _ => throw new InvalidOperationException("An append outcome holds an undefined case."),
    };

    public bool HasValue => _kind != Kind.None;

    public static bool operator ==(AppendOutcome left, AppendOutcome right) => left.Equals(right);

    public static bool operator !=(AppendOutcome left, AppendOutcome right) => !left.Equals(right);

    public bool TryGetValue(out Appended value)
    {
        value = _kind == Kind.Appended ? new Appended(_slot) : default;
        return _kind == Kind.Appended;
    }

    public bool TryGetValue(out Sealed value)
    {
        value = _kind == Kind.Sealed ? new Sealed(_slot) : default;
        return _kind == Kind.Sealed;
    }

    public bool TryGetValue(out Unresolved value)
    {
        var unresolved = _kind is Kind.Unresolved or Kind.Unacquired;
        value = unresolved ? ToUnresolved() : default;
        return unresolved;
    }

    /// <inheritdoc/>
    public bool Equals(AppendOutcome other) => _kind == other._kind && _slot == other._slot;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AppendOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_kind, _slot);

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    private Unresolved ToUnresolved() => new(_kind == Kind.Unacquired ? null : _slot);

    /// <summary>
    /// The batch is durable at <paramref name="Slot"/>: the append linearized when its slot completed, and every lower
    /// slot holds a batch on <c>Q</c> replicas (LP-I3, LP-I17).
    /// </summary>
    /// <param name="Slot">The slot.</param>
    public readonly record struct Appended(Slot Slot);

    /// <summary>
    /// The write at <paramref name="Slot"/> met a sealed replica, or one which answered dropped, before <c>Q</c> answered
    /// written: the slot never completes, the lifetime was stopped, and the write may still be on <c>Q</c> replicas, so
    /// the ledger map's recorded <c>T</c> decides it (LP-I8).
    /// </summary>
    /// <param name="Slot">The slot.</param>
    public readonly record struct Sealed(Slot Slot);

    /// <summary>
    /// The append's slot could not be acquired (<paramref name="Slot"/> is <see langword="null"/>: nothing was written);
    /// or its write at <paramref name="Slot"/> failed or was abandoned once it began, so the batch may be on some replicas
    /// and a recovery may copy it below the recovered <c>T</c>; or its batch is on <c>Q</c> replicas but its completion
    /// failed. The lifetime was stopped, so the recovered <c>T</c> decides the append (LP-I13).
    /// </summary>
    /// <param name="Slot">The append's slot, or <see langword="null"/> when it acquired none.</param>
    public readonly record struct Unresolved(Slot? Slot);
}
