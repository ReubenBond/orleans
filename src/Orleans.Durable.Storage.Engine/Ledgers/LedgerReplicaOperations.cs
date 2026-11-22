using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Checks the replica contract's arguments, and implements replica operations in terms of others for providers whose
/// storage has no cheaper form of them (ledger-replicas.md §2.4).
/// </summary>
public static class LedgerReplicaOperations
{
    /// <summary>
    /// Rejects a slot outside <c>0 ..= 2^63 − 2</c>, which no operation accepts (ledger-replicas.md §2.1).
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is <c>2^63 − 1</c>.</exception>
    public static void ValidateSlot(Slot slot) =>
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot.Value, long.MaxValue);

    /// <summary>
    /// Rejects a window <c>K</c> which is not positive.
    /// </summary>
    /// <param name="windowSize">The window.</param>
    /// <exception cref="ArgumentOutOfRangeException">The window is not positive.</exception>
    public static void ValidateWindowSize(int windowSize) =>
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);

    /// <summary>
    /// Rejects an operation whose window differs from the window the replica recorded at its creation, before the
    /// operation has any effect (LR-I6).
    /// </summary>
    /// <param name="replicaWindow">The window the replica recorded at its creation.</param>
    /// <param name="windowSize">The window the operation carries.</param>
    /// <exception cref="ArgumentException">The windows differ.</exception>
    public static void ValidateReplicaWindow(int replicaWindow, int windowSize)
    {
        if (windowSize != replicaWindow)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The request's window {windowSize} differs from the window {replicaWindow} the replica recorded at its creation."),
                nameof(windowSize));
        }
    }

    /// <summary>
    /// Rejects an empty batch: every batch carries at least one byte (ledger-replicas.md §3.2).
    /// </summary>
    /// <param name="payload">The batch.</param>
    /// <exception cref="ArgumentException">The batch is empty.</exception>
    public static void ValidateBatch(ArcBuffer payload)
    {
        if (payload.Length == 0)
        {
            throw new ArgumentException("A batch is non-empty.", nameof(payload));
        }
    }

    /// <summary>
    /// Reads a whole batch and returns bytes <c>[offset, offset + length)</c> of it, <see cref="ReplicaRead.OutsideBatch"/>
    /// when the range extends past it, or the read's other answer (ledger-replicas.md §2.4).
    /// </summary>
    /// <param name="replica">The replica.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="offset">The first byte of the range; at least zero.</param>
    /// <param name="length">The number of bytes in the range; positive.</param>
    /// <param name="contiguousTail">The caller's known contiguous tail.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The range of the batch, which the caller owns, or the read's other answer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative or <paramref name="length"/> not positive.</exception>
    public static async ValueTask<ReplicaRead> ReadRangeOfValueAsync(
        ILedgerReplica replica,
        Slot slot,
        int offset,
        int length,
        Slot contiguousTail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replica);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var read = await replica.ReadAsync(slot, contiguousTail, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out ArcBuffer batch))
        {
            return read;
        }

        using (batch)
        {
            return RangeOf(batch, offset, length);
        }
    }

    /// <summary>
    /// Returns an owned view of bytes <c>[offset, offset + length)</c> of a batch, or
    /// <see cref="ReplicaRead.OutsideBatch"/> when the range extends past it.
    /// </summary>
    /// <param name="value">The batch, which the caller keeps.</param>
    /// <param name="offset">The first byte of the range; at least zero.</param>
    /// <param name="length">The number of bytes in the range; positive.</param>
    /// <returns>A view of the range, which the caller owns, or <see cref="ReplicaRead.OutsideBatch"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative or <paramref name="length"/> not positive.</exception>
    public static ReplicaRead RangeOf(ArcBuffer value, int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return (long)offset + length > value.Length
            ? new ReplicaRead(new ReplicaRead.OutsideBatch(value.Length))
            : new ReplicaRead(value.Slice(offset, length));
    }
}
