namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>
/// An object-ledger store's record does not match the process (object-ledgers.md §4, OL-I1): it names another layout, or
/// its ID differs from the one the cluster's node registry records, or it is absent while the registry records one. The
/// process does not use the store.
/// </summary>
public sealed class ObjectLedgerStoreMismatchException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ObjectLedgerStoreMismatchException"/> class.</summary>
    public ObjectLedgerStoreMismatchException()
        : base("The object-ledger store's record does not match the process.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ObjectLedgerStoreMismatchException"/> class.</summary>
    public ObjectLedgerStoreMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ObjectLedgerStoreMismatchException"/> class.</summary>
    public ObjectLedgerStoreMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
