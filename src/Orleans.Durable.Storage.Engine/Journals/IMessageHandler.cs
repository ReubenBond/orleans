using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// Data-plane handler for one <see cref="MessageChannel"/> registered with <see cref="JournalStore"/>
/// (journals.md §2.1, §8.1). Validates payloads before admission, opens one isolated
/// <see cref="IMessageSession"/> per open ledger primary, optionally hints ledgers for proactive rolling, and stops
/// cleanly before <see cref="JournalStore"/> disposal.
/// </summary>
public interface IMessageHandler
{
    /// <summary>Gets the message channel owned by this handler.</summary>
    MessageChannel Channel { get; }

    /// <summary>
    /// Synchronously validates <paramref name="payload"/> before journal admission.
    /// </summary>
    /// <param name="payload">The message payload bytes.</param>
    /// <returns>
    /// <see langword="null"/> when valid; otherwise an <see cref="InvalidDataException"/> describing why the
    /// payload is rejected. Admission rejects invalid payloads with <see cref="ArgumentException"/> and zero effect.
    /// </returns>
    InvalidDataException? Validate(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Opens a fresh session bound to the newly claimed open ledger <paramref name="ledger"/>.
    /// </summary>
    IMessageSession OpenLedger(LedgerId ledger);

    /// <summary>
    /// Collects any open ledgers whose in-memory session state exceeded its memory threshold and should be proactively
    /// rolled by the host's control loop.
    /// </summary>
    ImmutableArray<LedgerId> CollectLedgersNeedingRoll() => [];

    /// <summary>
    /// Stops background work (such as timers and in-flight dispatches) owned by this handler during graceful shutdown,
    /// before <see cref="JournalStore"/> disposal.
    /// </summary>
    ValueTask StopAsync() => ValueTask.CompletedTask;
}
