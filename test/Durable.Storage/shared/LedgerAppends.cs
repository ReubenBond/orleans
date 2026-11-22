using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Appends a payload through a <see cref="LedgerWriter"/> in one call, as the ledger protocol's tests write them: it
/// acquires the next slot and appends at it (ledger-protocol.md §2.3, §2.4).
/// </summary>
internal static class LedgerAppends
{
    /// <summary>
    /// Acquires the next slot of <paramref name="writer"/> and appends <paramref name="payload"/> at it: an acquisition
    /// after the lifetime stopped answers <see cref="AppendOutcome.Unresolved"/> with no slot.
    /// </summary>
    /// <exception cref="ArgumentException">The payload is empty; nothing was acquired.</exception>
    public static async ValueTask<AppendOutcome> AppendAsync(this LedgerWriter writer, ArcBuffer payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (payload.Length == 0)
        {
            throw new ArgumentException("A zero-length batch cannot be appended.", nameof(payload));
        }

        return await writer.AcquireAsync(cancellationToken) is { } slot
            ? await writer.AppendAsync(slot, payload, cancellationToken)
            : new AppendOutcome(new AppendOutcome.Unresolved(Slot: null));
    }
}
