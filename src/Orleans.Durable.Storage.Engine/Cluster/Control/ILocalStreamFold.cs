using System.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// Per-ledger fold state opened by <see cref="ILocalStreamProcessor.BeginClosedLedgerFold"/> to fold and
/// idempotently commit one ledger-local stream's records (LG-I16).
/// </summary>
public interface ILocalStreamFold : IDisposable
{
    /// <summary>Gets the live approximate memory in bytes currently retained by this fold.</summary>
    long MemoryBytes { get; }

    /// <summary>Folds one <see cref="RowKind.LocalAppend"/> row's borrowed payload (valid only for the duration of the call).</summary>
    /// <param name="slot">The ledger slot containing the row.</param>
    /// <param name="rowIndex">The 0-based index of the row within the batch at <paramref name="slot"/>.</param>
    /// <param name="payload">The borrowed row payload bytes.</param>
    void Add(Slot slot, int rowIndex, ReadOnlySequence<byte> payload);

    /// <summary>Discards all folded state after a chunked flush so subsequent records start from zero retained memory.</summary>
    void Clear();

    /// <summary>Idempotently commits accumulated folded state (may be called multiple times for contiguous chunked flushes).</summary>
    /// <param name="cancellationToken">Cancels the commit attempt.</param>
    Task CommitAsync(CancellationToken cancellationToken);
}
