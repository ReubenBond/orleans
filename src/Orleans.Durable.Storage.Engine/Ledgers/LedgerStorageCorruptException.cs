namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// An object-store ledger's storage holds a value which fails its integrity check, or lacks a batch below a bound which
/// decided it while its ledger is not dropped (object-ledgers.md §6, OL-I7): corruption, never <c>Missing</c> or a batch.
/// </summary>
/// <remarks>
/// With one copy, corruption below a ledger's end blocks its recovery and so its close: a qualification boundary
/// (verification.md §13). The ledger protocol treats this exception as a failure which repetition cannot change.
/// </remarks>
public sealed class LedgerStorageCorruptException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="LedgerStorageCorruptException"/> class.</summary>
    public LedgerStorageCorruptException()
        : base("A ledger's storage holds a corrupt value.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LedgerStorageCorruptException"/> class.</summary>
    public LedgerStorageCorruptException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LedgerStorageCorruptException"/> class.</summary>
    public LedgerStorageCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
