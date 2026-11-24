using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// Registered once per <see cref="LocalStreamName"/> on <see cref="JournalStore"/> to validate ledger-local appends
/// before admission and open isolated per-ledger primary sessions (journals.md §4.2, JS-I11, JS-I12).
/// </summary>
public interface ILocalStreamHandler
{
    /// <summary>Gets the ledger-local stream name handled by this instance.</summary>
    LocalStreamName Stream { get; }

    /// <summary>
    /// Pure pre-admission payload validator. Must never perform I/O; returns <see langword="null"/> when valid or an
    /// <see cref="InvalidDataException"/> explaining why <paramref name="payload"/> is rejected.
    /// </summary>
    InvalidDataException? Validate(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Opens isolated per-ledger state when <see cref="LedgerAdmission"/> starts serving <paramref name="ledger"/>.
    /// Precondition (LP-I1 / LG-I2): <paramref name="ledger"/> is a fresh primary writer lifetime starting at slot 0.
    /// </summary>
    ILocalStreamSession OpenLedger(LedgerId ledger);
}
