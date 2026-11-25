using System.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Control;

/// <summary>
/// Segment-scoped fold for one <see cref="MessageChannel"/> populated during <see cref="DrainScan"/> and committed
/// before the segment's archive completion point (LSM manifest commit or Archive Logs drain marker).
/// </summary>
public interface IMessageFold : IDisposable
{
    /// <summary>Gets the approximate heap bytes currently retained by this fold, charged to the drain memory budget.</summary>
    long MemoryBytes { get; }

    /// <summary>Folds one decided message row from the segment in slot and row order.</summary>
    void Add(Slot slot, int rowIndex, ReadOnlySequence<byte> payload);

    /// <summary>Clears folded state, such as before an alone rescan after budget exhaustion.</summary>
    void Clear();

    /// <summary>
    /// Persists the folded segment state idempotently before the drain marks the segment complete.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);
}
