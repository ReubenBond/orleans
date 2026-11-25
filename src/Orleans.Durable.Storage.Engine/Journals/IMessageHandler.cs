using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// Data-plane handler for one <see cref="MessageChannel"/> registered with <see cref="JournalStore"/>
/// (journals.md §2.1, §8.1). Validates payloads before admission and opens one isolated
/// <see cref="IMessageSession"/> per open ledger primary.
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
}
