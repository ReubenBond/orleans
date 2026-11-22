using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A replica's answer to a read (ledger-replicas.md §2.1, conventions.md §15): the owned batch, an
/// <see cref="ArcBuffer"/> the caller disposes; <see cref="Missing"/>; <see cref="ReplicaWriteOutcome.Dropped"/>;
/// <see cref="ReplicaWriteOutcome.Backpressure"/>; or, to a byte-range read alone, <see cref="OutsideBatch"/>.
/// </summary>
/// <remarks>
/// A hand-written union: pattern matching reads each case through its <c>TryGetValue</c>, without boxing. It declares
/// no equality, which belongs to the code which owns the payload.
/// </remarks>
[Union]
public readonly struct ReplicaRead : IUnion
{
    private readonly ArcBuffer _payload;
    private readonly OutsideBatch? _outsideBatch;
    private readonly Kind _kind;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicaRead"/> struct holding an owned batch.
    /// </summary>
    public ReplicaRead(ArcBuffer value)
    {
        _payload = value;
        _kind = Kind.Payload;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicaRead"/> struct holding <see cref="Missing"/>.
    /// </summary>
    public ReplicaRead(Missing value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _kind = Kind.Missing;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicaRead"/> struct holding <see cref="ReplicaWriteOutcome.Dropped"/>.
    /// </summary>
    public ReplicaRead(ReplicaWriteOutcome.Dropped value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _kind = Kind.Dropped;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicaRead"/> struct holding <see cref="ReplicaWriteOutcome.Backpressure"/>.
    /// </summary>
    public ReplicaRead(ReplicaWriteOutcome.Backpressure value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _kind = Kind.Backpressure;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicaRead"/> struct holding <see cref="OutsideBatch"/>.
    /// </summary>
    public ReplicaRead(OutsideBatch value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _outsideBatch = value;
        _kind = Kind.OutsideBatch;
    }

    private enum Kind : byte
    {
        None,
        Payload,
        Missing,
        Dropped,
        Backpressure,
        OutsideBatch,
    }

    /// <inheritdoc/>
    public object? Value => _kind switch
    {
        Kind.Payload => _payload,
        Kind.Missing => Missing.Instance,
        Kind.Dropped => ReplicaWriteOutcome.Dropped.Instance,
        Kind.Backpressure => ReplicaWriteOutcome.Backpressure.Instance,
        Kind.OutsideBatch => _outsideBatch,
        Kind.None => null,
        _ => throw new UnreachableException(),
    };

    /// <summary>
    /// Gets a value indicating whether the answer holds a case.
    /// </summary>
    public bool HasValue => _kind != Kind.None;

    /// <summary>
    /// Returns the owned batch, when the answer holds one.
    /// </summary>
    public bool TryGetValue(out ArcBuffer value)
    {
        value = _payload;
        return _kind == Kind.Payload;
    }

    /// <summary>
    /// Returns <see cref="Missing"/>, when the answer holds it.
    /// </summary>
    public bool TryGetValue([NotNullWhen(true)] out Missing? value)
    {
        value = _kind == Kind.Missing ? Missing.Instance : null;
        return value is not null;
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

    /// <summary>
    /// Returns <see cref="OutsideBatch"/>, when the answer holds it.
    /// </summary>
    public bool TryGetValue([NotNullWhen(true)] out OutsideBatch? value)
    {
        value = _kind == Kind.OutsideBatch ? _outsideBatch : null;
        return value is not null;
    }

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>
    /// The slot holds no batch: it is unwritten, or the node holds no replica of the ledger (ledger-replicas.md §2.1).
    /// </summary>
    public sealed record Missing
    {
        private Missing()
        {
        }

        /// <summary>
        /// Gets the one value.
        /// </summary>
        public static Missing Instance { get; } = new();
    }

    /// <summary>
    /// The slot holds a batch of <paramref name="Length"/> bytes, which the byte range a read asked for extends past: a
    /// value, since the slot's one batch is the same on every replica which holds it (ledger-replicas.md §2.1).
    /// </summary>
    /// <param name="Length">The batch's length, positive.</param>
    public sealed record OutsideBatch(int Length);
}
