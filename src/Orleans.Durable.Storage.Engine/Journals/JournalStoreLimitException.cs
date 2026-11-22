namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A <see cref="JournalStore"/>'s startup check failed (journals.md §8.4): the bound <see cref="Limit"/> names does not
/// hold, so the process refuses to start. A composition's start check is constructing the store.
/// </summary>
public sealed class JournalStoreLimitException : ArgumentException
{
    /// <summary>Initializes a new instance of the <see cref="JournalStoreLimitException"/> class.</summary>
    public JournalStoreLimitException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JournalStoreLimitException"/> class.</summary>
    public JournalStoreLimitException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JournalStoreLimitException"/> class.</summary>
    public JournalStoreLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JournalStoreLimitException"/> class for the bound <paramref name="limit"/>.</summary>
    /// <param name="limit">The bound which does not hold.</param>
    /// <param name="message">What the check found.</param>
    /// <param name="paramName">The store's constructor parameter which carries the setting.</param>
    public JournalStoreLimitException(JournalStoreLimit limit, string message, string paramName)
        : base(message, paramName)
    {
        Limit = limit;
    }

    /// <summary>Gets the bound which does not hold, or <see langword="null"/> when the exception names none.</summary>
    public JournalStoreLimit? Limit { get; }
}
