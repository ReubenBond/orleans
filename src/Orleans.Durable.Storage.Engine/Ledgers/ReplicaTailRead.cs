using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A replica's answer to <see cref="ILedgerReplica.ReadTailAsync"/>, a contiguous-tail read or a seal
/// (ledger-replicas.md §2.1, conventions.md §15): the <see cref="LedgerReplicaTail"/>,
/// <see cref="ReplicaWriteOutcome.Dropped"/>, or <see cref="ReplicaWriteOutcome.Backpressure"/>, whose case types it
/// shares with a write's answer.
/// </summary>
/// <remarks>
/// A hand-written union: pattern matching reads each case through its <c>TryGetValue</c>, without boxing, and it holds
/// the tail's fields beside its case in 16 bytes. Two answers are equal when they hold the same case with equal values.
/// </remarks>
[Union]
public readonly struct ReplicaTailRead : IUnion, IEquatable<ReplicaTailRead>
{
    private readonly Slot _contiguousTail;
    private readonly TimeSpan _leaseRemaining;
    private readonly bool _isSealed;
    private readonly Kind _kind;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicaTailRead"/> struct holding a contiguous tail and its seal.
    /// </summary>
    public ReplicaTailRead(LedgerReplicaTail value)
    {
        (_contiguousTail, _isSealed, _leaseRemaining) = value;
        _kind = Kind.Tail;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicaTailRead"/> struct holding <see cref="ReplicaWriteOutcome.Dropped"/>.
    /// </summary>
    public ReplicaTailRead(ReplicaWriteOutcome.Dropped value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _kind = Kind.Dropped;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicaTailRead"/> struct holding <see cref="ReplicaWriteOutcome.Backpressure"/>.
    /// </summary>
    public ReplicaTailRead(ReplicaWriteOutcome.Backpressure value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _kind = Kind.Backpressure;
    }

    private enum Kind : byte
    {
        None,
        Tail,
        Dropped,
        Backpressure,
    }

    /// <inheritdoc/>
    public object? Value => _kind switch
    {
        Kind.Tail => new LedgerReplicaTail(_contiguousTail, _isSealed, _leaseRemaining),
        Kind.Dropped => ReplicaWriteOutcome.Dropped.Instance,
        Kind.Backpressure => ReplicaWriteOutcome.Backpressure.Instance,
        Kind.None => null,
        _ => throw new UnreachableException(),
    };

    /// <summary>
    /// Gets a value indicating whether the answer holds a case.
    /// </summary>
    public bool HasValue => _kind != Kind.None;

    public static bool operator ==(ReplicaTailRead left, ReplicaTailRead right) => left.Equals(right);

    public static bool operator !=(ReplicaTailRead left, ReplicaTailRead right) => !left.Equals(right);

    /// <summary>
    /// Returns the contiguous tail and its seal, when the answer holds them.
    /// </summary>
    public bool TryGetValue(out LedgerReplicaTail value)
    {
        value = new LedgerReplicaTail(_contiguousTail, _isSealed, _leaseRemaining);
        return _kind == Kind.Tail;
    }

    /// <summary>
    /// Returns <see cref="ReplicaWriteOutcome.Dropped"/>, when the answer holds it.
    /// </summary>
    public bool TryGetValue([NotNullWhen(true)] out ReplicaWriteOutcome.Dropped? value)
    {
        value = _kind == Kind.Dropped ? ReplicaWriteOutcome.Dropped.Instance : null;
        return value is not null;
    }

    /// <summary>
    /// Returns <see cref="ReplicaWriteOutcome.Backpressure"/>, when the answer holds it.
    /// </summary>
    public bool TryGetValue([NotNullWhen(true)] out ReplicaWriteOutcome.Backpressure? value)
    {
        value = _kind == Kind.Backpressure ? ReplicaWriteOutcome.Backpressure.Instance : null;
        return value is not null;
    }

    /// <inheritdoc/>
    public bool Equals(ReplicaTailRead other) =>
        _kind == other._kind && _contiguousTail == other._contiguousTail && _isSealed == other._isSealed && _leaseRemaining == other._leaseRemaining;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ReplicaTailRead other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_kind, _contiguousTail, _isSealed, _leaseRemaining);

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;
}
