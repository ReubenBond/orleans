namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A journal's history read could not reach the quorum of its key's ledger (journals.md §6): no decision reached its
/// quorum, and a repetition may succeed once a replica recovers. The inner exception is the <c>Ledger</c>'s
/// <see cref="AggregateException"/> of its replicas' failures (ledger-protocol.md §6.4). Unavailability, which asserts
/// nothing about the journal (outcomes.md §8): a read had no effect, and a write which fails with it was never proposed,
/// so it had none either (journals.md §2.2).
/// </summary>
public sealed class JournalStoreUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="JournalStoreUnavailableException"/> class.</summary>
    public JournalStoreUnavailableException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JournalStoreUnavailableException"/> class.</summary>
    public JournalStoreUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JournalStoreUnavailableException"/> class.</summary>
    public JournalStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
