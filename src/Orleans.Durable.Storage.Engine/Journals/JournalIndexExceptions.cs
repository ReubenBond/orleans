namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// The local journal index reported corruption and serves nothing until it is reset and rebuilt from the live ledgers
/// (journals.md §8.3).
/// </summary>
public sealed class JournalIndexCorruptException : IOException
{
    /// <summary>Initializes a new instance with <paramref name="message"/>.</summary>
    public JournalIndexCorruptException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    public JournalIndexCorruptException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Another process or open handle holds the journal index's lock.
/// </summary>
public sealed class JournalIndexLockHeldException : IOException
{
    /// <summary>Initializes a new instance with <paramref name="message"/>.</summary>
    public JournalIndexLockHeldException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    public JournalIndexLockHeldException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
