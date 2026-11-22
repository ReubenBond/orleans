namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A journal's history contradicts itself (journals.md §6): an entry record which §7's decoding rejects where it enters
/// the primary; a <c>Delete</c> row whose body is neither empty nor a tombstone; the archive's corruption; or warm state
/// which no longer folds the ledger's decided rows. Stored bytes which are damaged, never reported as missing or absent
/// (outcomes.md §8).
/// </summary>
public sealed class JournalStoreCorruptException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="JournalStoreCorruptException"/> class.</summary>
    public JournalStoreCorruptException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JournalStoreCorruptException"/> class.</summary>
    public JournalStoreCorruptException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JournalStoreCorruptException"/> class.</summary>
    public JournalStoreCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
