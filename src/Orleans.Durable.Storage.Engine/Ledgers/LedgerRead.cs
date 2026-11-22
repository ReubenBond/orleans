using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The outcome of a reader's read of one decided slot (ledgers.md §2.5): <see cref="Data"/>,
/// <see cref="ReplicaWriteOutcome.Dropped"/>, or <see cref="ReplicaWriteOutcome.Backpressure"/>.
/// </summary>
/// <remarks>
/// A non-boxing union written by hand, since every read and every scanned slot returns one (conventions.md §15). Its
/// <see cref="Data"/> case owns the batch's verified bytes, which its caller disposes, so the union has no equality. It
/// shares the replica contract's <c>Dropped</c> and <c>Backpressure</c> cases, as the ledger protocol's answers do.
/// </remarks>
[Union]
public readonly struct LedgerRead : IUnion
{
    private readonly ArcBuffer _batch;
    private readonly Slot _slot;
    private readonly int _count;
    private readonly Kind _kind;

    /// <summary>Initializes a new instance of the <see cref="LedgerRead"/> struct holding <paramref name="value"/>.</summary>
    public LedgerRead(Data value)
    {
        _batch = value.Batch;
        _slot = value.Slot;
        _count = value.Count;
        _kind = Kind.Data;
    }

    /// <summary>Initializes a new instance of the <see cref="LedgerRead"/> struct holding <paramref name="value"/>.</summary>
    public LedgerRead(ReplicaWriteOutcome.Dropped value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _kind = Kind.Dropped;
    }

    /// <summary>Initializes a new instance of the <see cref="LedgerRead"/> struct holding <paramref name="value"/>.</summary>
    public LedgerRead(ReplicaWriteOutcome.Backpressure value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _kind = Kind.Backpressure;
    }

    private enum Kind : byte
    {
        None,
        Data,
        Dropped,
        Backpressure,
    }

    /// <inheritdoc/>
    public object? Value => _kind switch
    {
        Kind.Data => new Data(_slot, _batch, _count),
        Kind.Dropped => ReplicaWriteOutcome.Dropped.Instance,
        Kind.Backpressure => ReplicaWriteOutcome.Backpressure.Instance,
        Kind.None => null,
        _ => throw new InvalidOperationException("A ledger read holds an undefined case."),
    };

    /// <summary>Gets a value indicating whether the read holds a case.</summary>
    public bool HasValue => _kind != Kind.None;

    /// <summary>Gets the <see cref="Data"/> case, when the read holds it.</summary>
    public bool TryGetValue(out Data value)
    {
        value = _kind == Kind.Data ? new Data(_slot, _batch, _count) : default;
        return _kind == Kind.Data;
    }

    /// <summary>Gets the <see cref="ReplicaWriteOutcome.Dropped"/> case, when the read holds it.</summary>
    public bool TryGetValue([NotNullWhen(true)] out ReplicaWriteOutcome.Dropped? value)
    {
        value = _kind == Kind.Dropped ? ReplicaWriteOutcome.Dropped.Instance : null;
        return value is not null;
    }

    /// <summary>Gets the <see cref="ReplicaWriteOutcome.Backpressure"/> case, when the read holds it.</summary>
    public bool TryGetValue([NotNullWhen(true)] out ReplicaWriteOutcome.Backpressure? value)
    {
        value = _kind == Kind.Backpressure ? ReplicaWriteOutcome.Backpressure.Instance : null;
        return value is not null;
    }

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>
    /// The batch at <see cref="Slot"/>, whose verified bytes this case owns and whose rows it lends (ledgers.md §2.5, §4).
    /// </summary>
    /// <remarks>
    /// Its caller disposes it once it has finished with <see cref="View"/> and the rows it lends: a read through them
    /// afterwards is a use after release (BUF-I4).
    /// </remarks>
    public readonly struct Data : IDisposable
    {
        internal Data(Slot slot, ArcBuffer batch, int count)
        {
            Slot = slot;
            Batch = batch;
            Count = count;
        }

        /// <summary>Gets the batch's slot.</summary>
        public Slot Slot { get; }

        /// <summary>Gets the batch's verified bytes, which this case owns.</summary>
        public ArcBuffer Batch { get; }

        /// <summary>Gets the batch's row count.</summary>
        public int Count { get; }

        /// <summary>Gets a view of the batch's rows, over <see cref="Batch"/>.</summary>
        public BatchView View => BatchView.OfVerified(Batch);

        /// <summary>Releases the batch's bytes.</summary>
        public void Dispose() => Batch.Dispose();

        /// <inheritdoc/>
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Data {{ Slot = {Slot}, Count = {Count} }}");
    }
}
